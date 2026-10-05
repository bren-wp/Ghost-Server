using System.Text;
using GhostServer.Models;
using Renci.SshNet;

namespace GhostServer.Services;

public sealed class TerminalOutputEventArgs(string text) : EventArgs
{
    public string Text { get; } = text;
}

public sealed class InteractiveSshTerminalSession : IDisposable
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _stateSync = new();
    private SshClient? _client;
    private ShellStream? _shellStream;
    private CancellationTokenSource? _readerCancellation;
    private Task? _readerTask;
    private Guid? _profileId;
    private int _disposed;

    public event EventHandler<TerminalOutputEventArgs>? OutputReceived;

    public event EventHandler? Disconnected;

    public bool IsConnected
    {
        get
        {
            lock (_stateSync)
            {
                return _client?.IsConnected == true && _shellStream is not null;
            }
        }
    }

    public Guid? ProfileId
    {
        get
        {
            lock (_stateSync)
            {
                return _profileId;
            }
        }
    }

    public async Task ConnectAsync(
        ServerProfile profile,
        string? secret,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await DisconnectCoreAsync(raiseEvent: false).ConfigureAwait(false);

            var client = SshServerClient.CreateVerifiedClient(profile, secret);
            try
            {
                await Task.Run(
                    () =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        client.Connect();
                        cancellationToken.ThrowIfCancellationRequested();
                    },
                    cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();

                var shellStream = client.CreateShellStream(
                    "dumb",
                    120,
                    32,
                    0,
                    0,
                    16 * 1024);

                var readerCancellation = new CancellationTokenSource();

                lock (_stateSync)
                {
                    _client = client;
                    _shellStream = shellStream;
                    _readerCancellation = readerCancellation;
                    _profileId = profile.Id;
                    _readerTask = Task.Run(
                        () => ReadLoopAsync(shellStream, readerCancellation.Token),
                        CancellationToken.None);
                }
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task SendLineAsync(
        string command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            ShellStream shellStream;
            lock (_stateSync)
            {
                if (_client?.IsConnected != true || _shellStream is null)
                {
                    throw new InvalidOperationException("Interactive terminal is not connected.");
                }

                shellStream = _shellStream;
            }

            var payload = Encoding.UTF8.GetBytes(command + "\n");
            await shellStream.WriteAsync(
                payload.AsMemory(),
                cancellationToken).ConfigureAwait(false);
            await shellStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync(raiseEvent: true).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CancellationTokenSource? cancellation;
        ShellStream? shellStream;
        SshClient? client;

        lock (_stateSync)
        {
            cancellation = _readerCancellation;
            shellStream = _shellStream;
            client = _client;

            _readerCancellation = null;
            _shellStream = null;
            _client = null;
            _readerTask = null;
            _profileId = null;
        }

        cancellation?.Cancel();

        try
        {
            shellStream?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            if (client?.IsConnected == true)
            {
                client.Disconnect();
            }
        }
        catch
        {
            // Window shutdown cleanup is best-effort. Resources are disposed below.
        }

        client?.Dispose();
        cancellation?.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task DisconnectCoreAsync(bool raiseEvent)
    {
        CancellationTokenSource? cancellation;
        ShellStream? shellStream;
        SshClient? client;
        Task? readerTask;
        bool hadSession;

        lock (_stateSync)
        {
            hadSession = _client is not null || _shellStream is not null || _readerTask is not null;
            cancellation = _readerCancellation;
            shellStream = _shellStream;
            client = _client;
            readerTask = _readerTask;

            _readerCancellation = null;
            _shellStream = null;
            _client = null;
            _readerTask = null;
            _profileId = null;
        }

        cancellation?.Cancel();

        try
        {
            shellStream?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            if (client?.IsConnected == true)
            {
                client.Disconnect();
            }
        }
        catch
        {
            // The transport is disposed below even if the remote side disappeared mid-disconnect.
        }
        finally
        {
            client?.Dispose();
        }

        if (readerTask is not null)
        {
            try
            {
                await readerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        cancellation?.Dispose();

        if (raiseEvent && hadSession)
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task ReadLoopAsync(
        ShellStream shellStream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var decoder = Encoding.UTF8.GetDecoder();
        var characters = new char[8192];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await shellStream.ReadAsync(
                    buffer.AsMemory(),
                    cancellationToken).ConfigureAwait(false);

                if (bytesRead == 0)
                {
                    break;
                }

                var characterCount = decoder.GetChars(
                    buffer,
                    0,
                    bytesRead,
                    characters,
                    0,
                    flush: false);

                if (characterCount > 0)
                {
                    OutputReceived?.Invoke(
                        this,
                        new TerminalOutputEventArgs(
                            new string(characters, 0, characterCount)));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            OutputReceived?.Invoke(
                this,
                new TerminalOutputEventArgs(
                    $"{Environment.NewLine}[terminal disconnected] {SafeMessage(ex)}{Environment.NewLine}"));
        }
        finally
        {
            HandleReaderEnded(shellStream);
        }
    }

    private void HandleReaderEnded(ShellStream shellStream)
    {
        CancellationTokenSource? cancellation = null;
        SshClient? client = null;
        var endedCurrentSession = false;

        lock (_stateSync)
        {
            if (ReferenceEquals(_shellStream, shellStream))
            {
                endedCurrentSession = true;
                cancellation = _readerCancellation;
                client = _client;

                _readerCancellation = null;
                _shellStream = null;
                _client = null;
                _readerTask = null;
                _profileId = null;
            }
        }

        if (!endedCurrentSession)
        {
            return;
        }

        try
        {
            shellStream.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        client?.Dispose();
        cancellation?.Dispose();
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }

    private static string SafeMessage(Exception exception)
    {
        var message = exception.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Connection closed.";
        }

        return message.Length > 300 ? message[..300] : message;
    }
}
