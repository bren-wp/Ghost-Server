using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GhostServer.Models;
using GhostServer.Services;
using Microsoft.Win32;

namespace GhostServer;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly ProfileStore _profileStore = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly SshServerClient _ssh = new();
    private AppSettings _settings = new();
    private string? _pendingFingerprint;
    private string? _pendingAlgorithm;
    private ServerProfile? _selectedProfile;
    private ServerProfile? _editingProfile;
    private ServerProfile? _pendingDeleteProfile;
    private readonly List<string> _commandHistory = [];
    private int _commandHistoryIndex;
    private string _rawLogs = string.Empty;
    private Button? _activeNavButton;
    private readonly DispatcherTimer _dashboardTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private int _mutationActive;
    private bool _autoRefreshBusy;

    public ObservableCollection<ServerProfile> Profiles { get; } = [];

    public ServerProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (ReferenceEquals(_selectedProfile, value))
            {
                return;
            }

            _selectedProfile = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _dashboardTimer.Tick += DashboardTimer_Tick;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        AppVersionBadge.Text = version;
        AboutVersionText.Text = $"Ghost Server {version}";
        AppDataPathText.Text = GetAppDataDirectory();

        try
        {
            _settings = await _settingsStore.LoadAsync();
            ApplySettingsToUi();

            var profiles = await _profileStore.LoadAsync();
            foreach (var profile in profiles)
            {
                Profiles.Add(profile);
            }

            if (_settings.LastSelectedServerId is Guid selectedId)
            {
                ServerList.SelectedItem = Profiles.FirstOrDefault(profile => profile.Id == selectedId);
            }

            StatusText.Text = Profiles.Count == 0
                ? "Ready • add your first server"
                : $"Ready • {Profiles.Count} server profile(s)";
            SetActiveNavigation(DashboardNavButton);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Application data load failed";
            TerminalOutput.Text = SafeError(ex);
        }
    }

    private void DashboardNav_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(DashboardNavButton);
        ShowPage(DashboardPage, "Dashboard", "Server health, services and connection state.");
    }

    private async void FilesNav_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(FilesNavButton);
        ShowPage(FilesPage, "Files", "Browse, upload and download files over the verified SFTP connection.");
        await RefreshFilesAsync();
    }

    private async void ServicesNav_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(ServicesNavButton);
        ShowPage(ServicesPage, "Services", "Inspect and control systemd services.");
        await RefreshManagerServicesAsync();
    }

    private async void DockerNav_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(DockerNavButton);
        ShowPage(DockerPage, "Docker", "Inspect and control containers on the selected server.");
        await RefreshDockerAsync();
    }

    private async void LogsNav_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(LogsNavButton);
        ShowPage(LogsPage, "Logs", "Recent server, service or container output.");
        await RefreshLogsAsync();
    }

    private void TerminalNav_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(TerminalNavButton);
        ShowPage(TerminalPage, "Terminal", "Run commands on the currently selected SSH server.");
        UpdateSelectedLabels();
        CommandInput.Focus();
    }

    private async void NetworkNav_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(NetworkNavButton);
        ShowPage(NetworkPage, "Network", "Interfaces, routes, listening sockets and firewall state.");
        await RefreshNetworkAsync();
    }

    private async void UpdatesNav_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(UpdatesNavButton);
        ShowPage(UpdatesPage, "Updates", "Read-only package update discovery for supported Linux distributions.");
        await RefreshUpdatesAsync();
    }

    private void BackupNav_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(BackupNavButton);
        ShowPage(BackupPage, "Backup", "Create and download a temporary configuration snapshot.");
    }

    private void SecurityNav_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(SecurityNavButton);
        ShowPage(SecurityPage, "Security", "Read-only checks for common server security risks.");
    }

    private void SettingsNav_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(SettingsNavButton);
        ShowPage(SettingsPage, "Settings", "Monitoring, profile portability and application information.");
        ApplySettingsToUi();
    }

    private void SetActiveNavigation(Button button)
    {
        foreach (var nav in new[]
                 {
                     DashboardNavButton,
                     FilesNavButton,
                     ServicesNavButton,
                     DockerNavButton,
                     NetworkNavButton,
                     UpdatesNavButton,
                     BackupNavButton,
                     LogsNavButton,
                     TerminalNavButton,
                     SecurityNavButton,
                     SettingsNavButton
                 })
        {
            nav.ClearValue(BackgroundProperty);
            nav.ClearValue(BorderBrushProperty);
        }

        button.Background = (Brush)FindResource("GhostSelectedSurface");
        button.BorderBrush = (Brush)FindResource("GhostBlue");
        _activeNavButton = button;
    }

    private void ShowPage(UIElement page, string title, string subtitle)
    {
        DashboardPage.Visibility = Visibility.Collapsed;
        FilesPage.Visibility = Visibility.Collapsed;
        ServicesPage.Visibility = Visibility.Collapsed;
        DockerPage.Visibility = Visibility.Collapsed;
        NetworkPage.Visibility = Visibility.Collapsed;
        UpdatesPage.Visibility = Visibility.Collapsed;
        BackupPage.Visibility = Visibility.Collapsed;
        LogsPage.Visibility = Visibility.Collapsed;
        TerminalPage.Visibility = Visibility.Collapsed;
        SecurityPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;
        PageTitle.Text = title;
        PageSubtitle.Text = subtitle;
    }

    private async void ServerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedProfile = ServerList.SelectedItem as ServerProfile;
        _pendingFingerprint = null;
        _pendingAlgorithm = null;
        HostKeyPanel.Visibility = Visibility.Collapsed;
        SessionSecretBox.Clear();

        if (SelectedProfile is null)
        {
            _settings.LastSelectedServerId = null;
            await PersistSettingsQuietlyAsync();
            EmptyState.Visibility = Visibility.Visible;
            ServerDetail.Visibility = Visibility.Collapsed;
            return;
        }

        _settings.LastSelectedServerId = SelectedProfile.Id;
        await PersistSettingsQuietlyAsync();

        EmptyState.Visibility = Visibility.Collapsed;
        ServerDetail.Visibility = Visibility.Visible;
        SelectedName.Text = SelectedProfile.Name;
        SelectedEndpoint.Text = $"{SelectedProfile.Username}@{SelectedProfile.Endpoint}";
        ConnectionStatus.Text = string.IsNullOrWhiteSpace(SelectedProfile.HostKeyFingerprint)
            ? "Host key not approved"
            : "Ready to connect";
        ResetMetrics();
        ServicesList.ItemsSource = null;
        ServicesManagerList.ItemsSource = null;
        DockerList.ItemsSource = null;
        RemoteFilesList.ItemsSource = null;
        RemotePathBox.Text = "/";
        FilesStatusText.Text = string.Empty;
        _rawLogs = string.Empty;
        LogsFilterBox.Clear();
        LogsOutput.Clear();
        SelectedServiceText.Text = "Select a service.";
        SelectedDockerText.Text = "Select a Docker container.";
        NetworkOutput.Clear();
        UpdatesOutput.Clear();
        BackupOutput.Text = "Ready. Select a server, unlock the session, then create a configuration snapshot.";
        UpdateSelectedLabels();
    }

    private void UpdateSelectedLabels()
    {
        TerminalServerLabel.Text = SelectedProfile is null
            ? "Select a server on Dashboard before running commands."
            : $"Target: {SelectedProfile.Username}@{SelectedProfile.Endpoint}";
    }

    private async void RefreshFiles_Click(object sender, RoutedEventArgs e) =>
        await RefreshFilesAsync();

    private async Task RefreshFilesAsync()
    {
        if (SelectedProfile is null)
        {
            RemoteFilesList.ItemsSource = null;
            FilesStatusText.Text = "Select a server on Dashboard first.";
            return;
        }

        try
        {
            var path = string.IsNullOrWhiteSpace(RemotePathBox.Text) ? "/" : RemotePathBox.Text.Trim();
            FilesStatusText.Text = "Loading…";
            StatusText.Text = "Loading remote files…";

            var files = await _ssh.GetRemoteFilesAsync(
                SelectedProfile,
                SessionSecretBox.Password,
                path);

            RemoteFilesList.ItemsSource = files;
            RemotePathBox.Text = NormalizeUiRemotePath(path);
            FilesStatusText.Text = $"{files.Count} item(s)";
            StatusText.Text = "Remote files refreshed";
        }
        catch (Exception ex)
        {
            RemoteFilesList.ItemsSource = null;
            FilesStatusText.Text = SafeError(ex);
            StatusText.Text = "Remote file refresh failed";
        }
    }

    private async void RemoteFiles_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RemoteFilesList.SelectedItem is not RemoteFileItem item)
        {
            return;
        }

        if (item.IsDirectory)
        {
            RemotePathBox.Text = item.FullPath;
            await RefreshFilesAsync();
            return;
        }

        await DownloadSelectedRemoteFileAsync(item);
    }

    private async void RemoteUp_Click(object sender, RoutedEventArgs e)
    {
        var current = NormalizeUiRemotePath(RemotePathBox.Text);
        if (current == "/")
        {
            return;
        }

        var trimmed = current.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        RemotePathBox.Text = index <= 0 ? "/" : trimmed[..index];
        await RefreshFilesAsync();
    }

    private async void RemotePathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await RefreshFilesAsync();
    }

    private async void UploadFile_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null)
        {
            FilesStatusText.Text = "Select a server first.";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Upload file to Ghost Server",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            FilesStatusText.Text = $"Uploading {Path.GetFileName(dialog.FileName)}…";
            await _ssh.UploadFileAsync(
                SelectedProfile,
                SessionSecretBox.Password,
                dialog.FileName,
                RemotePathBox.Text);
            StatusText.Text = $"Uploaded {Path.GetFileName(dialog.FileName)}";
            await RefreshFilesAsync();
        }
        catch (Exception ex)
        {
            FilesStatusText.Text = SafeError(ex);
            StatusText.Text = "Upload failed";
        }
    }

    private async void DownloadFile_Click(object sender, RoutedEventArgs e)
    {
        if (RemoteFilesList.SelectedItem is not RemoteFileItem item)
        {
            FilesStatusText.Text = "Select a file to download.";
            return;
        }

        if (item.IsDirectory)
        {
            FilesStatusText.Text = "Select a file, not a folder.";
            return;
        }

        await DownloadSelectedRemoteFileAsync(item);
    }

    private async Task DownloadSelectedRemoteFileAsync(RemoteFileItem item)
    {
        if (SelectedProfile is null)
        {
            FilesStatusText.Text = "Select a server first.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Download file from Ghost Server",
            FileName = item.Name,
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            FilesStatusText.Text = $"Downloading {item.Name}…";
            await _ssh.DownloadFileAsync(
                SelectedProfile,
                SessionSecretBox.Password,
                item.FullPath,
                dialog.FileName);
            FilesStatusText.Text = $"Downloaded {item.Name}";
            StatusText.Text = $"Downloaded {item.Name}";
        }
        catch (Exception ex)
        {
            FilesStatusText.Text = SafeError(ex);
            StatusText.Text = "Download failed";
        }
    }

    private static string NormalizeUiRemotePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var normalized = path.Replace('\\', '/').Trim();
        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        return normalized;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null)
        {
            return;
        }

        SetBusy("Connecting…");
        HostKeyPanel.Visibility = Visibility.Collapsed;

        try
        {
            if (string.IsNullOrWhiteSpace(SelectedProfile.HostKeyFingerprint))
            {
                var probe = await _ssh.ProbeAsync(SelectedProfile, SessionSecretBox.Password);
                if (probe.RequiresTrust && !string.IsNullOrWhiteSpace(probe.PresentedFingerprint))
                {
                    _pendingFingerprint = probe.PresentedFingerprint;
                    _pendingAlgorithm = probe.PresentedHostKeyAlgorithm;
                    HostKeyFingerprint.Text = "SHA256:" + _pendingFingerprint;
                    HostKeyAlgorithm.Text = $"Algorithm: {_pendingAlgorithm ?? "unknown"}";
                    HostKeyPanel.Visibility = Visibility.Visible;
                    ConnectionStatus.Text = "Waiting for host key approval";
                    StatusText.Text = "Connection paused for host-key verification";
                    return;
                }
            }

            var snapshot = await _ssh.GetSnapshotAsync(SelectedProfile, SessionSecretBox.Password);
            ApplySnapshot(snapshot);
            SelectedProfile.LastConnectedUtc = DateTimeOffset.UtcNow;
            await _profileStore.SaveAsync(Profiles);
            ConnectionStatus.Text = "Connected";
            ConnectionStatus.Foreground = (Brush)FindResource("GhostSuccess");
            StatusText.Text = $"Connected to {SelectedProfile.Name}";
            await RefreshServicesAsync();
        }
        catch (SecurityException ex)
        {
            ConnectionStatus.Text = "Host key rejected";
            ConnectionStatus.Foreground = (Brush)FindResource("GhostDanger");
            StatusText.Text = SafeError(ex);
        }
        catch (Exception ex)
        {
            ConnectionStatus.Text = "Connection failed";
            ConnectionStatus.Foreground = (Brush)FindResource("GhostDanger");
            StatusText.Text = SafeError(ex);
        }
    }

    private async void TrustHostKey_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null || string.IsNullOrWhiteSpace(_pendingFingerprint))
        {
            return;
        }

        SelectedProfile.HostKeyFingerprint = _pendingFingerprint;
        _pendingFingerprint = null;
        _pendingAlgorithm = null;
        HostKeyPanel.Visibility = Visibility.Collapsed;

        try
        {
            await _profileStore.SaveAsync(Profiles);
            StatusText.Text = "Host key pinned. Connecting…";
            Connect_Click(sender, e);
        }
        catch (Exception ex)
        {
            StatusText.Text = SafeError(ex);
        }
    }

    private void RejectHostKey_Click(object sender, RoutedEventArgs e)
    {
        _pendingFingerprint = null;
        _pendingAlgorithm = null;
        HostKeyPanel.Visibility = Visibility.Collapsed;
        ConnectionStatus.Text = "Host key not approved";
        StatusText.Text = "Host key rejected; no connection was made";
    }

    private async void RefreshServices_Click(object sender, RoutedEventArgs e) => await RefreshServicesAsync();

    private async Task RefreshServicesAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        try
        {
            SetBusy("Loading services…");
            ServicesList.ItemsSource = await _ssh.GetRunningServicesAsync(
                SelectedProfile, SessionSecretBox.Password);
            StatusText.Text = "Service list refreshed";
        }
        catch (Exception ex)
        {
            StatusText.Text = SafeError(ex);
        }
    }

    private async void RefreshManagerServices_Click(object sender, RoutedEventArgs e) =>
        await RefreshManagerServicesAsync();

    private async Task RefreshManagerServicesAsync()
    {
        if (SelectedProfile is null)
        {
            ServicesManagerList.ItemsSource = null;
            SelectedServiceText.Text = "Select a server on Dashboard first.";
            return;
        }

        try
        {
            StatusText.Text = "Loading services…";
            ServicesManagerList.ItemsSource = await _ssh.GetServicesAsync(
                SelectedProfile, SessionSecretBox.Password);
            SelectedServiceText.Text = "Select a service to manage it.";
            StatusText.Text = "Services refreshed";
        }
        catch (Exception ex)
        {
            ServicesManagerList.ItemsSource = null;
            SelectedServiceText.Text = SafeError(ex);
            StatusText.Text = "Service refresh failed";
        }
    }

    private void ServicesManagerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedServiceText.Text = ServicesManagerList.SelectedItem is ServiceStatus service
            ? $"{service.Name} • {service.State}"
            : "Select a service.";
    }

    private async void StartService_Click(object sender, RoutedEventArgs e) =>
        await RunServiceActionAsync("start");

    private async void StopService_Click(object sender, RoutedEventArgs e) =>
        await RunServiceActionAsync("stop");

    private async void RestartService_Click(object sender, RoutedEventArgs e) =>
        await RunServiceActionAsync("restart");

    private async Task RunServiceActionAsync(string action)
    {
        if (SelectedProfile is null)
        {
            SelectedServiceText.Text = "Select a server on Dashboard first.";
            return;
        }

        if (ServicesManagerList.SelectedItem is not ServiceStatus service)
        {
            SelectedServiceText.Text = "Select a service first.";
            return;
        }

        if (action is "stop" or "restart" &&
            !ConfirmAdministrativeAction(
                $"{char.ToUpperInvariant(action[0])}{action[1..]} service?",
                $"{char.ToUpperInvariant(action[0])}{action[1..]} {service.Name} on {SelectedProfile.Name}?"))
        {
            return;
        }

        if (!TryAcquireMutation($"Preparing service {action}…"))
        {
            return;
        }

        try
        {
            StatusText.Text = $"{char.ToUpperInvariant(action[0])}{action[1..]}ing {service.Name}…";
            var output = await _ssh.ServiceActionAsync(
                SelectedProfile,
                SessionSecretBox.Password,
                service.Name,
                action);
            StatusText.Text = $"{service.Name}: {output}";
            await RefreshManagerServicesAsync();
            await RefreshServicesAsync();
        }
        catch (Exception ex)
        {
            SelectedServiceText.Text = SafeError(ex);
            StatusText.Text = $"Service {action} failed";
        }
        finally
        {
            Interlocked.Exchange(ref _mutationActive, 0);
        }
    }

    private async void ServiceLogs_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null)
        {
            SelectedServiceText.Text = "Select a server first.";
            return;
        }

        if (ServicesManagerList.SelectedItem is not ServiceStatus service)
        {
            SelectedServiceText.Text = "Select a service first.";
            return;
        }

        try
        {
            StatusText.Text = $"Loading logs for {service.Name}…";
            _rawLogs = await _ssh.GetServiceLogsAsync(
                SelectedProfile,
                SessionSecretBox.Password,
                service.Name);
            LogsFilterBox.Clear();
            ApplyLogFilter();
            SetActiveNavigation(LogsNavButton);
            ShowPage(LogsPage, "Logs", $"Recent logs for {service.Name}");
            StatusText.Text = $"Loaded logs for {service.Name}";
        }
        catch (Exception ex)
        {
            SelectedServiceText.Text = SafeError(ex);
            StatusText.Text = "Service log load failed";
        }
    }

    private async void RefreshDocker_Click(object sender, RoutedEventArgs e) =>
        await RefreshDockerAsync();

    private async Task RefreshDockerAsync()
    {
        if (SelectedProfile is null)
        {
            DockerList.ItemsSource = null;
            SelectedDockerText.Text = "Select a server on Dashboard first.";
            return;
        }

        try
        {
            StatusText.Text = "Loading Docker containers…";
            DockerList.ItemsSource = await _ssh.GetDockerContainersAsync(
                SelectedProfile, SessionSecretBox.Password);
            SelectedDockerText.Text = "Select a Docker container.";
            StatusText.Text = "Docker containers refreshed";
        }
        catch (Exception ex)
        {
            DockerList.ItemsSource = null;
            SelectedDockerText.Text = SafeError(ex);
            StatusText.Text = "Docker refresh failed";
        }
    }

    private void DockerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedDockerText.Text = DockerList.SelectedItem is DockerContainerStatus container
            ? $"{container.Name} • {container.State}"
            : "Select a Docker container.";
    }

    private async void StartDocker_Click(object sender, RoutedEventArgs e) =>
        await RunDockerActionAsync("start");

    private async void StopDocker_Click(object sender, RoutedEventArgs e) =>
        await RunDockerActionAsync("stop");

    private async void RestartDocker_Click(object sender, RoutedEventArgs e) =>
        await RunDockerActionAsync("restart");

    private async Task RunDockerActionAsync(string action)
    {
        if (SelectedProfile is null)
        {
            SelectedDockerText.Text = "Select a server on Dashboard first.";
            return;
        }

        if (DockerList.SelectedItem is not DockerContainerStatus container)
        {
            SelectedDockerText.Text = "Select a Docker container first.";
            return;
        }

        if (action is "stop" or "restart" &&
            !ConfirmAdministrativeAction(
                $"{char.ToUpperInvariant(action[0])}{action[1..]} container?",
                $"{char.ToUpperInvariant(action[0])}{action[1..]} Docker container {container.Name} on {SelectedProfile.Name}?"))
        {
            return;
        }

        if (!TryAcquireMutation($"Preparing Docker {action}…"))
        {
            return;
        }

        try
        {
            StatusText.Text = $"Docker {action}: {container.Name}…";
            var output = await _ssh.DockerActionAsync(
                SelectedProfile,
                SessionSecretBox.Password,
                container.Id,
                action);
            StatusText.Text = $"Docker {container.Name}: {output}";
            await RefreshDockerAsync();
        }
        catch (Exception ex)
        {
            SelectedDockerText.Text = SafeError(ex);
            StatusText.Text = $"Docker {action} failed";
        }
        finally
        {
            Interlocked.Exchange(ref _mutationActive, 0);
        }
    }

    private async void DockerLogs_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null)
        {
            SelectedDockerText.Text = "Select a server first.";
            return;
        }

        if (DockerList.SelectedItem is not DockerContainerStatus container)
        {
            SelectedDockerText.Text = "Select a Docker container first.";
            return;
        }

        try
        {
            StatusText.Text = $"Loading logs for {container.Name}…";
            _rawLogs = await _ssh.GetDockerLogsAsync(
                SelectedProfile,
                SessionSecretBox.Password,
                container.Id);
            LogsFilterBox.Clear();
            ApplyLogFilter();
            SetActiveNavigation(LogsNavButton);
            ShowPage(LogsPage, "Logs", $"Recent logs for Docker container {container.Name}");
            StatusText.Text = $"Loaded Docker logs for {container.Name}";
        }
        catch (Exception ex)
        {
            SelectedDockerText.Text = SafeError(ex);
            StatusText.Text = "Docker log load failed";
        }
    }

    private async void RefreshNetwork_Click(object sender, RoutedEventArgs e) =>
        await RefreshNetworkAsync();

    private async Task RefreshNetworkAsync()
    {
        if (SelectedProfile is null)
        {
            NetworkOutput.Text = "Select a server on Dashboard first.";
            return;
        }

        try
        {
            StatusText.Text = "Loading network state…";
            NetworkOutput.Text = await _ssh.GetNetworkOverviewAsync(
                SelectedProfile,
                SessionSecretBox.Password);
            NetworkOutput.ScrollToHome();
            StatusText.Text = "Network state refreshed";
        }
        catch (Exception ex)
        {
            NetworkOutput.Text = SafeError(ex);
            StatusText.Text = "Network refresh failed";
        }
    }

    private async void AllowFirewallPort_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null)
        {
            NetworkOutput.Text = "Select a server on Dashboard first.";
            return;
        }

        if (!int.TryParse(FirewallPortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            NetworkOutput.Text = "Enter a valid port between 1 and 65535.";
            return;
        }

        var protocol = (FirewallProtocolBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "tcp";
        var confirmed = MessageBox.Show(
            this,
            $"Allow inbound {protocol.ToUpperInvariant()} port {port} on {SelectedProfile.Name}?\n\nThis changes the remote firewall and requires passwordless sudo for the connected account.",
            "Confirm firewall change",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirmed != MessageBoxResult.Yes)
        {
            return;
        }

        if (!TryAcquireMutation("Preparing firewall change…"))
        {
            return;
        }

        try
        {
            StatusText.Text = $"Allowing firewall port {port}/{protocol}…";
            var output = await _ssh.AllowFirewallPortAsync(
                SelectedProfile,
                SessionSecretBox.Password,
                port,
                protocol);
            NetworkOutput.Text = output;
            StatusText.Text = $"Firewall rule added: {port}/{protocol}";
            await RefreshNetworkAsync();
        }
        catch (Exception ex)
        {
            NetworkOutput.Text = SafeError(ex);
            StatusText.Text = "Firewall change failed";
        }
        finally
        {
            Interlocked.Exchange(ref _mutationActive, 0);
        }
    }

    private async void RefreshUpdates_Click(object sender, RoutedEventArgs e) =>
        await RefreshUpdatesAsync();

    private async Task RefreshUpdatesAsync()
    {
        if (SelectedProfile is null)
        {
            UpdatesOutput.Text = "Select a server on Dashboard first.";
            return;
        }

        try
        {
            StatusText.Text = "Checking package updates…";
            var output = await _ssh.GetUpdateOverviewAsync(
                SelectedProfile,
                SessionSecretBox.Password);
            UpdatesOutput.Text = string.IsNullOrWhiteSpace(output)
                ? "No pending updates were reported."
                : output;
            UpdatesOutput.ScrollToHome();
            StatusText.Text = "Update check completed";
        }
        catch (Exception ex)
        {
            UpdatesOutput.Text = SafeError(ex);
            StatusText.Text = "Update check failed";
        }
    }

    private async void CreateConfigBackup_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null)
        {
            BackupOutput.Text = "Select a server on Dashboard first.";
            return;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var safeServerName = new string(
            SelectedProfile.Name
                .Select(ch => invalid.Contains(ch) ? '_' : ch)
                .ToArray());

        if (string.IsNullOrWhiteSpace(safeServerName))
        {
            safeServerName = "server";
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save Ghost Server configuration snapshot",
            FileName = $"ghost-server-{safeServerName}-config-{DateTime.Now:yyyyMMdd-HHmmss}.tar.gz",
            Filter = "GZip archive (*.tar.gz)|*.tar.gz|All files|*.*",
            OverwritePrompt = true,
            InitialDirectory = Directory.Exists(_settings.DefaultBackupDirectory)
                ? _settings.DefaultBackupDirectory
                : null
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (!TryAcquireMutation("Preparing configuration snapshot…"))
        {
            return;
        }

        string? remoteArchive = null;
        try
        {
            BackupOutput.Text = "Creating remote configuration snapshot…";
            StatusText.Text = "Creating configuration snapshot…";
            remoteArchive = await _ssh.CreateConfigurationSnapshotAsync(
                SelectedProfile,
                SessionSecretBox.Password);

            BackupOutput.AppendText($"{Environment.NewLine}Remote archive: {remoteArchive}");
            BackupOutput.AppendText($"{Environment.NewLine}Downloading securely over SFTP…");

            await _ssh.DownloadFileAsync(
                SelectedProfile,
                SessionSecretBox.Password,
                remoteArchive,
                dialog.FileName);

            var size = new FileInfo(dialog.FileName).Length;
            BackupOutput.AppendText($"{Environment.NewLine}Saved: {dialog.FileName}");
            BackupOutput.AppendText($"{Environment.NewLine}Size: {size:N0} bytes");
            StatusText.Text = "Configuration snapshot downloaded";
        }
        catch (Exception ex)
        {
            BackupOutput.AppendText($"{Environment.NewLine}[error] {SafeError(ex)}");
            StatusText.Text = "Configuration snapshot failed";
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(remoteArchive))
            {
                try
                {
                    await _ssh.DeleteRemoteFileAsync(
                        SelectedProfile,
                        SessionSecretBox.Password,
                        remoteArchive);
                    BackupOutput.AppendText($"{Environment.NewLine}Temporary remote archive removed.");
                }
                catch (Exception cleanupEx)
                {
                    BackupOutput.AppendText(
                        $"{Environment.NewLine}[cleanup warning] {SafeError(cleanupEx)}");
                }
            }

            BackupOutput.ScrollToEnd();
            Interlocked.Exchange(ref _mutationActive, 0);
        }
    }

    private async void RefreshLogs_Click(object sender, RoutedEventArgs e) =>
        await RefreshLogsAsync();

    private async Task RefreshLogsAsync()
    {
        if (SelectedProfile is null)
        {
            _rawLogs = string.Empty;
            LogsOutput.Text = "Select a server on Dashboard first.";
            return;
        }

        try
        {
            StatusText.Text = "Loading recent logs…";
            _rawLogs = await _ssh.GetRecentLogsAsync(
                SelectedProfile, SessionSecretBox.Password);
            ApplyLogFilter();
            LogsOutput.ScrollToEnd();
            StatusText.Text = "Logs refreshed";
        }
        catch (Exception ex)
        {
            _rawLogs = string.Empty;
            LogsOutput.Text = SafeError(ex);
            StatusText.Text = "Log refresh failed";
        }
    }

    private void LogsFilter_TextChanged(object sender, TextChangedEventArgs e) =>
        ApplyLogFilter();

    private void ApplyLogFilter()
    {
        if (LogsOutput is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_rawLogs))
        {
            LogsOutput.Text = "(no log output)";
            return;
        }

        var filter = LogsFilterBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(filter))
        {
            LogsOutput.Text = _rawLogs;
            return;
        }

        var visible = _rawLogs
            .Split('\n')
            .Where(line => line.Contains(filter, StringComparison.OrdinalIgnoreCase));

        LogsOutput.Text = string.Join(Environment.NewLine, visible);
    }

    private void ApplySnapshot(ServerSnapshot snapshot)
    {
        CpuValue.Text = snapshot.CpuPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        MemoryValue.Text = snapshot.MemoryTotalMb > 0
            ? $"{snapshot.MemoryUsedMb:N0} / {snapshot.MemoryTotalMb:N0} MB"
            : "—";
        DiskValue.Text = snapshot.DiskTotalGb > 0
            ? $"{snapshot.DiskUsedGb:0.0} / {snapshot.DiskTotalGb:0.0} GB"
            : "—";
        LoadValue.Text = snapshot.Load;
        HostnameValue.Text = snapshot.Hostname;
        OsValue.Text = snapshot.OperatingSystem;
        KernelValue.Text = snapshot.Kernel;
        UptimeValue.Text = snapshot.Uptime;
        DockerValue.Text = snapshot.Docker;
    }

    private void ResetMetrics()
    {
        CpuValue.Text = "—";
        MemoryValue.Text = "—";
        DiskValue.Text = "—";
        LoadValue.Text = "—";
        HostnameValue.Text = "—";
        OsValue.Text = "—";
        KernelValue.Text = "—";
        UptimeValue.Text = "—";
        DockerValue.Text = "—";
        ConnectionStatus.Foreground = (Brush)FindResource("GhostMuted");
    }

    private async void RunCommand_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null)
        {
            TerminalOutput.Text = "Select a server on Dashboard first.";
            return;
        }

        var command = CommandInput.Text.Trim();
        if (command.Length == 0)
        {
            return;
        }

        if (_commandHistory.Count == 0 ||
            !string.Equals(_commandHistory[^1], command, StringComparison.Ordinal))
        {
            _commandHistory.Add(command);
            if (_commandHistory.Count > 100)
            {
                _commandHistory.RemoveAt(0);
            }
        }

        _commandHistoryIndex = _commandHistory.Count;

        try
        {
            StatusText.Text = "Running command…";
            var output = await _ssh.RunCommandAsync(SelectedProfile, SessionSecretBox.Password, command);
            TerminalOutput.AppendText($"> {command}{Environment.NewLine}");
            TerminalOutput.AppendText(string.IsNullOrWhiteSpace(output) ? "(no output)" : output);
            TerminalOutput.AppendText(Environment.NewLine + Environment.NewLine);
            TerminalOutput.ScrollToEnd();
            CommandInput.Clear();
            StatusText.Text = "Command completed";
        }
        catch (Exception ex)
        {
            TerminalOutput.AppendText($"> {command}{Environment.NewLine}[error] {SafeError(ex)}{Environment.NewLine}{Environment.NewLine}");
            TerminalOutput.ScrollToEnd();
            StatusText.Text = "Command failed";
        }
    }

    private void CommandInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RunCommand_Click(sender, e);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Up && _commandHistory.Count > 0)
        {
            _commandHistoryIndex = Math.Max(0, _commandHistoryIndex - 1);
            CommandInput.Text = _commandHistory[_commandHistoryIndex];
            CommandInput.CaretIndex = CommandInput.Text.Length;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down && _commandHistory.Count > 0)
        {
            _commandHistoryIndex = Math.Min(_commandHistory.Count, _commandHistoryIndex + 1);
            CommandInput.Text = _commandHistoryIndex >= _commandHistory.Count
                ? string.Empty
                : _commandHistory[_commandHistoryIndex];
            CommandInput.CaretIndex = CommandInput.Text.Length;
            e.Handled = true;
        }
    }

    private void ClearTerminal_Click(object sender, RoutedEventArgs e)
    {
        TerminalOutput.Clear();
        StatusText.Text = "Terminal output cleared";
        CommandInput.Focus();
    }

    private async void SecurityScan_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null)
        {
            SecurityOutput.Text = "Select a server on Dashboard first.";
            return;
        }

        try
        {
            StatusText.Text = "Running read-only security scan…";
            SecurityOutput.Text = await _ssh.RunSecurityScanAsync(
                SelectedProfile, SessionSecretBox.Password);
            StatusText.Text = "Security scan completed";
        }
        catch (Exception ex)
        {
            SecurityOutput.Text = SafeError(ex);
            StatusText.Text = "Security scan failed";
        }
    }

    private void OpenAddServer_Click(object sender, RoutedEventArgs e)
    {
        _editingProfile = null;
        AddServerTitle.Text = "Add server";
        SaveServerButton.Content = "Save server";
        AddError.Visibility = Visibility.Collapsed;
        AddName.Clear();
        AddHost.Clear();
        AddUsername.Clear();
        AddPort.Text = "22";
        AddAuthentication.SelectedIndex = 0;
        AddPrivateKeyPath.Clear();
        AddServerOverlay.Visibility = Visibility.Visible;
        AddName.Focus();
    }

    private void OpenEditServer_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null)
        {
            return;
        }

        _editingProfile = SelectedProfile;
        AddServerTitle.Text = "Edit server";
        SaveServerButton.Content = "Save changes";
        AddError.Visibility = Visibility.Collapsed;
        AddName.Text = SelectedProfile.Name;
        AddHost.Text = SelectedProfile.Host;
        AddUsername.Text = SelectedProfile.Username;
        AddPort.Text = SelectedProfile.Port.ToString(CultureInfo.InvariantCulture);
        AddPrivateKeyPath.Text = SelectedProfile.PrivateKeyPath ?? string.Empty;

        AddAuthentication.SelectedIndex = string.Equals(
            SelectedProfile.Authentication,
            "PrivateKey",
            StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        AddServerOverlay.Visibility = Visibility.Visible;
        AddName.Focus();
        AddName.SelectAll();
    }

    private void CloseAddServer_Click(object sender, RoutedEventArgs e)
    {
        AddServerOverlay.Visibility = Visibility.Collapsed;
        _editingProfile = null;
    }

    private void AddAuthentication_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PrivateKeyPanel is null || AddAuthentication.SelectedItem is not ComboBoxItem selected)
        {
            return;
        }

        PrivateKeyPanel.Visibility = string.Equals(selected.Tag?.ToString(), "PrivateKey", StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void BrowsePrivateKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select SSH private key",
            CheckFileExists = true,
            Multiselect = false,
            Filter = "SSH private keys|id_*;*.pem;*.key;*.ppk|All files|*.*"
        };

        if (dialog.ShowDialog(this) == true)
        {
            AddPrivateKeyPath.Text = dialog.FileName;
        }
    }

    private async void SaveServer_Click(object sender, RoutedEventArgs e)
    {
        AddError.Visibility = Visibility.Collapsed;

        if (string.IsNullOrWhiteSpace(AddName.Text) ||
            string.IsNullOrWhiteSpace(AddHost.Text) ||
            string.IsNullOrWhiteSpace(AddUsername.Text) ||
            !int.TryParse(AddPort.Text, out var port) ||
            port is < 1 or > 65535)
        {
            ShowAddError("Name, host, username and a valid port are required.");
            return;
        }

        var auth = (AddAuthentication.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Password";
        if (auth == "PrivateKey" && string.IsNullOrWhiteSpace(AddPrivateKeyPath.Text))
        {
            ShowAddError("Choose a private key path for private-key authentication.");
            return;
        }

        var name = AddName.Text.Trim();
        var host = AddHost.Text.Trim();
        var username = AddUsername.Text.Trim();
        var keyPath = auth == "PrivateKey" ? AddPrivateKeyPath.Text.Trim() : null;

        if (Profiles.Any(profile =>
                profile.Id != _editingProfile?.Id &&
                string.Equals(profile.Host, host, StringComparison.OrdinalIgnoreCase) &&
                profile.Port == port &&
                string.Equals(profile.Username, username, StringComparison.OrdinalIgnoreCase)))
        {
            ShowAddError("This SSH endpoint and username already exist.");
            return;
        }

        var editedExisting = _editingProfile is not null;
        var oldProfile = _editingProfile;
        var profile = new ServerProfile
        {
            Id = oldProfile?.Id ?? Guid.NewGuid(),
            Name = name,
            Host = host,
            Port = port,
            Username = username,
            Authentication = auth,
            PrivateKeyPath = keyPath,
            HostKeyFingerprint = oldProfile is not null &&
                                 string.Equals(oldProfile.Host, host, StringComparison.OrdinalIgnoreCase) &&
                                 oldProfile.Port == port
                ? oldProfile.HostKeyFingerprint
                : null,
            LastConnectedUtc = oldProfile?.LastConnectedUtc
        };

        var replaceIndex = oldProfile is null ? -1 : Profiles.IndexOf(oldProfile);

        try
        {
            if (editedExisting && replaceIndex >= 0)
            {
                Profiles[replaceIndex] = profile;
            }
            else
            {
                Profiles.Add(profile);
            }

            await _profileStore.SaveAsync(Profiles);
            AddServerOverlay.Visibility = Visibility.Collapsed;
            _editingProfile = null;
            ServerList.SelectedItem = profile;
            ServerList.ScrollIntoView(profile);
            StatusText.Text = editedExisting
                ? $"Updated {profile.Name}."
                : $"Saved {profile.Name}. Approve the SSH host key before first connection.";
        }
        catch (Exception ex)
        {
            if (editedExisting && replaceIndex >= 0 && oldProfile is not null)
            {
                Profiles[replaceIndex] = oldProfile;
                ServerList.SelectedItem = oldProfile;
            }
            else
            {
                Profiles.Remove(profile);
            }

            ShowAddError(SafeError(ex));
        }
    }

    private void DeleteServer_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null)
        {
            return;
        }

        _pendingDeleteProfile = SelectedProfile;
        ConfirmMessage.Text = $"Delete local profile “{SelectedProfile.Name}” ({SelectedProfile.Endpoint})?";
        ConfirmOverlay.Visibility = Visibility.Visible;
    }

    private void CancelDelete_Click(object sender, RoutedEventArgs e)
    {
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        _pendingDeleteProfile = null;
    }

    private async void ConfirmDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingDeleteProfile is null)
        {
            ConfirmOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        var profile = _pendingDeleteProfile;
        var index = Profiles.IndexOf(profile);
        try
        {
            Profiles.Remove(profile);
            await _profileStore.SaveAsync(Profiles);
            ConfirmOverlay.Visibility = Visibility.Collapsed;
            _pendingDeleteProfile = null;
            SelectedProfile = null;
            ServerList.SelectedItem = null;
            SessionSecretBox.Clear();
            EmptyState.Visibility = Visibility.Visible;
            ServerDetail.Visibility = Visibility.Collapsed;
            StatusText.Text = $"Deleted local profile {profile.Name}";
        }
        catch (Exception ex)
        {
            if (!Profiles.Contains(profile))
            {
                if (index >= 0 && index <= Profiles.Count)
                {
                    Profiles.Insert(index, profile);
                }
                else
                {
                    Profiles.Add(profile);
                }
            }

            ConfirmOverlay.Visibility = Visibility.Collapsed;
            _pendingDeleteProfile = null;
            StatusText.Text = SafeError(ex);
        }
    }

    private async void ResetHostKey_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null)
        {
            return;
        }

        SelectedProfile.HostKeyFingerprint = null;
        try
        {
            await _profileStore.SaveAsync(Profiles);
            ConnectionStatus.Text = "Host key not approved";
            ConnectionStatus.Foreground = (Brush)FindResource("GhostWarning");
            HostKeyPanel.Visibility = Visibility.Collapsed;
            StatusText.Text = $"SSH trust reset for {SelectedProfile.Name}";
        }
        catch (Exception ex)
        {
            StatusText.Text = SafeError(ex);
        }
    }

    private void LockSession_Click(object sender, RoutedEventArgs e)
    {
        SessionSecretBox.Clear();
        _dashboardTimer.Stop();
        if (AutoRefreshToggle is not null)
        {
            AutoRefreshToggle.IsChecked = false;
        }

        StatusText.Text = "Session secret cleared from memory";
        ConnectionStatus.Text = SelectedProfile is null ? "Not connected" : "Session locked";
        ConnectionStatus.Foreground = (Brush)FindResource("GhostMuted");
    }

    private void BrowseBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose default Ghost Server backup folder",
            InitialDirectory = Directory.Exists(_settings.DefaultBackupDirectory)
                ? _settings.DefaultBackupDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog(this) == true)
        {
            DefaultBackupFolderBox.Text = dialog.FolderName;
        }
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings.DashboardRefreshSeconds = ReadRefreshInterval();
            _settings.DefaultBackupDirectory = string.IsNullOrWhiteSpace(DefaultBackupFolderBox.Text)
                ? null
                : DefaultBackupFolderBox.Text.Trim();
            _settings.Normalize();

            _dashboardTimer.Interval = TimeSpan.FromSeconds(_settings.DashboardRefreshSeconds);
            await _settingsStore.SaveAsync(_settings);
            SettingsStatusText.Text = "Settings saved.";
            StatusText.Text = "Settings saved";
        }
        catch (Exception ex)
        {
            SettingsStatusText.Text = SafeError(ex);
            StatusText.Text = "Settings save failed";
        }
    }

    private async void ExportProfiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export Ghost Server profiles",
            FileName = $"GhostServer-Profiles-{DateTime.Now:yyyyMMdd}.json",
            Filter = "JSON files (*.json)|*.json|All files|*.*",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await ProfileStore.ExportAsync(dialog.FileName, Profiles);
            SettingsStatusText.Text = $"Exported {Profiles.Count} profile(s). No passwords or passphrases were included.";
            StatusText.Text = "Profiles exported";
        }
        catch (Exception ex)
        {
            SettingsStatusText.Text = SafeError(ex);
            StatusText.Text = "Profile export failed";
        }
    }

    private async void ImportProfiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Ghost Server profiles",
            Filter = "JSON files (*.json)|*.json|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var imported = await ProfileStore.ImportAsync(dialog.FileName);
            var confirmed = MessageBox.Show(
                this,
                $"Import {imported.Count} validated profile(s)? Existing profiles with the same ID or SSH endpoint will be replaced. Session secrets are not imported.",
                "Import server profiles",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (confirmed != MessageBoxResult.Yes)
            {
                return;
            }

            foreach (var incoming in imported)
            {
                var existing = Profiles.FirstOrDefault(profile =>
                    profile.Id == incoming.Id ||
                    (string.Equals(profile.Host, incoming.Host, StringComparison.OrdinalIgnoreCase) &&
                     profile.Port == incoming.Port &&
                     string.Equals(profile.Username, incoming.Username, StringComparison.OrdinalIgnoreCase)));

                if (existing is null)
                {
                    Profiles.Add(incoming);
                    continue;
                }

                var index = Profiles.IndexOf(existing);
                Profiles[index] = incoming;
            }

            await _profileStore.SaveAsync(Profiles);
            SettingsStatusText.Text = $"Imported and validated {imported.Count} profile(s).";
            StatusText.Text = "Profiles imported";
        }
        catch (Exception ex)
        {
            SettingsStatusText.Text = SafeError(ex);
            StatusText.Text = "Profile import failed";
        }
    }

    private void OpenAppData_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = GetAppDataDirectory();
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SettingsStatusText.Text = SafeError(ex);
        }
    }

    private void ApplySettingsToUi()
    {
        _settings.Normalize();
        _dashboardTimer.Interval = TimeSpan.FromSeconds(_settings.DashboardRefreshSeconds);
        DefaultBackupFolderBox.Text = _settings.DefaultBackupDirectory ?? string.Empty;
        AutoRefreshToggle.Content = $"Auto refresh • {_settings.DashboardRefreshSeconds}s";

        var tag = _settings.DashboardRefreshSeconds.ToString(CultureInfo.InvariantCulture);
        foreach (var item in RefreshIntervalBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                RefreshIntervalBox.SelectedItem = item;
                break;
            }
        }
    }

    private int ReadRefreshInterval()
    {
        if (RefreshIntervalBox.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            return seconds;
        }

        return 30;
    }

    private async Task PersistSettingsQuietlyAsync()
    {
        try
        {
            await _settingsStore.SaveAsync(_settings);
        }
        catch
        {
            // Selection persistence is best-effort; explicit Settings save reports failures.
        }
    }

    private static string GetAppDataDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GhostServer");

    private void AutoRefresh_Changed(object sender, RoutedEventArgs e)
    {
        if (AutoRefreshToggle.IsChecked == true)
        {
            _dashboardTimer.Start();
            StatusText.Text = $"Dashboard auto-refresh enabled ({_settings.DashboardRefreshSeconds} seconds)";
        }
        else
        {
            _dashboardTimer.Stop();
            StatusText.Text = "Dashboard auto-refresh disabled";
        }
    }

    private async void DashboardTimer_Tick(object? sender, EventArgs e)
    {
        if (_autoRefreshBusy ||
            SelectedProfile is null ||
            string.IsNullOrWhiteSpace(SelectedProfile.HostKeyFingerprint) ||
            AutoRefreshToggle.IsChecked != true)
        {
            return;
        }

        _autoRefreshBusy = true;
        try
        {
            var snapshot = await _ssh.GetSnapshotAsync(
                SelectedProfile,
                SessionSecretBox.Password);
            ApplySnapshot(snapshot);
            ServicesList.ItemsSource = await _ssh.GetRunningServicesAsync(
                SelectedProfile,
                SessionSecretBox.Password);
            ConnectionStatus.Text = "Connected • auto-refreshed";
            ConnectionStatus.Foreground = (Brush)FindResource("GhostSuccess");
            StatusText.Text = $"Auto-refreshed {SelectedProfile.Name} at {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _dashboardTimer.Stop();
            AutoRefreshToggle.IsChecked = false;
            ConnectionStatus.Text = "Auto-refresh stopped";
            ConnectionStatus.Foreground = (Brush)FindResource("GhostWarning");
            StatusText.Text = SafeError(ex);
        }
        finally
        {
            _autoRefreshBusy = false;
        }
    }

    private bool ConfirmAdministrativeAction(string title, string message)
    {
        return MessageBox.Show(
                   this,
                   message + "\n\nThe action is sent to the selected remote server.",
                   title,
                   MessageBoxButton.YesNo,
                   MessageBoxImage.Warning,
                   MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private bool TryAcquireMutation(string message)
    {
        if (Interlocked.CompareExchange(ref _mutationActive, 1, 0) != 0)
        {
            StatusText.Text = "Another administrative action is already running.";
            return false;
        }

        StatusText.Text = message;
        return true;
    }

    private void ShowAddError(string message)
    {
        AddError.Text = message;
        AddError.Visibility = Visibility.Visible;
    }

    private void SetBusy(string message)
    {
        StatusText.Text = message;
        ConnectionStatus.Foreground = (Brush)FindResource("GhostMuted");
    }

    private static string SafeError(Exception exception)
    {
        var message = exception.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Unexpected error.";
        }

        return message.Length > 500 ? message[..500] : message;
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (ConfirmOverlay.Visibility == Visibility.Visible)
            {
                CancelDelete_Click(sender, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            if (AddServerOverlay.Visibility == Visibility.Visible)
            {
                CloseAddServer_Click(sender, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N)
        {
            OpenAddServer_Click(sender, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.L)
        {
            LockSession_Click(sender, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key != Key.F5)
        {
            return;
        }

        e.Handled = true;

        if (_activeNavButton == FilesNavButton)
        {
            await RefreshFilesAsync();
        }
        else if (_activeNavButton == ServicesNavButton)
        {
            await RefreshManagerServicesAsync();
        }
        else if (_activeNavButton == DockerNavButton)
        {
            await RefreshDockerAsync();
        }
        else if (_activeNavButton == NetworkNavButton)
        {
            await RefreshNetworkAsync();
        }
        else if (_activeNavButton == UpdatesNavButton)
        {
            await RefreshUpdatesAsync();
        }
        else if (_activeNavButton == LogsNavButton)
        {
            await RefreshLogsAsync();
        }
        else if (_activeNavButton == SecurityNavButton)
        {
            SecurityScan_Click(sender, new RoutedEventArgs());
        }
        else if (_activeNavButton == DashboardNavButton)
        {
            Connect_Click(sender, new RoutedEventArgs());
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsInsideButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        DragMove();
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is Button)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
