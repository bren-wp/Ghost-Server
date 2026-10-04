using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GhostServer.Models;
using GhostServer.Services;
using Microsoft.Win32;

namespace GhostServer;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly ProfileStore _profileStore = new();
    private readonly SshServerClient _ssh = new();
    private string? _pendingFingerprint;
    private string? _pendingAlgorithm;
    private ServerProfile? _selectedProfile;
    private ServerProfile? _editingProfile;
    private ServerProfile? _pendingDeleteProfile;
    private readonly List<string> _commandHistory = [];
    private int _commandHistoryIndex;
    private string _rawLogs = string.Empty;
    private Button? _activeNavButton;

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
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var profiles = await _profileStore.LoadAsync();
            foreach (var profile in profiles)
            {
                Profiles.Add(profile);
            }

            StatusText.Text = Profiles.Count == 0
                ? "Ready • add your first server"
                : $"Ready • {Profiles.Count} server profile(s)";
            SetActiveNavigation(DashboardNavButton);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Profile load failed";
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

    private void SecurityNav_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(SecurityNavButton);
        ShowPage(SecurityPage, "Security", "Read-only checks for common server security risks.");
    }

    private void SetActiveNavigation(Button button)
    {
        foreach (var nav in new[]
                 {
                     DashboardNavButton,
                     FilesNavButton,
                     ServicesNavButton,
                     DockerNavButton,
                     LogsNavButton,
                     TerminalNavButton,
                     SecurityNavButton
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
        LogsPage.Visibility = Visibility.Collapsed;
        TerminalPage.Visibility = Visibility.Collapsed;
        SecurityPage.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;
        PageTitle.Text = title;
        PageSubtitle.Text = subtitle;
    }

    private void ServerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedProfile = ServerList.SelectedItem as ServerProfile;
        _pendingFingerprint = null;
        _pendingAlgorithm = null;
        HostKeyPanel.Visibility = Visibility.Collapsed;
        SessionSecretBox.Clear();

        if (SelectedProfile is null)
        {
            EmptyState.Visibility = Visibility.Visible;
            ServerDetail.Visibility = Visibility.Collapsed;
            return;
        }

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
        LogsOutput.Clear();
        SelectedServiceText.Text = "Select a service.";
        SelectedDockerText.Text = "Select a Docker container.";
        UpdateSelectedLabels();
    }

    private void UpdateSelectedLabels()
    {
        TerminalServerLabel.Text = SelectedProfile is null
            ? "Select a server on Dashboard before running commands."
            : $"Target: {SelectedProfile.Username}@{SelectedProfile.Endpoint}";
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
    }

    private async void RefreshLogs_Click(object sender, RoutedEventArgs e) =>
        await RefreshLogsAsync();

    private async Task RefreshLogsAsync()
    {
        if (SelectedProfile is null)
        {
            LogsOutput.Text = "Select a server on Dashboard first.";
            return;
        }

        try
        {
            StatusText.Text = "Loading recent logs…";
            var logs = await _ssh.GetRecentLogsAsync(
                SelectedProfile, SessionSecretBox.Password);
            LogsOutput.Text = string.IsNullOrWhiteSpace(logs) ? "(no log output)" : logs;
            LogsOutput.ScrollToEnd();
            StatusText.Text = "Logs refreshed";
        }
        catch (Exception ex)
        {
            LogsOutput.Text = SafeError(ex);
            StatusText.Text = "Log refresh failed";
        }
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
        }
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

    private void CloseAddServer_Click(object sender, RoutedEventArgs e) =>
        AddServerOverlay.Visibility = Visibility.Collapsed;

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

        if (Profiles.Any(profile =>
                string.Equals(profile.Host, AddHost.Text.Trim(), StringComparison.OrdinalIgnoreCase) &&
                profile.Port == port &&
                string.Equals(profile.Username, AddUsername.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            ShowAddError("This SSH endpoint and username already exist.");
            return;
        }

        var profile = new ServerProfile
        {
            Name = AddName.Text.Trim(),
            Host = AddHost.Text.Trim(),
            Port = port,
            Username = AddUsername.Text.Trim(),
            Authentication = auth,
            PrivateKeyPath = auth == "PrivateKey" ? AddPrivateKeyPath.Text.Trim() : null
        };

        try
        {
            Profiles.Add(profile);
            await _profileStore.SaveAsync(Profiles);
            AddServerOverlay.Visibility = Visibility.Collapsed;
            ServerList.SelectedItem = profile;
            StatusText.Text = $"Saved {profile.Name}. Approve the SSH host key before first connection.";
        }
        catch (Exception ex)
        {
            Profiles.Remove(profile);
            ShowAddError(SafeError(ex));
        }
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
