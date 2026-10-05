using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GhostServer.Models;
using GhostServer.Services;
using Microsoft.Win32;

namespace GhostServer;

public partial class MainWindow : Window, INotifyPropertyChanged, IDisposable
{
    private readonly ProfileStore _profileStore = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly FleetHistoryStore _fleetHistoryStore = new();
    private readonly InteractiveSshTerminalSession _terminalSession = new();
    private AppSettings _settings = new();
    private string? _pendingFingerprint;
    private string? _pendingAlgorithm;
    private ServerProfile? _selectedProfile;
    private ServerProfile? _editingProfile;
    private ServerProfile? _pendingDeleteProfile;
    private readonly List<string> _commandHistory = [];
    private readonly List<FleetServerStatus> _fleetRows = [];
    private readonly List<FleetHealthRecord> _fleetHistory = [];
    private const double FleetAttentionThresholdPercent = 90.0;
    private const int FleetHistoryPerProfileLimit = 100;
    private const int FleetHistoryTotalLimit = 2000;
    private int _commandHistoryIndex;
    private string _rawLogs = string.Empty;
    private readonly DispatcherTimer _dashboardTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _windowSettingsTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private int _mutationActive;
    private bool _autoRefreshBusy;
    private bool _terminalTransitionBusy;
    private CancellationTokenSource? _terminalConnectCancellation;
    private CancellationTokenSource _remoteOperationsCancellation = new();
    private readonly List<CancellationTokenSource> _retiredRemoteCancellations = [];
    private readonly Dictionary<string, int> _activeUiOperations = [];
    private int _remoteOperationGeneration;
    private int _windowDisposed;
    private bool _windowStateCorrection;
    private bool _fitWindowActive;
    private bool _windowSizeSettingsDirty;
    private bool _settingsLoaded;
    private bool _settingsUiUpdate;
    private bool _settingsDirty;
    private bool _settingsSaveBusy;
    private long _settingsEditGeneration;
    private bool _allowCloseAfterSettingsFlush;
    private int _responsiveLayoutSignature = -1;
    private IInputElement? _focusBeforeOverlay;
    private TaskCompletionSource<bool>? _confirmationCompletion;
    private const double StandardWindowWidth = 1180;
    private const double StandardWindowHeight = 760;
    private const double CompactSidebarBreakpoint = 1040;
    private const double TightLayoutBreakpoint = 820;
    private const int TerminalOutputMaxCharacters = 500_000;

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
        _windowSettingsTimer.Tick += WindowSettingsTimer_Tick;
        _terminalSession.OutputReceived += TerminalSession_OutputReceived;
        _terminalSession.Disconnected += TerminalSession_Disconnected;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ConfigureInitialWindowBounds();
        ApplyResponsiveLayout();

        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        AppVersionBadge.Text = version;
        AboutVersionText.Text = $"Ghost Server {version}";
        AppDataPathText.Text = GetAppDataDirectory();

        try
        {
            _settings = await _settingsStore.LoadAsync();
            ApplySettingsToUi();
            ApplySavedWindowSize();
            _settingsLoaded = true;

            var profiles = await _profileStore.LoadAsync();
            foreach (var profile in profiles)
            {
                Profiles.Add(profile);
            }

            var fleetHistory = await _fleetHistoryStore.LoadAsync();
            _fleetHistory.AddRange(fleetHistory);

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

    private void FleetNav_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(FleetNavButton);
        ShowPage(FleetPage, "Fleet", "Review saved servers and run safe read-only health probes.");
        RefreshFleetInventory();
    }

    private void AlertsNav_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(FleetNavButton);
        ShowPage(AlertsPage, "Alerts", "Review and acknowledge local Fleet health incidents.");
        RefreshAlertCenter();
    }

    private void TrendsNav_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(FleetNavButton);
        ShowPage(TrendsPage, "Trends", "Compare recent local Fleet health across saved servers.");
        RefreshTrends();
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
        SetActiveNavigation(SystemNavButton);
        ShowPage(LogsPage, "Logs", "Recent server, service or container output.");
        await RefreshLogsAsync();
    }

    private void TerminalNav_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(TerminalNavButton);
        ShowPage(TerminalPage, "Terminal", "Persistent interactive shell for the currently selected SSH server.");
        UpdateSelectedLabels();
        UpdateTerminalSessionUi();
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
        ShowPage(UpdatesPage, "Safe Update", "Preview, back up, update and verify supported Linux servers.");
        await RefreshUpdatesAsync();
    }

    private void BackupNav_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(UpdatesNavButton);
        ShowPage(BackupPage, "Backup", "Create and download a temporary configuration snapshot.");
    }

    private async void TasksNav_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(UpdatesNavButton);
        ShowPage(TasksPage, "Tasks", "Manage isolated Ghost Server systemd timers and inspect the current user crontab.");
        await RefreshTasksAsync();
    }

    private async void SystemNav_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(SystemNavButton);
        ShowPage(SystemPage, "System", "Processes, filesystems, logged-in users and current host state.");
        await RefreshSystemAsync();
    }

    private async void DatabasesNav_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(SystemNavButton);
        ShowPage(DatabasesPage, "Databases", "Read-only database engine and database-name discovery.");
        await RefreshDatabasesAsync();
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

        if (!_settingsDirty)
        {
            ApplySettingsToUi();
        }
    }

    private void SetActiveNavigation(Button button)
    {
        foreach (var nav in new[]
                 {
                     DashboardNavButton,
                     FleetNavButton,
                     FilesNavButton,
                     ServicesNavButton,
                     DockerNavButton,
                     NetworkNavButton,
                     UpdatesNavButton,
                     SystemNavButton,
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
    }

    private void ShowPage(UIElement page, string title, string subtitle)
    {
        DashboardPage.Visibility = Visibility.Collapsed;
        FleetPage.Visibility = Visibility.Collapsed;
        AlertsPage.Visibility = Visibility.Collapsed;
        TrendsPage.Visibility = Visibility.Collapsed;
        FilesPage.Visibility = Visibility.Collapsed;
        ServicesPage.Visibility = Visibility.Collapsed;
        DockerPage.Visibility = Visibility.Collapsed;
        NetworkPage.Visibility = Visibility.Collapsed;
        UpdatesPage.Visibility = Visibility.Collapsed;
        BackupPage.Visibility = Visibility.Collapsed;
        TasksPage.Visibility = Visibility.Collapsed;
        SystemPage.Visibility = Visibility.Collapsed;
        DatabasesPage.Visibility = Visibility.Collapsed;
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
        var nextProfile = ServerList.SelectedItem as ServerProfile;
        if (SelectedProfile?.Id != nextProfile?.Id)
        {
            CancelRemoteOperations();
            _terminalConnectCancellation?.Cancel();
            await DisconnectTerminalAsync(
                "Terminal disconnected because the selected server changed.",
                appendMessage: false);
            TerminalOutput.Clear();
        }

        SelectedProfile = nextProfile;
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
            UpdateSelectedLabels();
            UpdateTerminalSessionUi();
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
        TasksList.ItemsSource = null;
        TasksStatusText.Text = "Only Ghost Server timers named ghost-server-* are managed here.";
        CrontabOutput.Text = "Current user crontab is shown here for visibility only. Ghost Server does not edit it.";
        ProcessesList.ItemsSource = null;
        SystemStatusText.Text = "Processes, filesystems and signed-in users are loaded read-only.";
        SystemOverviewOutput.Text = "Select a server to inspect memory, filesystems, block devices, logged-in users and load.";
        DatabaseEnginesList.ItemsSource = null;
        DatabasesStatusText.Text = "Select a server, then refresh database engines.";
        DatabaseNamesOutput.Text = "Select a database engine to inspect discovered database names.";
        UpdateSelectedLabels();
        UpdateTerminalSessionUi();
    }

    private void UpdateSelectedLabels()
    {
        TerminalServerLabel.Text = SelectedProfile is null
            ? "Select a server on Dashboard before connecting the interactive shell."
            : $"Target: {SelectedProfile.Username}@{SelectedProfile.Endpoint}";
    }

    private void RefreshFleetInventory_Click(object sender, RoutedEventArgs e) =>
        RefreshFleetInventory();

    private void RefreshFleetInventory()
    {
        _fleetRows.Clear();

        foreach (var profile in Profiles.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase))
        {
            var latest = _fleetHistory
                .Where(record => record.ProfileId == profile.Id)
                .OrderByDescending(record => record.RecordedUtc)
                .FirstOrDefault();

            var trust = string.IsNullOrWhiteSpace(profile.HostKeyFingerprint)
                ? "Not trusted"
                : "Trusted";

            var health = trust != "Trusted"
                ? "Trust required"
                : latest?.Status ??
                  (string.Equals(profile.Authentication, "Password", StringComparison.OrdinalIgnoreCase)
                      ? "Session secret required"
                      : "Ready to probe");

            _fleetRows.Add(new FleetServerStatus
            {
                ProfileId = profile.Id,
                Name = profile.Name,
                Endpoint = profile.Endpoint,
                Username = profile.Username,
                Authentication = string.Equals(profile.Authentication, "PrivateKey", StringComparison.OrdinalIgnoreCase)
                    ? "Private key"
                    : "Password",
                Trust = trust,
                Health = health,
                Load = latest?.Load ?? "—",
                Cpu = latest is null
                    ? "—"
                    : latest.CpuPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%",
                Memory = latest is null || latest.MemoryPercent <= 0
                    ? "—"
                    : latest.MemoryPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%",
                LastConnected = profile.LastConnectedUtc is null
                    ? "Never"
                    : profile.LastConnectedUtc.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            });
        }

        ApplyFleetFilter();
        UpdateFleetSummary();
        FleetStatusText.Text = _fleetRows.Count == 0
            ? "No saved server profiles."
            : "Fleet inventory refreshed. Last known local health is restored where available.";
    }

    private void FleetFilterBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ApplyFleetFilter();

    private void ApplyFleetFilter()
    {
        if (FleetList is null)
        {
            return;
        }

        var selectedId = (FleetList.SelectedItem as FleetServerStatus)?.ProfileId;
        var filter = FleetFilterBox?.Text?.Trim();
        IEnumerable<FleetServerStatus> rows = _fleetRows;

        if (!string.IsNullOrWhiteSpace(filter))
        {
            rows = rows.Where(row =>
                row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                row.Endpoint.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                row.Username.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                row.Health.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                row.OperatingSystem.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        var visible = rows
            .OrderBy(row => FleetHealthRank(row.Health))
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        FleetList.ItemsSource = visible;

        if (selectedId is Guid id)
        {
            FleetList.SelectedItem = visible.FirstOrDefault(row => row.ProfileId == id);
        }

        UpdateFleetHistoryView();
    }

    private async void ProbeFleetKeyProfiles_Click(object sender, RoutedEventArgs e)
    {
        if (_fleetRows.Count == 0)
        {
            RefreshFleetInventory();
        }

        var candidates = Profiles
            .Where(profile =>
                !string.IsNullOrWhiteSpace(profile.HostKeyFingerprint) &&
                string.Equals(profile.Authentication, "PrivateKey", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (candidates.Length == 0)
        {
            FleetStatusText.Text = "No trusted private-key profiles are available for a session-secret-free probe.";
            return;
        }

        FleetStatusText.Text = $"Probing {candidates.Length} trusted private-key profile(s)…";
        StatusText.Text = "Running Fleet health probes…";

        using var concurrency = new SemaphoreSlim(4, 4);
        var tasks = candidates.Select(async profile =>
        {
            await concurrency.WaitAsync();
            try
            {
                var snapshot = await SshServerClient.GetSnapshotAsync(profile, null);
                return (profile.Id, Snapshot: snapshot, Error: (string?)null);
            }
            catch (Exception ex)
            {
                return (profile.Id, Snapshot: (ServerSnapshot?)null, Error: SafeFleetError(ex));
            }
            finally
            {
                concurrency.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        foreach (var result in results)
        {
            var row = _fleetRows.FirstOrDefault(item => item.ProfileId == result.Id);
            if (row is null)
            {
                continue;
            }

            if (result.Snapshot is not null)
            {
                _fleetHistory.Add(ApplyFleetSnapshot(row, result.Snapshot));
            }
            else
            {
                row.Health = result.Error ?? "Probe failed";
                _fleetHistory.Add(CreateFleetFailureRecord(row.ProfileId, row.Health));
            }
        }

        var historySaved = await PersistFleetHistoryAsync();
        ApplyFleetFilter();
        UpdateFleetSummary();

        if (historySaved)
        {
            FleetStatusText.Text = $"Fleet probe finished for {results.Length} profile(s). Password profiles were not contacted.";
            StatusText.Text = "Fleet health probes completed";
        }
        else
        {
            FleetStatusText.Text = $"Fleet probe finished for {results.Length} profile(s), but local health history could not be saved.";
            StatusText.Text = "Fleet probes completed; history save failed";
        }
    }

    private async void ProbeSelectedFleet_Click(object sender, RoutedEventArgs e)
    {
        if (FleetList.SelectedItem is not FleetServerStatus row)
        {
            FleetStatusText.Text = "Select a Fleet row first.";
            return;
        }

        var profile = Profiles.FirstOrDefault(item => item.Id == row.ProfileId);
        if (profile is null)
        {
            FleetStatusText.Text = "The selected profile no longer exists.";
            RefreshFleetInventory();
            return;
        }

        if (string.IsNullOrWhiteSpace(profile.HostKeyFingerprint))
        {
            row.Health = "Trust required";
            FleetStatusText.Text = "Approve this server's SSH host key on Dashboard before probing it.";
            ApplyFleetFilter();
            UpdateFleetSummary();
            return;
        }

        var secret = FleetSecretBox.Password;
        FleetStatusText.Text = $"Probing {profile.Name}…";
        StatusText.Text = $"Fleet probe: {profile.Name}";

        try
        {
            var snapshot = await SshServerClient.GetSnapshotAsync(profile, secret);
            _fleetHistory.Add(ApplyFleetSnapshot(row, snapshot));
            FleetStatusText.Text = row.Health == "Attention"
                ? $"{profile.Name} responded, but local utilization thresholds need attention."
                : $"{profile.Name} responded successfully.";
            StatusText.Text = $"Fleet probe {row.Health.ToLowerInvariant()}: {profile.Name}";
        }
        catch (Exception ex)
        {
            row.Health = SafeFleetError(ex);
            _fleetHistory.Add(CreateFleetFailureRecord(row.ProfileId, row.Health));
            FleetStatusText.Text = $"{profile.Name}: {row.Health}";
            StatusText.Text = $"Fleet probe failed: {profile.Name}";
        }
        finally
        {
            FleetSecretBox.Clear();
        }

        var selectedHistorySaved = await PersistFleetHistoryAsync();
        ApplyFleetFilter();
        UpdateFleetSummary();

        if (!selectedHistorySaved)
        {
            FleetStatusText.Text = $"{profile.Name} probe completed, but local health history could not be saved.";
            StatusText.Text = "Fleet probe completed; history save failed";
        }
    }

    private void FleetList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateFleetHistoryView();

    private void UpdateFleetHistoryView()
    {
        if (FleetHistoryList is null || FleetHistoryTitle is null)
        {
            return;
        }

        if (FleetList?.SelectedItem is not FleetServerStatus row)
        {
            FleetHistoryTitle.Text = "Health history";
            FleetHistoryList.ItemsSource = Array.Empty<FleetHealthRecord>();
            return;
        }

        var history = _fleetHistory
            .Where(record => record.ProfileId == row.ProfileId)
            .OrderByDescending(record => record.RecordedUtc)
            .Take(FleetHistoryPerProfileLimit)
            .ToArray();

        FleetHistoryTitle.Text = $"Health history • {row.Name} • {history.Length} record(s)";
        FleetHistoryList.ItemsSource = history;
    }

    private async void ClearFleetHistory_Click(object sender, RoutedEventArgs e)
    {
        if (FleetList.SelectedItem is not FleetServerStatus row)
        {
            FleetStatusText.Text = "Select a Fleet row first.";
            return;
        }

        var count = _fleetHistory.Count(record => record.ProfileId == row.ProfileId);
        if (count == 0)
        {
            FleetStatusText.Text = $"{row.Name} has no local health history.";
            return;
        }

        if (!await ShowGhostConfirmationAsync(
                "Clear Fleet health history",
                $"Delete {count} local Fleet health record(s) for {row.Name}?\n\nThis does not change the remote server.",
                "Clear history",
                danger: true))
        {
            return;
        }

        var previousHistory = _fleetHistory.ToArray();
        _fleetHistory.RemoveAll(record => record.ProfileId == row.ProfileId);

        if (!await PersistFleetHistoryAsync())
        {
            _fleetHistory.Clear();
            _fleetHistory.AddRange(previousHistory);
            RefreshFleetInventory();
            FleetStatusText.Text = $"Could not clear local Fleet health history for {row.Name}. The previous history was restored.";
            StatusText.Text = "Fleet history clear failed";
            return;
        }

        RefreshFleetInventory();
        FleetStatusText.Text = $"Cleared local Fleet health history for {row.Name}.";
        StatusText.Text = "Fleet history cleared";
    }

    private void OpenFleetServer_Click(object sender, RoutedEventArgs e)
    {
        if (FleetList.SelectedItem is not FleetServerStatus row)
        {
            FleetStatusText.Text = "Select a Fleet row first.";
            return;
        }

        var profile = Profiles.FirstOrDefault(item => item.Id == row.ProfileId);
        if (profile is null)
        {
            FleetStatusText.Text = "The selected profile no longer exists.";
            RefreshFleetInventory();
            return;
        }

        ServerList.SelectedItem = profile;
        ServerList.ScrollIntoView(profile);
        SetActiveNavigation(DashboardNavButton);
        ShowPage(DashboardPage, "Dashboard", "Server health, services and connection state.");
    }

    private static FleetHealthRecord ApplyFleetSnapshot(
        FleetServerStatus row,
        ServerSnapshot snapshot)
    {
        var memoryPercent = snapshot.MemoryTotalMb > 0
            ? snapshot.MemoryUsedMb * 100.0 / snapshot.MemoryTotalMb
            : 0.0;

        var diskPercent = snapshot.DiskTotalGb > 0
            ? snapshot.DiskUsedGb * 100.0 / snapshot.DiskTotalGb
            : 0.0;

        var alerts = new List<string>();

        if (snapshot.CpuPercent >= FleetAttentionThresholdPercent)
        {
            alerts.Add($"CPU {snapshot.CpuPercent:0.0}%");
        }

        if (memoryPercent >= FleetAttentionThresholdPercent)
        {
            alerts.Add($"RAM {memoryPercent:0.0}%");
        }

        if (diskPercent >= FleetAttentionThresholdPercent)
        {
            alerts.Add($"Disk {diskPercent:0.0}%");
        }

        row.Health = alerts.Count == 0 ? "Healthy" : "Attention";
        row.OperatingSystem = snapshot.OperatingSystem;
        row.Uptime = snapshot.Uptime;
        row.Load = snapshot.Load;
        row.Cpu = snapshot.CpuPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        row.Memory = snapshot.MemoryTotalMb > 0
            ? memoryPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%"
            : "—";

        return new FleetHealthRecord
        {
            ProfileId = row.ProfileId,
            RecordedUtc = DateTimeOffset.UtcNow,
            Status = row.Health,
            CpuPercent = snapshot.CpuPercent,
            MemoryPercent = memoryPercent,
            DiskPercent = diskPercent,
            Load = snapshot.Load,
            Message = alerts.Count == 0
                ? "Read-only probe healthy."
                : "Threshold: " + string.Join(" • ", alerts)
        };
    }

    private static FleetHealthRecord CreateFleetFailureRecord(
        Guid profileId,
        string status)
    {
        return new FleetHealthRecord
        {
            ProfileId = profileId,
            RecordedUtc = DateTimeOffset.UtcNow,
            Status = status,
            Message = "Read-only Fleet probe did not complete successfully."
        };
    }

    private async Task<bool> PersistFleetHistoryAsync()
    {
        TrimFleetHistory();

        try
        {
            await _fleetHistoryStore.SaveAsync(_fleetHistory);
            UpdateFleetHistoryView();
            return true;
        }
        catch (Exception ex)
        {
            FleetStatusText.Text = $"Fleet history could not be saved locally: {SafeError(ex)}";
            StatusText.Text = "Fleet history save failed";
            UpdateFleetHistoryView();
            return false;
        }
    }

    private void TrimFleetHistory()
    {
        var trimmed = _fleetHistory
            .GroupBy(record => record.ProfileId)
            .SelectMany(group => group
                .OrderByDescending(record => record.RecordedUtc)
                .Take(FleetHistoryPerProfileLimit))
            .OrderByDescending(record => record.RecordedUtc)
            .Take(FleetHistoryTotalLimit)
            .ToArray();

        _fleetHistory.Clear();
        _fleetHistory.AddRange(trimmed);
    }

    private void UpdateFleetSummary()
    {
        FleetSavedValue.Text = _fleetRows.Count.ToString(CultureInfo.InvariantCulture);
        FleetTrustedValue.Text = _fleetRows.Count(row => row.Trust == "Trusted").ToString(CultureInfo.InvariantCulture);
        FleetHealthyValue.Text = _fleetRows.Count(row => row.Health == "Healthy").ToString(CultureInfo.InvariantCulture);
        FleetAttentionValue.Text = _fleetRows.Count(row =>
                row.Health == "Attention" ||
                row.Trust != "Trusted" ||
                row.Health is "Probe failed" or "Authentication failed" or "Private key missing" or "Trust failed")
            .ToString(CultureInfo.InvariantCulture);
    }

    private static int FleetHealthRank(string health) =>
        health switch
        {
            "Attention" => 0,
            "Healthy" => 1,
            "Ready to probe" => 2,
            "Session secret required" => 3,
            "Trust required" => 4,
            _ => 5
        };

    private static string SafeFleetError(Exception exception)
    {
        if (exception is FileNotFoundException)
        {
            return "Private key missing";
        }

        if (exception is SecurityException)
        {
            return "Trust failed";
        }

        return exception.Message.Contains("passphrase", StringComparison.OrdinalIgnoreCase) ||
               exception.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase)
            ? "Authentication failed"
            : "Probe failed";
    }

    private void AlertFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            RefreshAlertCenter();
        }
    }

    private void RefreshAlertCenter()
    {
        if (AlertsList is null)
        {
            return;
        }

        var allAlerts = _fleetHistory
            .Where(IsAlertRecord)
            .OrderByDescending(record => record.RecordedUtc)
            .ToArray();

        var filter = (AlertFilterBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Active";
        IEnumerable<FleetHealthRecord> visible = filter switch
        {
            "Acknowledged" => allAlerts.Where(record => record.AcknowledgedUtc is not null),
            "All" => allAlerts,
            _ => allAlerts.Where(record => record.AcknowledgedUtc is null)
        };

        var names = Profiles.ToDictionary(
            profile => profile.Id,
            profile => profile.Name);

        var items = visible
            .Select(record => new FleetAlertItem
            {
                Record = record,
                ServerName = names.TryGetValue(record.ProfileId, out var name)
                    ? name
                    : "(deleted profile)"
            })
            .ToArray();

        AlertsList.ItemsSource = items;
        AlertsActiveValue.Text = allAlerts.Count(record => record.AcknowledgedUtc is null)
            .ToString(CultureInfo.InvariantCulture);
        AlertsAcknowledgedValue.Text = allAlerts.Count(record => record.AcknowledgedUtc is not null)
            .ToString(CultureInfo.InvariantCulture);
        AlertsAttentionValue.Text = allAlerts.Count(record =>
                string.Equals(record.Status, "Attention", StringComparison.Ordinal))
            .ToString(CultureInfo.InvariantCulture);
        AlertsFailuresValue.Text = allAlerts.Count(record =>
                !string.Equals(record.Status, "Attention", StringComparison.Ordinal))
            .ToString(CultureInfo.InvariantCulture);

        AlertsStatusText.Text = items.Length == 0
            ? "No alerts match the selected filter."
            : $"{items.Length} local alert(s) shown. Acknowledgement never changes the remote server.";
    }

    private async void AcknowledgeAlert_Click(object sender, RoutedEventArgs e)
    {
        if (AlertsList.SelectedItem is not FleetAlertItem item)
        {
            AlertsStatusText.Text = "Select an alert first.";
            return;
        }

        if (item.Record.AcknowledgedUtc is not null)
        {
            AlertsStatusText.Text = "The selected alert is already acknowledged.";
            return;
        }

        var previousAcknowledgement = item.Record.AcknowledgedUtc;
        item.Record.AcknowledgedUtc = DateTimeOffset.UtcNow;

        if (!await PersistFleetHistoryAsync())
        {
            item.Record.AcknowledgedUtc = previousAcknowledgement;
            RefreshAlertCenter();
            AlertsStatusText.Text = $"Could not acknowledge the local alert for {item.ServerName}. The alert remains active.";
            StatusText.Text = "Alert acknowledgement failed";
            return;
        }

        RefreshAlertCenter();
        AlertsStatusText.Text = $"Acknowledged local alert for {item.ServerName}.";
        StatusText.Text = "Local alert acknowledged";
    }

    private void OpenAlertServer_Click(object sender, RoutedEventArgs e)
    {
        if (AlertsList.SelectedItem is not FleetAlertItem item)
        {
            AlertsStatusText.Text = "Select an alert first.";
            return;
        }

        var profile = Profiles.FirstOrDefault(profile => profile.Id == item.ProfileId);
        if (profile is null)
        {
            AlertsStatusText.Text = "This alert belongs to a server profile that no longer exists.";
            return;
        }

        ServerList.SelectedItem = profile;
        ServerList.ScrollIntoView(profile);
        SetActiveNavigation(DashboardNavButton);
        ShowPage(DashboardPage, "Dashboard", "Server health, services and connection state.");
    }

    private async void ExportAlertsCsv_Click(object sender, RoutedEventArgs e)
    {
        var alerts = _fleetHistory
            .Where(IsAlertRecord)
            .OrderByDescending(record => record.RecordedUtc)
            .ToArray();

        if (alerts.Length == 0)
        {
            AlertsStatusText.Text = "There are no local alerts to export.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export Ghost Server alerts",
            FileName = $"GhostServer-Alerts-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            Filter = "CSV files (*.csv)|*.csv|All files|*.*",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var names = Profiles.ToDictionary(
            profile => profile.Id,
            profile => profile.Name);

        var csv = new StringBuilder();
        csv.AppendLine("RecordedUtc,Server,Status,AcknowledgedUtc,CpuPercent,MemoryPercent,DiskPercent,Load,Details");

        foreach (var alert in alerts)
        {
            var name = names.TryGetValue(alert.ProfileId, out var serverName)
                ? serverName
                : "(deleted profile)";

            csv.Append(CsvValue(alert.RecordedUtc.ToString("O", CultureInfo.InvariantCulture))).Append(',')
                .Append(CsvValue(name)).Append(',')
                .Append(CsvValue(alert.Status)).Append(',')
                .Append(CsvValue(alert.AcknowledgedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty)).Append(',')
                .Append(alert.CpuPercent.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                .Append(alert.MemoryPercent.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                .Append(alert.DiskPercent.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                .Append(CsvValue(alert.Load)).Append(',')
                .Append(CsvValue(alert.Message))
                .AppendLine();
        }

        try
        {
            await File.WriteAllTextAsync(
                dialog.FileName,
                csv.ToString(),
                Encoding.UTF8);

            AlertsStatusText.Text = $"Exported {alerts.Length} local alert(s).";
            StatusText.Text = "Alert CSV exported";
        }
        catch (Exception ex)
        {
            AlertsStatusText.Text = SafeError(ex);
            StatusText.Text = "Alert export failed";
        }
    }

    private static bool IsAlertRecord(FleetHealthRecord record) =>
        !string.Equals(record.Status, "Healthy", StringComparison.Ordinal);

    private static string CsvValue(string value)
    {
        if (!value.Contains(',') &&
            !value.Contains('"') &&
            !value.Contains('\r') &&
            !value.Contains('\n'))
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private void TrendSampleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            RefreshTrends();
        }
    }

    private void RefreshTrends_Click(object sender, RoutedEventArgs e) =>
        RefreshTrends();

    private void RefreshTrends()
    {
        if (TrendsList is null)
        {
            return;
        }

        var items = BuildTrendItems(GetTrendSampleLimit());
        TrendsList.ItemsSource = items;

        TrendsServersValue.Text = items.Length.ToString(CultureInfo.InvariantCulture);
        TrendsRisksValue.Text = items.Count(item =>
                !string.Equals(item.LatestStatus, "Healthy", StringComparison.Ordinal))
            .ToString(CultureInfo.InvariantCulture);

        var metricItems = items.Where(item => item.HasMetrics).ToArray();

        TrendsCpuValue.Text = metricItems.Length == 0
            ? "—"
            : metricItems.Max(item => item.AverageCpuPercent)
                .ToString("0.0", CultureInfo.InvariantCulture) + "%";

        TrendsDiskValue.Text = metricItems.Length == 0
            ? "—"
            : metricItems.Max(item => item.AverageDiskPercent)
                .ToString("0.0", CultureInfo.InvariantCulture) + "%";

        TrendsStatusText.Text = items.Length == 0
            ? "No local Fleet health history is available yet."
            : $"Comparing up to {GetTrendSampleLimit()} recent local probe record(s) per server.";
    }

    private FleetTrendItem[] BuildTrendItems(int sampleLimit)
    {
        var names = Profiles.ToDictionary(
            profile => profile.Id,
            profile => profile.Name);

        return _fleetHistory
            .GroupBy(record => record.ProfileId)
            .Select(group =>
            {
                var samples = group
                    .OrderByDescending(record => record.RecordedUtc)
                    .Take(sampleLimit)
                    .ToArray();

                var metricSamples = samples
                    .Where(record =>
                        string.Equals(record.Status, "Healthy", StringComparison.Ordinal) ||
                        string.Equals(record.Status, "Attention", StringComparison.Ordinal))
                    .ToArray();

                return new FleetTrendItem
                {
                    ProfileId = group.Key,
                    ServerName = names.TryGetValue(group.Key, out var name)
                        ? name
                        : "(deleted profile)",
                    SampleCount = samples.Length,
                    HasMetrics = metricSamples.Length > 0,
                    HealthyCount = samples.Count(record =>
                        string.Equals(record.Status, "Healthy", StringComparison.Ordinal)),
                    AttentionCount = samples.Count(record =>
                        string.Equals(record.Status, "Attention", StringComparison.Ordinal)),
                    FailureCount = samples.Count(record =>
                        !string.Equals(record.Status, "Healthy", StringComparison.Ordinal) &&
                        !string.Equals(record.Status, "Attention", StringComparison.Ordinal)),
                    AverageCpuPercent = AverageMetric(metricSamples, record => record.CpuPercent),
                    MaxCpuPercent = MaxMetric(metricSamples, record => record.CpuPercent),
                    AverageMemoryPercent = AverageMetric(metricSamples, record => record.MemoryPercent),
                    MaxMemoryPercent = MaxMetric(metricSamples, record => record.MemoryPercent),
                    AverageDiskPercent = AverageMetric(metricSamples, record => record.DiskPercent),
                    MaxDiskPercent = MaxMetric(metricSamples, record => record.DiskPercent),
                    LatestStatus = samples.Length == 0 ? "No data" : samples[0].Status
                };
            })
            .OrderBy(item => TrendRiskRank(item.LatestStatus))
            .ThenByDescending(item => item.FailureCount)
            .ThenByDescending(item => item.AttentionCount)
            .ThenByDescending(item => item.MaxDiskPercent)
            .ThenBy(item => item.ServerName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private int GetTrendSampleLimit()
    {
        var raw = (TrendSampleBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
               value is 10 or 25 or 50
            ? value
            : 25;
    }

    private static double AverageMetric(
        FleetHealthRecord[] records,
        Func<FleetHealthRecord, double> selector) =>
        records.Length == 0 ? 0.0 : records.Average(selector);

    private static double MaxMetric(
        FleetHealthRecord[] records,
        Func<FleetHealthRecord, double> selector) =>
        records.Length == 0 ? 0.0 : records.Max(selector);

    private static int TrendRiskRank(string status) =>
        status switch
        {
            "Attention" => 0,
            "Healthy" => 2,
            _ => 1
        };

    private void OpenTrendServer_Click(object sender, RoutedEventArgs e)
    {
        if (TrendsList.SelectedItem is not FleetTrendItem item)
        {
            TrendsStatusText.Text = "Select a trend row first.";
            return;
        }

        var profile = Profiles.FirstOrDefault(profile => profile.Id == item.ProfileId);
        if (profile is null)
        {
            TrendsStatusText.Text = "This trend belongs to a server profile that no longer exists.";
            return;
        }

        ServerList.SelectedItem = profile;
        ServerList.ScrollIntoView(profile);
        SetActiveNavigation(DashboardNavButton);
        ShowPage(DashboardPage, "Dashboard", "Server health, services and connection state.");
    }

    private async void ExportTrendsCsv_Click(object sender, RoutedEventArgs e)
    {
        var items = BuildTrendItems(GetTrendSampleLimit());
        if (items.Length == 0)
        {
            TrendsStatusText.Text = "There is no local trend data to export.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export Ghost Server Fleet trends",
            FileName = $"GhostServer-Fleet-Trends-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            Filter = "CSV files (*.csv)|*.csv|All files|*.*",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var csv = new StringBuilder();
        csv.AppendLine("Server,Samples,Healthy,Attention,Failures,AverageCpuPercent,MaxCpuPercent,AverageMemoryPercent,MaxMemoryPercent,AverageDiskPercent,MaxDiskPercent,LatestStatus");

        foreach (var item in items)
        {
            csv.Append(CsvValue(item.ServerName)).Append(',')
                .Append(item.SampleCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(item.HealthyCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(item.AttentionCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(item.FailureCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(item.AverageCpuPercent.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                .Append(item.MaxCpuPercent.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                .Append(item.AverageMemoryPercent.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                .Append(item.MaxMemoryPercent.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                .Append(item.AverageDiskPercent.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                .Append(item.MaxDiskPercent.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                .Append(CsvValue(item.LatestStatus))
                .AppendLine();
        }

        try
        {
            await File.WriteAllTextAsync(
                dialog.FileName,
                csv.ToString(),
                Encoding.UTF8);

            TrendsStatusText.Text = $"Exported {items.Length} Fleet trend row(s).";
            StatusText.Text = "Fleet trends CSV exported";
        }
        catch (Exception ex)
        {
            TrendsStatusText.Text = SafeError(ex);
            StatusText.Text = "Fleet trends export failed";
        }
    }

    private async void RefreshFiles_Click(object sender, RoutedEventArgs e) =>
        await RefreshFilesAsync();

    private async Task RefreshFilesAsync()
    {
        var operation = CaptureRemoteOperation();
        if (operation is null)
        {
            RemoteFilesList.ItemsSource = null;
            FilesStatusText.Text = "Select a server on Dashboard first.";
            return;
        }

        if (!TryBeginUiOperation(operation, "files-refresh", "Remote files are already refreshing."))
        {
            return;
        }

        try
        {
            var path = string.IsNullOrWhiteSpace(RemotePathBox.Text) ? "/" : RemotePathBox.Text.Trim();
            FilesStatusText.Text = "Loading…";
            StatusText.Text = "Loading remote files…";

            var files = await SshServerClient.GetRemoteFilesAsync(
                operation.Profile,
                operation.Secret,
                path,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            RemoteFilesList.ItemsSource = files;
            RemotePathBox.Text = NormalizeUiRemotePath(path);
            FilesStatusText.Text = $"{files.Count} item(s)";
            StatusText.Text = "Remote files refreshed";
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsRemoteOperationCurrent(operation))
            {
                RemoteFilesList.ItemsSource = null;
                FilesStatusText.Text = SafeError(ex);
                StatusText.Text = "Remote file refresh failed";
            }
        }
        finally
        {
            EndUiOperation(operation, "files-refresh");
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
        var operation = CaptureRemoteOperation();
        if (operation is null)
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

        if (!TryBeginUiOperation(operation, "file-upload", "A file upload is already running."))
        {
            return;
        }

        try
        {
            FilesStatusText.Text = $"Uploading {Path.GetFileName(dialog.FileName)}…";
            await SshServerClient.UploadFileAsync(
                operation.Profile,
                operation.Secret,
                dialog.FileName,
                RemotePathBox.Text,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            StatusText.Text = $"Uploaded {Path.GetFileName(dialog.FileName)}";
            await RefreshFilesAsync();
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsRemoteOperationCurrent(operation))
            {
                FilesStatusText.Text = SafeError(ex);
                StatusText.Text = "Upload failed";
            }
        }
        finally
        {
            EndUiOperation(operation, "file-upload");
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
        var operation = CaptureRemoteOperation();
        if (operation is null)
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

        if (!TryBeginUiOperation(operation, "file-download", "A file download is already running."))
        {
            return;
        }

        try
        {
            FilesStatusText.Text = $"Downloading {item.Name}…";
            await SshServerClient.DownloadFileAsync(
                operation.Profile,
                operation.Secret,
                item.FullPath,
                dialog.FileName,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            FilesStatusText.Text = $"Downloaded {item.Name}";
            StatusText.Text = $"Downloaded {item.Name}";
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsRemoteOperationCurrent(operation))
            {
                FilesStatusText.Text = SafeError(ex);
                StatusText.Text = "Download failed";
            }
        }
        finally
        {
            EndUiOperation(operation, "file-download");
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
        var operation = CaptureRemoteOperation();
        if (operation is null)
        {
            return;
        }

        if (!TryBeginUiOperation(operation, "dashboard-connect", "A Dashboard connection operation is already running."))
        {
            return;
        }

        SetBusy("Connecting…");
        HostKeyPanel.Visibility = Visibility.Collapsed;

        try
        {
            if (string.IsNullOrWhiteSpace(operation.Profile.HostKeyFingerprint))
            {
                var probe = await SshServerClient.ProbeAsync(
                    operation.Profile,
                    operation.Secret,
                    operation.CancellationToken);

                if (!IsRemoteOperationCurrent(operation))
                {
                    return;
                }

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

            var snapshot = await SshServerClient.GetSnapshotAsync(
                operation.Profile,
                operation.Secret,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            ApplySnapshot(snapshot);
            if (SelectedProfile is not null)
            {
                SelectedProfile.LastConnectedUtc = DateTimeOffset.UtcNow;
                await _profileStore.SaveAsync(Profiles);
            }

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            ConnectionStatus.Text = "Connected";
            ConnectionStatus.Foreground = (Brush)FindResource("GhostSuccess");
            StatusText.Text = $"Connected to {operation.Profile.Name}";
            await RefreshServicesAsync();
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (SecurityException ex)
        {
            if (IsRemoteOperationCurrent(operation))
            {
                ConnectionStatus.Text = "Host key rejected";
                ConnectionStatus.Foreground = (Brush)FindResource("GhostDanger");
                StatusText.Text = SafeError(ex);
            }
        }
        catch (Exception ex)
        {
            if (IsRemoteOperationCurrent(operation))
            {
                ConnectionStatus.Text = "Connection failed";
                ConnectionStatus.Foreground = (Brush)FindResource("GhostDanger");
                StatusText.Text = SafeError(ex);
            }
        }
        finally
        {
            EndUiOperation(operation, "dashboard-connect");
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
        var operation = CaptureRemoteOperation();
        if (operation is null)
        {
            return;
        }

        if (!TryBeginUiOperation(operation, "dashboard-services-refresh", "Dashboard services are already refreshing."))
        {
            return;
        }

        try
        {
            SetBusy("Loading services…");
            var services = await SshServerClient.GetRunningServicesAsync(
                operation.Profile,
                operation.Secret,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            ServicesList.ItemsSource = services;
            StatusText.Text = "Service list refreshed";
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsRemoteOperationCurrent(operation))
            {
                StatusText.Text = SafeError(ex);
            }
        }
        finally
        {
            EndUiOperation(operation, "dashboard-services-refresh");
        }
    }

    private async void RefreshManagerServices_Click(object sender, RoutedEventArgs e) =>
        await RefreshManagerServicesAsync();

    private async Task RefreshManagerServicesAsync()
    {
        var operation = CaptureRemoteOperation();
        if (operation is null)
        {
            ServicesManagerList.ItemsSource = null;
            SelectedServiceText.Text = "Select a server on Dashboard first.";
            return;
        }

        if (!TryBeginUiOperation(operation, "services-refresh", "Services are already refreshing."))
        {
            return;
        }

        try
        {
            StatusText.Text = "Loading services…";
            var services = await SshServerClient.GetServicesAsync(
                operation.Profile,
                operation.Secret,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            ServicesManagerList.ItemsSource = services;
            SelectedServiceText.Text = "Select a service to manage it.";
            StatusText.Text = "Services refreshed";
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsRemoteOperationCurrent(operation))
            {
                ServicesManagerList.ItemsSource = null;
                SelectedServiceText.Text = SafeError(ex);
                StatusText.Text = "Service refresh failed";
            }
        }
        finally
        {
            EndUiOperation(operation, "services-refresh");
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
        var operation = CaptureRemoteOperation();
        if (operation is null)
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
            !await ConfirmAdministrativeActionAsync(
                $"{char.ToUpperInvariant(action[0])}{action[1..]} service?",
                $"{char.ToUpperInvariant(action[0])}{action[1..]} {service.Name} on {operation.Profile.Name}?",
                $"{char.ToUpperInvariant(action[0])}{action[1..]} service"))
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
            var output = await SshServerClient.ServiceActionAsync(
                operation.Profile,
                operation.Secret,
                service.Name,
                action,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            StatusText.Text = $"{service.Name}: {output}";
            await RefreshManagerServicesAsync();
            await RefreshServicesAsync();
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsRemoteOperationCurrent(operation))
            {
                SelectedServiceText.Text = SafeError(ex);
                StatusText.Text = $"Service {action} failed";
            }
        }
        finally
        {
            ReleaseAdministrativeMutation();
        }
    }

    private async void ServiceLogs_Click(object sender, RoutedEventArgs e)
    {
        var operation = CaptureRemoteOperation();
        if (operation is null)
        {
            SelectedServiceText.Text = "Select a server first.";
            return;
        }

        if (ServicesManagerList.SelectedItem is not ServiceStatus service)
        {
            SelectedServiceText.Text = "Select a service first.";
            return;
        }

        if (!TryBeginUiOperation(operation, "service-logs", "Service logs are already loading."))
        {
            return;
        }

        try
        {
            StatusText.Text = $"Loading logs for {service.Name}…";
            var logs = await SshServerClient.GetServiceLogsAsync(
                operation.Profile,
                operation.Secret,
                service.Name,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            _rawLogs = logs;
            LogsFilterBox.Clear();
            ApplyLogFilter();
            SetActiveNavigation(SystemNavButton);
            ShowPage(LogsPage, "Logs", $"Recent logs for {service.Name}");
            StatusText.Text = $"Loaded logs for {service.Name}";
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsRemoteOperationCurrent(operation))
            {
                SelectedServiceText.Text = SafeError(ex);
                StatusText.Text = "Service log load failed";
            }
        }
        finally
        {
            EndUiOperation(operation, "service-logs");
        }
    }

    private async void RefreshDocker_Click(object sender, RoutedEventArgs e) =>
        await RefreshDockerAsync();

    private async Task RefreshDockerAsync()
    {
        var operation = CaptureRemoteOperation();
        if (operation is null)
        {
            DockerList.ItemsSource = null;
            SelectedDockerText.Text = "Select a server on Dashboard first.";
            return;
        }

        if (!TryBeginUiOperation(operation, "docker-refresh", "Docker containers are already refreshing."))
        {
            return;
        }

        try
        {
            StatusText.Text = "Loading Docker containers…";
            var containers = await SshServerClient.GetDockerContainersAsync(
                operation.Profile,
                operation.Secret,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            DockerList.ItemsSource = containers;
            SelectedDockerText.Text = "Select a Docker container.";
            StatusText.Text = "Docker containers refreshed";
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsRemoteOperationCurrent(operation))
            {
                DockerList.ItemsSource = null;
                SelectedDockerText.Text = SafeError(ex);
                StatusText.Text = "Docker refresh failed";
            }
        }
        finally
        {
            EndUiOperation(operation, "docker-refresh");
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
        var operation = CaptureRemoteOperation();
        if (operation is null)
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
            !await ConfirmAdministrativeActionAsync(
                $"{char.ToUpperInvariant(action[0])}{action[1..]} container?",
                $"{char.ToUpperInvariant(action[0])}{action[1..]} Docker container {container.Name} on {operation.Profile.Name}?",
                $"{char.ToUpperInvariant(action[0])}{action[1..]} container"))
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
            var output = await SshServerClient.DockerActionAsync(
                operation.Profile,
                operation.Secret,
                container.Id,
                action,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            StatusText.Text = $"Docker {container.Name}: {output}";
            await RefreshDockerAsync();
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsRemoteOperationCurrent(operation))
            {
                SelectedDockerText.Text = SafeError(ex);
                StatusText.Text = $"Docker {action} failed";
            }
        }
        finally
        {
            ReleaseAdministrativeMutation();
        }
    }

    private async void DockerLogs_Click(object sender, RoutedEventArgs e)
    {
        var operation = CaptureRemoteOperation();
        if (operation is null)
        {
            SelectedDockerText.Text = "Select a server first.";
            return;
        }

        if (DockerList.SelectedItem is not DockerContainerStatus container)
        {
            SelectedDockerText.Text = "Select a Docker container first.";
            return;
        }

        if (!TryBeginUiOperation(operation, "docker-logs", "Docker logs are already loading."))
        {
            return;
        }

        try
        {
            StatusText.Text = $"Loading logs for {container.Name}…";
            var logs = await SshServerClient.GetDockerLogsAsync(
                operation.Profile,
                operation.Secret,
                container.Id,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            _rawLogs = logs;
            LogsFilterBox.Clear();
            ApplyLogFilter();
            SetActiveNavigation(SystemNavButton);
            ShowPage(LogsPage, "Logs", $"Recent logs for Docker container {container.Name}");
            StatusText.Text = $"Loaded Docker logs for {container.Name}";
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsRemoteOperationCurrent(operation))
            {
                SelectedDockerText.Text = SafeError(ex);
                StatusText.Text = "Docker log load failed";
            }
        }
        finally
        {
            EndUiOperation(operation, "docker-logs");
        }
    }

    private async void RefreshNetwork_Click(object sender, RoutedEventArgs e) =>
        await RefreshNetworkAsync();

    private async Task RefreshNetworkAsync()
    {
        var operation = CaptureRemoteOperation();
        if (operation is null)
        {
            NetworkOutput.Text = "Select a server on Dashboard first.";
            return;
        }

        if (!TryBeginUiOperation(operation, "network-refresh", "Network state is already refreshing."))
        {
            return;
        }

        try
        {
            StatusText.Text = "Loading network state…";
            var output = await SshServerClient.GetNetworkOverviewAsync(
                operation.Profile,
                operation.Secret,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            NetworkOutput.Text = output;
            NetworkOutput.ScrollToHome();
            StatusText.Text = "Network state refreshed";
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsRemoteOperationCurrent(operation))
            {
                NetworkOutput.Text = SafeError(ex);
                StatusText.Text = "Network refresh failed";
            }
        }
        finally
        {
            EndUiOperation(operation, "network-refresh");
        }
    }

    private async void AllowFirewallPort_Click(object sender, RoutedEventArgs e)
    {
        var operation = CaptureRemoteOperation();
        if (operation is null)
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
        if (!await ShowGhostConfirmationAsync(
                "Confirm firewall change",
                $"Allow inbound {protocol.ToUpperInvariant()} port {port} on {operation.Profile.Name}?\n\nThis changes the remote firewall and requires passwordless sudo for the connected account.",
                "Allow port",
                danger: true))
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
            var output = await SshServerClient.AllowFirewallPortAsync(
                operation.Profile,
                operation.Secret,
                port,
                protocol,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            NetworkOutput.Text = output;
            StatusText.Text = $"Firewall rule added: {port}/{protocol}";
            await RefreshNetworkAsync();
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsRemoteOperationCurrent(operation))
            {
                NetworkOutput.Text = SafeError(ex);
                StatusText.Text = "Firewall change failed";
            }
        }
        finally
        {
            ReleaseAdministrativeMutation();
        }
    }

    private async void RefreshUpdates_Click(object sender, RoutedEventArgs e) =>
        await RefreshUpdatesAsync();

    private async Task RefreshUpdatesAsync()
    {
        var operation = CaptureRemoteOperation();
        if (operation is null)
        {
            UpdatesOutput.Text = "Select a server on Dashboard first.";
            return;
        }

        if (!TryBeginUiOperation(operation, "updates-refresh", "Safe Update preview is already running."))
        {
            return;
        }

        try
        {
            StatusText.Text = "Preparing Safe Update preview…";
            var output = await SshServerClient.GetSafeUpdatePreviewAsync(
                operation.Profile,
                operation.Secret,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            UpdatesOutput.Text = string.IsNullOrWhiteSpace(output)
                ? "No update information was reported."
                : output;
            UpdatesOutput.ScrollToHome();
            StatusText.Text = "Safe Update preview completed";
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsRemoteOperationCurrent(operation))
            {
                UpdatesOutput.Text = SafeError(ex);
                StatusText.Text = "Safe Update preview failed";
            }
        }
        finally
        {
            EndUiOperation(operation, "updates-refresh");
        }
    }

    private async void RunSafeUpdate_Click(object sender, RoutedEventArgs e)
    {
        var operation = CaptureRemoteOperation();
        if (operation is null)
        {
            UpdatesOutput.Text = "Select a server on Dashboard first.";
            return;
        }

        if (!await ShowGhostConfirmationAsync(
                "Run Safe Update",
                $"Run Safe Update on {operation.Profile.Name}?\n\nGhost Server will first create and download a configuration snapshot. It will then install regular updates using the detected supported package manager. No automatic reboot is performed. Package managers may update dependencies.",
                "Run Safe Update",
                danger: false))
        {
            return;
        }

        var snapshotDialog = CreateSnapshotSaveDialog(
            "Save mandatory pre-update configuration snapshot");

        if (snapshotDialog.ShowDialog(this) != true)
        {
            UpdatesOutput.Text = "Safe Update cancelled because the required configuration snapshot was not saved.";
            return;
        }

        if (!TryAcquireMutation("Preparing Safe Update…"))
        {
            return;
        }

        string? remoteArchive = null;
        try
        {
            UpdatesOutput.Text = "Step 1/4 • Creating configuration snapshot…";
            remoteArchive = await SshServerClient.CreateConfigurationSnapshotAsync(
                operation.Profile,
                operation.Secret,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            UpdatesOutput.AppendText(
                $"{Environment.NewLine}Step 2/4 • Downloading snapshot over verified SFTP…");

            await SshServerClient.DownloadFileAsync(
                operation.Profile,
                operation.Secret,
                remoteArchive,
                snapshotDialog.FileName,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            var snapshotSize = new FileInfo(snapshotDialog.FileName).Length;
            UpdatesOutput.AppendText(
                $"{Environment.NewLine}Snapshot saved: {snapshotDialog.FileName}");
            UpdatesOutput.AppendText(
                $"{Environment.NewLine}Snapshot size: {snapshotSize:N0} bytes");
            UpdatesOutput.AppendText(
                $"{Environment.NewLine}{Environment.NewLine}Step 3/4 • Installing supported package updates…");
            UpdatesOutput.ScrollToEnd();

            StatusText.Text = "Safe Update is installing package updates…";
            var updateOutput = await SshServerClient.RunSafeUpdateAsync(
                operation.Profile,
                operation.Secret,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            UpdatesOutput.AppendText(
                $"{Environment.NewLine}{Environment.NewLine}{updateOutput}");
            UpdatesOutput.AppendText(
                $"{Environment.NewLine}{Environment.NewLine}Step 4/4 • Running post-update health check…");
            UpdatesOutput.ScrollToEnd();

            var healthOutput = await SshServerClient.GetSafeUpdateHealthAsync(
                operation.Profile,
                operation.Secret,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            UpdatesOutput.AppendText(
                $"{Environment.NewLine}{Environment.NewLine}{healthOutput}");
            UpdatesOutput.AppendText(
                $"{Environment.NewLine}{Environment.NewLine}Safe Update finished. Review the health report above. Ghost Server did not reboot the server.");
            UpdatesOutput.ScrollToEnd();
            StatusText.Text = "Safe Update completed";
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsRemoteOperationCurrent(operation))
            {
                UpdatesOutput.AppendText(
                    $"{Environment.NewLine}{Environment.NewLine}[Safe Update stopped] {SafeError(ex)}");
                UpdatesOutput.AppendText(
                    $"{Environment.NewLine}No automatic reboot was attempted. The pre-update snapshot remains on this PC if its download completed.");
                UpdatesOutput.ScrollToEnd();
                StatusText.Text = "Safe Update stopped";
            }
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(remoteArchive))
            {
                try
                {
                    await SshServerClient.DeleteRemoteFileAsync(
                        operation.Profile,
                        operation.Secret,
                        remoteArchive);
                    if (IsRemoteOperationCurrent(operation))
                    {
                        UpdatesOutput.AppendText(
                            $"{Environment.NewLine}Temporary remote snapshot removed.");
                    }
                }
                catch (Exception cleanupEx)
                {
                    if (IsRemoteOperationCurrent(operation))
                    {
                        UpdatesOutput.AppendText(
                            $"{Environment.NewLine}[cleanup warning] {SafeError(cleanupEx)}");
                    }
                }
            }

            ReleaseAdministrativeMutation();
        }
    }

    private async void RestoreConfigSnapshot_Click(object sender, RoutedEventArgs e)
    {
        var operation = CaptureRemoteOperation();
        if (operation is null)
        {
            UpdatesOutput.Text = "Select a server on Dashboard first.";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select Ghost Server configuration snapshot",
            Filter = "Ghost Server snapshot (*.tar.gz)|*.tar.gz|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (!await ShowGhostConfirmationAsync(
                "Restore configuration snapshot",
                $"Restore allowlisted configuration from {Path.GetFileName(dialog.FileName)} to {operation.Profile.Name}?\n\nThis can overwrite SSH, web server, systemd, Docker, Fail2ban or UFW configuration contained in the snapshot. Ghost Server validates archive paths and file types first. It will not downgrade packages, restart services or reboot automatically.",
                "Restore snapshot",
                danger: true))
        {
            return;
        }

        if (!TryAcquireMutation("Preparing configuration restore…"))
        {
            return;
        }

        var remotePath = $"/tmp/ghost-server-restore-{Guid.NewGuid():N}.tar.gz";
        try
        {
            UpdatesOutput.Text = "Uploading snapshot over verified SFTP…";
            await SshServerClient.UploadFileToPathAsync(
                operation.Profile,
                operation.Secret,
                dialog.FileName,
                remotePath,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            UpdatesOutput.AppendText(
                $"{Environment.NewLine}Validating allowlisted paths and archive entry types…");

            var restoreOutput = await SshServerClient.RestoreConfigurationSnapshotAsync(
                operation.Profile,
                operation.Secret,
                remotePath,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            UpdatesOutput.AppendText(
                $"{Environment.NewLine}{Environment.NewLine}{restoreOutput}");
            UpdatesOutput.AppendText(
                $"{Environment.NewLine}{Environment.NewLine}Running post-restore health check…");

            var healthOutput = await SshServerClient.GetSafeUpdateHealthAsync(
                operation.Profile,
                operation.Secret,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            UpdatesOutput.AppendText(
                $"{Environment.NewLine}{Environment.NewLine}{healthOutput}");
            UpdatesOutput.ScrollToEnd();
            StatusText.Text = "Configuration restore completed";
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsRemoteOperationCurrent(operation))
            {
                UpdatesOutput.AppendText(
                    $"{Environment.NewLine}{Environment.NewLine}[restore stopped] {SafeError(ex)}");
                UpdatesOutput.ScrollToEnd();
                StatusText.Text = "Configuration restore stopped";
            }
        }
        finally
        {
            try
            {
                await SshServerClient.DeleteRemoteFileAsync(
                    operation.Profile,
                    operation.Secret,
                    remotePath);
            }
            catch (Exception cleanupEx)
            {
                if (IsRemoteOperationCurrent(operation))
                {
                    UpdatesOutput.AppendText(
                        $"{Environment.NewLine}[cleanup warning] {SafeError(cleanupEx)}");
                }
            }

            ReleaseAdministrativeMutation();
        }
    }

    private async void CreateConfigBackup_Click(object sender, RoutedEventArgs e)
    {
        var operation = CaptureRemoteOperation();
        if (operation is null)
        {
            BackupOutput.Text = "Select a server on Dashboard first.";
            return;
        }

        var dialog = CreateSnapshotSaveDialog(
            "Save Ghost Server configuration snapshot");

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
            remoteArchive = await SshServerClient.CreateConfigurationSnapshotAsync(
                operation.Profile,
                operation.Secret,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            BackupOutput.AppendText($"{Environment.NewLine}Remote archive: {remoteArchive}");
            BackupOutput.AppendText($"{Environment.NewLine}Downloading securely over SFTP…");

            await SshServerClient.DownloadFileAsync(
                operation.Profile,
                operation.Secret,
                remoteArchive,
                dialog.FileName,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            var size = new FileInfo(dialog.FileName).Length;
            BackupOutput.AppendText($"{Environment.NewLine}Saved: {dialog.FileName}");
            BackupOutput.AppendText($"{Environment.NewLine}Size: {size:N0} bytes");
            StatusText.Text = "Configuration snapshot downloaded";
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsRemoteOperationCurrent(operation))
            {
                BackupOutput.AppendText($"{Environment.NewLine}[error] {SafeError(ex)}");
                StatusText.Text = "Configuration snapshot failed";
            }
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(remoteArchive))
            {
                try
                {
                    await SshServerClient.DeleteRemoteFileAsync(
                        operation.Profile,
                        operation.Secret,
                        remoteArchive);
                    if (IsRemoteOperationCurrent(operation))
                    {
                        BackupOutput.AppendText($"{Environment.NewLine}Temporary remote archive removed.");
                    }
                }
                catch (Exception cleanupEx)
                {
                    if (IsRemoteOperationCurrent(operation))
                    {
                        BackupOutput.AppendText(
                            $"{Environment.NewLine}[cleanup warning] {SafeError(cleanupEx)}");
                    }
                }
            }

            if (IsRemoteOperationCurrent(operation))
            {
                BackupOutput.ScrollToEnd();
            }

            ReleaseAdministrativeMutation();
        }
    }

    private async void RefreshTasks_Click(object sender, RoutedEventArgs e) =>
        await RefreshTasksAsync();

    private async Task RefreshTasksAsync()
    {
        var operation = CaptureRemoteOperation();
        if (operation is null)
        {
            TasksList.ItemsSource = null;
            TasksStatusText.Text = "Select a server on Dashboard first.";
            CrontabOutput.Text = "Select a server on Dashboard first.";
            return;
        }

        if (!TryBeginUiOperation(operation, "tasks-refresh", "Scheduled operations are already refreshing."))
        {
            return;
        }

        try
        {
            StatusText.Text = "Loading scheduled operations…";
            var tasksTask = SshServerClient.GetScheduledTasksAsync(
                operation.Profile,
                operation.Secret,
                operation.CancellationToken);
            var crontabTask = SshServerClient.GetUserCrontabAsync(
                operation.Profile,
                operation.Secret,
                operation.CancellationToken);

            await Task.WhenAll(tasksTask, crontabTask);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            var tasks = await tasksTask;
            TasksList.ItemsSource = tasks;
            TasksStatusText.Text = tasks.Count == 0
                ? "No Ghost Server scheduled tasks."
                : $"{tasks.Count} Ghost Server scheduled task(s).";
            CrontabOutput.Text = await crontabTask;
            StatusText.Text = "Scheduled operations refreshed";
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsRemoteOperationCurrent(operation))
            {
                TasksList.ItemsSource = null;
                TasksStatusText.Text = SafeError(ex);
                CrontabOutput.Text = SafeError(ex);
                StatusText.Text = "Scheduled operations refresh failed";
            }
        }
        finally
        {
            EndUiOperation(operation, "tasks-refresh");
        }
    }

    private async void CreateTask_Click(object sender, RoutedEventArgs e)
    {
        var operation = CaptureRemoteOperation();
        if (operation is null)
        {
            TasksStatusText.Text = "Select a server on Dashboard first.";
            return;
        }

        var name = TaskNameBox.Text.Trim();
        var command = TaskCommandBox.Text.Trim();
        var schedule = (TaskScheduleBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Daily";

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(command))
        {
            TasksStatusText.Text = "Task name and command are required.";
            return;
        }

        if (!await ConfirmAdministrativeActionAsync(
                "Create scheduled task?",
                $"Create Ghost Server task '{name}' on {operation.Profile.Name} with schedule {schedule}?\n\nThe command will run as root through a dedicated systemd oneshot service.",
                "Create task"))
        {
            return;
        }

        if (!TryAcquireMutation("Creating scheduled task…"))
        {
            return;
        }

        try
        {
            var output = await SshServerClient.CreateScheduledTaskAsync(
                operation.Profile,
                operation.Secret,
                name,
                schedule,
                command,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            TasksStatusText.Text = output;
            TaskNameBox.Clear();
            TaskCommandBox.Clear();
            await RefreshTasksAsync();
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsRemoteOperationCurrent(operation))
            {
                TasksStatusText.Text = SafeError(ex);
                StatusText.Text = "Scheduled task creation failed";
            }
        }
        finally
        {
            ReleaseAdministrativeMutation();
        }
    }

    private async void DeleteTask_Click(object sender, RoutedEventArgs e)
    {
        var operation = CaptureRemoteOperation();
        if (operation is null)
        {
            TasksStatusText.Text = "Select a server on Dashboard first.";
            return;
        }

        if (TasksList.SelectedItem is not ScheduledTaskStatus task)
        {
            TasksStatusText.Text = "Select a Ghost Server scheduled task first.";
            return;
        }

        if (!await ConfirmAdministrativeActionAsync(
                "Delete scheduled task?",
                $"Delete Ghost Server task '{task.Name}' from {operation.Profile.Name}?",
                "Delete task"))
        {
            return;
        }

        if (!TryAcquireMutation("Deleting scheduled task…"))
        {
            return;
        }

        try
        {
            var output = await SshServerClient.DeleteScheduledTaskAsync(
                operation.Profile,
                operation.Secret,
                task.Name,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            TasksStatusText.Text = output;
            await RefreshTasksAsync();
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsRemoteOperationCurrent(operation))
            {
                TasksStatusText.Text = SafeError(ex);
                StatusText.Text = "Scheduled task deletion failed";
            }
        }
        finally
        {
            ReleaseAdministrativeMutation();
        }
    }

    private async void RefreshSystem_Click(object sender, RoutedEventArgs e) =>
        await RefreshSystemAsync();

    private async Task RefreshSystemAsync()
    {
        var operation = CaptureRemoteOperation();
        if (operation is null)
        {
            ProcessesList.ItemsSource = null;
            SystemStatusText.Text = "Select a server on Dashboard first.";
            SystemOverviewOutput.Text = "Select a server on Dashboard first.";
            return;
        }

        if (!TryBeginUiOperation(operation, "system-refresh", "System state is already refreshing."))
        {
            return;
        }

        try
        {
            StatusText.Text = "Loading system state…";

            var processesTask = SshServerClient.GetProcessesAsync(
                operation.Profile,
                operation.Secret,
                operation.CancellationToken);
            var overviewTask = SshServerClient.GetSystemOverviewAsync(
                operation.Profile,
                operation.Secret,
                operation.CancellationToken);

            await Task.WhenAll(processesTask, overviewTask);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            var processes = await processesTask;
            ProcessesList.ItemsSource = processes;
            SystemOverviewOutput.Text = await overviewTask;
            SystemOverviewOutput.ScrollToHome();
            SystemStatusText.Text = $"{processes.Count} process(es) loaded.";
            StatusText.Text = "System state refreshed";
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsRemoteOperationCurrent(operation))
            {
                ProcessesList.ItemsSource = null;
                SystemStatusText.Text = SafeError(ex);
                SystemOverviewOutput.Text = SafeError(ex);
                StatusText.Text = "System refresh failed";
            }
        }
        finally
        {
            EndUiOperation(operation, "system-refresh");
        }
    }

    private void ProcessesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProcessesList.SelectedItem is not ProcessStatus process)
        {
            SystemStatusText.Text = "Select a process to inspect or terminate it.";
            return;
        }

        SystemStatusText.Text =
            $"PID {process.Pid} • {process.User} • CPU {process.CpuPercent:0.0}% • RAM {process.MemoryPercent:0.0}% • {process.Command}";
    }

    private async void TerminateProcess_Click(object sender, RoutedEventArgs e)
    {
        var operation = CaptureRemoteOperation();
        if (operation is null)
        {
            SystemStatusText.Text = "Select a server on Dashboard first.";
            return;
        }

        if (ProcessesList.SelectedItem is not ProcessStatus process)
        {
            SystemStatusText.Text = "Select a process first.";
            return;
        }

        if (!await ConfirmAdministrativeActionAsync(
                "Terminate process?",
                $"Send SIGTERM to PID {process.Pid} ({process.Command}) on {operation.Profile.Name}?",
                "Terminate process"))
        {
            return;
        }

        if (!TryAcquireMutation("Terminating selected process…"))
        {
            return;
        }

        try
        {
            var output = await SshServerClient.TerminateProcessAsync(
                operation.Profile,
                operation.Secret,
                process.Pid,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            SystemStatusText.Text = output;
            StatusText.Text = $"SIGTERM sent to PID {process.Pid}";
            await RefreshSystemAsync();
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsRemoteOperationCurrent(operation))
            {
                SystemStatusText.Text = SafeError(ex);
                StatusText.Text = "Process termination failed";
            }
        }
        finally
        {
            ReleaseAdministrativeMutation();
        }
    }

    private async void RefreshDatabases_Click(object sender, RoutedEventArgs e) =>
        await RefreshDatabasesAsync();

    private async Task RefreshDatabasesAsync()
    {
        var operation = CaptureRemoteOperation();
        if (operation is null)
        {
            DatabaseEnginesList.ItemsSource = null;
            DatabasesStatusText.Text = "Select a server on Dashboard first.";
            DatabaseNamesOutput.Text = "No server selected.";
            return;
        }

        if (!TryBeginUiOperation(operation, "databases-refresh", "Database discovery is already running."))
        {
            return;
        }

        try
        {
            StatusText.Text = "Discovering database engines…";
            DatabasesStatusText.Text = "Checking PostgreSQL, MySQL/MariaDB and SQLite…";

            var engines = await SshServerClient.GetDatabaseEnginesAsync(
                operation.Profile,
                operation.Secret,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            DatabaseEnginesList.ItemsSource = engines;
            DatabaseNamesOutput.Text = engines.Count == 0
                ? "No supported database client was detected."
                : "Select a database engine to inspect discovered database names.";

            if (engines.Count == 0)
            {
                DatabasesStatusText.Text = "No supported database engines were detected.";
            }
            else
            {
                var restricted = engines.Count(engine =>
                    engine.Access.Contains("no non-interactive access", StringComparison.OrdinalIgnoreCase));

                DatabasesStatusText.Text = restricted == 0
                    ? $"{engines.Count} database engine(s) detected."
                    : $"{engines.Count} engine(s) detected • {restricted} require database credentials outside Ghost Server.";

                DatabaseEnginesList.SelectedIndex = 0;
            }

            StatusText.Text = "Database discovery completed";
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsRemoteOperationCurrent(operation))
            {
                DatabaseEnginesList.ItemsSource = null;
                DatabasesStatusText.Text = SafeError(ex);
                DatabaseNamesOutput.Text = SafeError(ex);
                StatusText.Text = "Database discovery failed";
            }
        }
        finally
        {
            EndUiOperation(operation, "databases-refresh");
        }
    }

    private void DatabaseEnginesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DatabaseEnginesList.SelectedItem is not DatabaseEngineStatus engine)
        {
            DatabaseNamesOutput.Text = "Select a database engine to inspect discovered database names.";
            return;
        }

        DatabaseNamesOutput.Text = engine.DatabaseNames;
        DatabasesStatusText.Text =
            $"{engine.Engine} • service {engine.ServiceStatus} • {engine.Access} • {engine.DatabaseCount} database name(s)";
    }

    private async void RefreshLogs_Click(object sender, RoutedEventArgs e) =>
        await RefreshLogsAsync();

    private async Task RefreshLogsAsync()
    {
        var operation = CaptureRemoteOperation();
        if (operation is null)
        {
            _rawLogs = string.Empty;
            LogsOutput.Text = "Select a server on Dashboard first.";
            return;
        }

        if (!TryBeginUiOperation(operation, "logs-refresh", "Logs are already refreshing."))
        {
            return;
        }

        try
        {
            StatusText.Text = "Loading recent logs…";
            var logs = await SshServerClient.GetRecentLogsAsync(
                operation.Profile,
                operation.Secret,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            _rawLogs = logs;
            ApplyLogFilter();
            LogsOutput.ScrollToEnd();
            StatusText.Text = "Logs refreshed";
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsRemoteOperationCurrent(operation))
            {
                _rawLogs = string.Empty;
                LogsOutput.Text = SafeError(ex);
                StatusText.Text = "Log refresh failed";
            }
        }
        finally
        {
            EndUiOperation(operation, "logs-refresh");
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

    private async void ConnectTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (_terminalTransitionBusy)
        {
            return;
        }

        var profile = SelectedProfile;
        if (profile is null)
        {
            AppendTerminalSystemLine("Select a server on Dashboard before connecting.");
            return;
        }

        if (string.IsNullOrWhiteSpace(profile.HostKeyFingerprint))
        {
            AppendTerminalSystemLine("SSH host key must be approved before opening an interactive shell.");
            TerminalSessionStatus.Text = "Trust required";
            TerminalSessionStatus.Foreground = (Brush)FindResource("GhostWarning");
            UpdateTerminalSessionUi();
            return;
        }

        _terminalConnectCancellation?.Cancel();
        _terminalConnectCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _terminalConnectCancellation = cancellation;

        _terminalTransitionBusy = true;
        TerminalSessionStatus.Text = "Connecting…";
        TerminalSessionStatus.Foreground = (Brush)FindResource("GhostMuted");
        StatusText.Text = $"Opening interactive shell to {profile.Name}…";
        UpdateTerminalSessionUi();

        try
        {
            await _terminalSession.ConnectAsync(
                profile,
                SessionSecretBox.Password,
                cancellation.Token);

            if (cancellation.IsCancellationRequested ||
                SelectedProfile?.Id != profile.Id ||
                (ServerList.SelectedItem as ServerProfile)?.Id != profile.Id)
            {
                await _terminalSession.DisconnectAsync();
                return;
            }

            TerminalSessionStatus.Text = "Connected";
            TerminalSessionStatus.Foreground = (Brush)FindResource("GhostSuccess");
            AppendTerminalSystemLine(
                $"Connected to {profile.Username}@{profile.Endpoint}. Shell state persists until disconnect.");
            StatusText.Text = $"Interactive terminal connected to {profile.Name}";
            CommandInput.Focus();
        }
        catch (OperationCanceledException)
        {
            TerminalSessionStatus.Text = "Disconnected";
            TerminalSessionStatus.Foreground = (Brush)FindResource("GhostMuted");
            StatusText.Text = "Interactive terminal connection cancelled";
        }
        catch (Exception ex)
        {
            TerminalSessionStatus.Text = "Connection failed";
            TerminalSessionStatus.Foreground = (Brush)FindResource("GhostWarning");
            AppendTerminalSystemLine($"Connection failed: {SafeError(ex)}");
            StatusText.Text = "Interactive terminal connection failed";
        }
        finally
        {
            if (ReferenceEquals(_terminalConnectCancellation, cancellation))
            {
                _terminalConnectCancellation = null;
            }

            cancellation.Dispose();
            _terminalTransitionBusy = false;
            UpdateTerminalSessionUi();
        }
    }

    private async void DisconnectTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (_terminalTransitionBusy)
        {
            return;
        }

        _terminalTransitionBusy = true;
        UpdateTerminalSessionUi();

        try
        {
            await DisconnectTerminalAsync(
                "Interactive terminal disconnected.",
                appendMessage: true);
            StatusText.Text = "Interactive terminal disconnected";
        }
        catch (Exception ex)
        {
            AppendTerminalSystemLine($"Disconnect failed: {SafeError(ex)}");
            StatusText.Text = "Interactive terminal disconnect failed";
        }
        finally
        {
            _terminalTransitionBusy = false;
            UpdateTerminalSessionUi();
        }
    }

    private async Task DisconnectTerminalAsync(
        string message,
        bool appendMessage)
    {
        _terminalConnectCancellation?.Cancel();

        var hadSession = _terminalSession.IsConnected ||
                         _terminalSession.ProfileId is not null;

        if (hadSession)
        {
            await _terminalSession.DisconnectAsync();
        }

        if (appendMessage && hadSession)
        {
            AppendTerminalSystemLine(message);
        }

        TerminalSessionStatus.Text = "Disconnected";
        TerminalSessionStatus.Foreground = (Brush)FindResource("GhostMuted");
        UpdateTerminalSessionUi();
    }

    private async void RunCommand_Click(object sender, RoutedEventArgs e)
    {
        var profile = SelectedProfile;
        if (profile is null)
        {
            AppendTerminalSystemLine("Select a server on Dashboard first.");
            return;
        }

        if (!_terminalSession.IsConnected ||
            _terminalSession.ProfileId != profile.Id)
        {
            AppendTerminalSystemLine("Connect the interactive shell before sending commands.");
            UpdateTerminalSessionUi();
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
            await _terminalSession.SendLineAsync(command);
            CommandInput.Clear();
            StatusText.Text = "Command sent to interactive shell";
        }
        catch (Exception ex)
        {
            AppendTerminalSystemLine($"Send failed: {SafeError(ex)}");
            StatusText.Text = "Terminal command failed";
            UpdateTerminalSessionUi();
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
        StatusText.Text = "Terminal output cleared locally";
        CommandInput.Focus();
    }

    private void InsertQuickCommand_Click(object sender, RoutedEventArgs e)
    {
        if (QuickCommandBox.SelectedItem is not ComboBoxItem item ||
            string.IsNullOrWhiteSpace(item.Tag?.ToString()))
        {
            return;
        }

        CommandInput.Text = item.Tag!.ToString()!;
        CommandInput.CaretIndex = CommandInput.Text.Length;
        CommandInput.Focus();
        StatusText.Text = "Quick Command inserted for review";
    }

    private void TerminalSession_OutputReceived(object? sender, TerminalOutputEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() => AppendTerminalOutput(e.Text));
    }

    private void TerminalSession_Disconnected(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!_terminalTransitionBusy)
            {
                TerminalSessionStatus.Text = "Disconnected";
                TerminalSessionStatus.Foreground = (Brush)FindResource("GhostMuted");
            }

            UpdateTerminalSessionUi();
        });
    }

    private void AppendTerminalSystemLine(string message)
    {
        var prefix = TerminalOutput.Text.Length == 0
            ? string.Empty
            : Environment.NewLine;

        AppendTerminalOutput(
            $"{prefix}[ghost] {message}{Environment.NewLine}");
    }

    private void AppendTerminalOutput(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return;
        }

        TerminalOutput.AppendText(output);

        if (TerminalOutput.Text.Length > TerminalOutputMaxCharacters)
        {
            var start = TerminalOutput.Text.Length - TerminalOutputMaxCharacters;
            TerminalOutput.Text = TerminalOutput.Text[start..];
            TerminalOutput.CaretIndex = TerminalOutput.Text.Length;
        }

        TerminalOutput.ScrollToEnd();
    }

    private void UpdateTerminalSessionUi()
    {
        var profile = SelectedProfile;
        var connectedToSelection = profile is not null &&
                                   _terminalSession.IsConnected &&
                                   _terminalSession.ProfileId == profile.Id;

        ConnectTerminalButton.IsEnabled =
            !_terminalTransitionBusy &&
            !connectedToSelection &&
            profile is not null &&
            !string.IsNullOrWhiteSpace(profile.HostKeyFingerprint);

        DisconnectTerminalButton.IsEnabled =
            !_terminalTransitionBusy &&
            _terminalSession.IsConnected;

        SendTerminalButton.IsEnabled =
            !_terminalTransitionBusy &&
            connectedToSelection;

        if (_terminalTransitionBusy)
        {
            return;
        }

        if (connectedToSelection)
        {
            TerminalSessionStatus.Text = "Connected";
            TerminalSessionStatus.Foreground = (Brush)FindResource("GhostSuccess");
        }
        else if (profile is not null &&
                 string.IsNullOrWhiteSpace(profile.HostKeyFingerprint))
        {
            TerminalSessionStatus.Text = "Trust required";
            TerminalSessionStatus.Foreground = (Brush)FindResource("GhostWarning");
        }
        else
        {
            TerminalSessionStatus.Text = "Disconnected";
            TerminalSessionStatus.Foreground = (Brush)FindResource("GhostMuted");
        }
    }

    private async void SecurityScan_Click(object sender, RoutedEventArgs e)
    {
        var operation = CaptureRemoteOperation();
        if (operation is null)
        {
            SecurityOutput.Text = "Select a server on Dashboard first.";
            return;
        }

        if (!TryBeginUiOperation(operation, "security-scan", "Security scan is already running."))
        {
            return;
        }

        try
        {
            StatusText.Text = "Running read-only security scan…";
            var output = await SshServerClient.RunSecurityScanAsync(
                operation.Profile,
                operation.Secret,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            SecurityOutput.Text = output;
            StatusText.Text = "Security scan completed";
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsRemoteOperationCurrent(operation))
            {
                SecurityOutput.Text = SafeError(ex);
                StatusText.Text = "Security scan failed";
            }
        }
        finally
        {
            EndUiOperation(operation, "security-scan");
        }
    }

    private void OpenAddServer_Click(object sender, RoutedEventArgs e)
    {
        CaptureOverlayFocus();
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

        CaptureOverlayFocus();
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
        RestoreOverlayFocus();
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

        if (editedExisting && oldProfile is not null)
        {
            CancelRemoteOperations();
        }

        if (editedExisting &&
            oldProfile is not null &&
            _terminalSession.ProfileId == oldProfile.Id)
        {
            _terminalConnectCancellation?.Cancel();
            await DisconnectTerminalAsync(
                "Terminal disconnected because the active server profile was edited.",
                appendMessage: true);
        }

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
            _focusBeforeOverlay = null;
            ServerList.SelectedItem = profile;
            ServerList.ScrollIntoView(profile);
            ServerList.Focus();
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

        CaptureOverlayFocus();
        _pendingDeleteProfile = SelectedProfile;
        ConfirmMessage.Text = $"Delete local profile “{SelectedProfile.Name}” ({SelectedProfile.Endpoint})?";
        ConfirmOverlay.Visibility = Visibility.Visible;
        ConfirmCancelButton.Focus();
    }

    private void CancelDelete_Click(object sender, RoutedEventArgs e)
    {
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        _pendingDeleteProfile = null;
        RestoreOverlayFocus();
    }

    private async void ConfirmDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingDeleteProfile is null)
        {
            ConfirmOverlay.Visibility = Visibility.Collapsed;
            RestoreOverlayFocus();
            return;
        }

        var profile = _pendingDeleteProfile;
        var index = Profiles.IndexOf(profile);
        try
        {
            CancelRemoteOperations();

            if (_terminalSession.ProfileId == profile.Id)
            {
                _terminalConnectCancellation?.Cancel();
                await DisconnectTerminalAsync(
                    "Terminal disconnected because the active server profile is being deleted.",
                    appendMessage: true);
            }

            Profiles.Remove(profile);
            await _profileStore.SaveAsync(Profiles);
            ConfirmOverlay.Visibility = Visibility.Collapsed;
            _pendingDeleteProfile = null;
            _focusBeforeOverlay = null;
            SelectedProfile = null;
            ServerList.SelectedItem = null;
            SessionSecretBox.Clear();
            EmptyState.Visibility = Visibility.Visible;
            ServerDetail.Visibility = Visibility.Collapsed;
            ServerList.Focus();
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
            RestoreOverlayFocus();
            StatusText.Text = SafeError(ex);
        }
    }

    private async void ResetHostKey_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null)
        {
            return;
        }

        CancelRemoteOperations();
        await DisconnectTerminalAsync(
            "Terminal disconnected because SSH trust was reset.",
            appendMessage: true);
        SelectedProfile.HostKeyFingerprint = null;
        try
        {
            await _profileStore.SaveAsync(Profiles);
            ConnectionStatus.Text = "Host key not approved";
            ConnectionStatus.Foreground = (Brush)FindResource("GhostWarning");
            HostKeyPanel.Visibility = Visibility.Collapsed;
            StatusText.Text = $"SSH trust reset for {SelectedProfile.Name}";
            UpdateTerminalSessionUi();
        }
        catch (Exception ex)
        {
            StatusText.Text = SafeError(ex);
        }
    }

    private async void LockSession_Click(object sender, RoutedEventArgs e)
    {
        CancelRemoteOperations();
        _terminalConnectCancellation?.Cancel();
        SessionSecretBox.Clear();
        await DisconnectTerminalAsync(
            "Terminal disconnected because the session was locked.",
            appendMessage: true);
        _dashboardTimer.Stop();
        if (AutoRefreshToggle is not null)
        {
            AutoRefreshToggle.IsChecked = false;
        }

        StatusText.Text = "Session secret cleared from memory";
        ConnectionStatus.Text = SelectedProfile is null ? "Not connected" : "Session locked";
        ConnectionStatus.Foreground = (Brush)FindResource("GhostMuted");
    }

    private async void ResetWindowLayout_Click(object sender, RoutedEventArgs e)
    {
        _settings.WindowWidth = null;
        _settings.WindowHeight = null;
        _windowSizeSettingsDirty = false;
        _windowSettingsTimer.Stop();

        RestoreComfortableWindowSize();
        _windowSettingsTimer.Stop();
        _windowSizeSettingsDirty = false;

        if (_settings.RememberWindowSize)
        {
            CaptureCurrentWindowSize();
        }

        try
        {
            await _settingsStore.SaveAsync(_settings);
            SettingsStatusText.Text = _settingsDirty
                ? "Window size reset. Other settings still have unsaved changes."
                : "Window size reset to the safe default.";
            StatusText.Text = "Window size reset";
        }
        catch (Exception ex)
        {
            SettingsStatusText.Text = SafeError(ex);
            StatusText.Text = "Window layout reset failed";
        }
    }

    private void ClearBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DefaultBackupFolderBox.Text))
        {
            return;
        }

        DefaultBackupFolderBox.Clear();
        MarkSettingsDirty();
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

        if (dialog.ShowDialog(this) == true &&
            !string.Equals(DefaultBackupFolderBox.Text, dialog.FolderName, StringComparison.OrdinalIgnoreCase))
        {
            DefaultBackupFolderBox.Text = dialog.FolderName;
            MarkSettingsDirty();
        }
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e) =>
        await SaveSettingsAsync();

    private async Task<bool> SaveSettingsAsync()
    {
        if (_settingsSaveBusy)
        {
            return false;
        }

        if (!_settingsDirty)
        {
            return true;
        }

        _settingsSaveBusy = true;
        SaveSettingsButton.IsEnabled = false;
        var saveGeneration = _settingsEditGeneration;

        try
        {
            var candidate = CreateSettingsCandidateFromUi();
            var selectedServerChangedDuringSave = false;

            await _settingsStore.SaveAsync(candidate);

            selectedServerChangedDuringSave =
                _settings.LastSelectedServerId != candidate.LastSelectedServerId;

            _settings.DashboardRefreshSeconds = candidate.DashboardRefreshSeconds;
            _settings.DefaultBackupDirectory = candidate.DefaultBackupDirectory;
            _settings.RememberWindowSize = candidate.RememberWindowSize;

            if (!candidate.RememberWindowSize)
            {
                _settings.WindowWidth = null;
                _settings.WindowHeight = null;
                _windowSizeSettingsDirty = false;
                _windowSettingsTimer.Stop();
            }
            else if (!_windowSizeSettingsDirty)
            {
                _settings.WindowWidth = candidate.WindowWidth;
                _settings.WindowHeight = candidate.WindowHeight;
            }

            _settings.Normalize();
            _dashboardTimer.Interval = TimeSpan.FromSeconds(_settings.DashboardRefreshSeconds);

            if (selectedServerChangedDuringSave)
            {
                _ = PersistSettingsQuietlyAsync();
            }

            if (saveGeneration == _settingsEditGeneration)
            {
                _settingsDirty = false;
                SettingsStatusText.Text = "Settings saved.";
                StatusText.Text = "Settings saved";
                return true;
            }

            _settingsDirty = true;
            SettingsStatusText.Text = "Settings saved. New changes are still unsaved.";
            StatusText.Text = "Settings saved";
            return false;
        }
        catch (Exception ex)
        {
            _settingsDirty = true;
            SettingsStatusText.Text = SafeError(ex);
            StatusText.Text = "Settings save failed";
            return false;
        }
        finally
        {
            _settingsSaveBusy = false;
            SaveSettingsButton.IsEnabled = _settingsDirty;
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
            if (!await ShowGhostConfirmationAsync(
                    "Import server profiles",
                    $"Import {imported.Count} validated profile(s)? Existing profiles with the same ID or SSH endpoint will be replaced. Session secrets are not imported.",
                    "Import profiles",
                    danger: false))
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
        _settingsUiUpdate = true;
        try
        {
            _dashboardTimer.Interval = TimeSpan.FromSeconds(_settings.DashboardRefreshSeconds);
            DefaultBackupFolderBox.Text = _settings.DefaultBackupDirectory ?? string.Empty;
            RememberWindowSizeToggle.IsChecked = _settings.RememberWindowSize;
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
        finally
        {
            _settingsUiUpdate = false;
        }

        _settingsDirty = false;
        SaveSettingsButton.IsEnabled = false;
    }

    private AppSettings CreateSettingsCandidateFromUi()
    {
        var candidate = new AppSettings
        {
            DashboardRefreshSeconds = ReadRefreshInterval(),
            DefaultBackupDirectory = string.IsNullOrWhiteSpace(DefaultBackupFolderBox.Text)
                ? null
                : DefaultBackupFolderBox.Text.Trim(),
            LastSelectedServerId = _settings.LastSelectedServerId,
            RememberWindowSize = RememberWindowSizeToggle.IsChecked == true,
            WindowWidth = _settings.WindowWidth,
            WindowHeight = _settings.WindowHeight
        };

        if (!candidate.RememberWindowSize)
        {
            candidate.WindowWidth = null;
            candidate.WindowHeight = null;
        }
        else if (!_fitWindowActive)
        {
            candidate.WindowWidth = Math.Round(Math.Clamp(ActualWidth, MinWidth, MaxWidth), 0);
            candidate.WindowHeight = Math.Round(Math.Clamp(ActualHeight, MinHeight, MaxHeight), 0);
        }

        candidate.Normalize();
        return candidate;
    }

    private void SettingsControl_Changed(object sender, RoutedEventArgs e) =>
        MarkSettingsDirty();

    private void MarkSettingsDirty()
    {
        if (!_settingsLoaded || _settingsUiUpdate)
        {
            return;
        }

        _settingsEditGeneration++;
        _settingsDirty = true;
        SaveSettingsButton.IsEnabled = !_settingsSaveBusy;
        SettingsStatusText.Text = "Unsaved changes.";
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

    private sealed record RemoteOperationSnapshot(
        ServerProfile Profile,
        string? Secret,
        int Generation,
        CancellationToken CancellationToken);

    private RemoteOperationSnapshot? CaptureRemoteOperation()
    {
        var profile = SelectedProfile;
        if (profile is null)
        {
            return null;
        }

        return new RemoteOperationSnapshot(
            CloneServerProfile(profile),
            SessionSecretBox.Password,
            Volatile.Read(ref _remoteOperationGeneration),
            _remoteOperationsCancellation.Token);
    }

    private bool IsRemoteOperationCurrent(RemoteOperationSnapshot operation) =>
        !operation.CancellationToken.IsCancellationRequested &&
        operation.Generation == Volatile.Read(ref _remoteOperationGeneration) &&
        SelectedProfile?.Id == operation.Profile.Id;

    private void CancelRemoteOperations()
    {
        Interlocked.Increment(ref _remoteOperationGeneration);

        var previous = _remoteOperationsCancellation;
        _remoteOperationsCancellation = new CancellationTokenSource();
        previous.Cancel();
        _retiredRemoteCancellations.Add(previous);
    }

    private bool TryBeginUiOperation(
        RemoteOperationSnapshot operation,
        string key,
        string duplicateMessage)
    {
        if (_activeUiOperations.TryGetValue(key, out var generation) &&
            generation == operation.Generation)
        {
            StatusText.Text = duplicateMessage;
            return false;
        }

        _activeUiOperations[key] = operation.Generation;
        return true;
    }

    private void EndUiOperation(RemoteOperationSnapshot operation, string key)
    {
        if (_activeUiOperations.TryGetValue(key, out var generation) &&
            generation == operation.Generation)
        {
            _activeUiOperations.Remove(key);
        }
    }

    private static ServerProfile CloneServerProfile(ServerProfile source) =>
        new()
        {
            Id = source.Id,
            Name = source.Name,
            Host = source.Host,
            Port = source.Port,
            Username = source.Username,
            Authentication = source.Authentication,
            PrivateKeyPath = source.PrivateKeyPath,
            HostKeyFingerprint = source.HostKeyFingerprint,
            LastConnectedUtc = source.LastConnectedUtc
        };

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
        if (WindowState == WindowState.Minimized || !IsVisible)
        {
            return;
        }

        var operation = CaptureRemoteOperation();
        if (_autoRefreshBusy ||
            operation is null ||
            string.IsNullOrWhiteSpace(operation.Profile.HostKeyFingerprint) ||
            AutoRefreshToggle.IsChecked != true)
        {
            return;
        }

        _autoRefreshBusy = true;
        try
        {
            var snapshot = await SshServerClient.GetSnapshotAsync(
                operation.Profile,
                operation.Secret,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            var services = await SshServerClient.GetRunningServicesAsync(
                operation.Profile,
                operation.Secret,
                operation.CancellationToken);

            if (!IsRemoteOperationCurrent(operation))
            {
                return;
            }

            ApplySnapshot(snapshot);
            ServicesList.ItemsSource = services;
            ConnectionStatus.Text = "Connected • auto-refreshed";
            ConnectionStatus.Foreground = (Brush)FindResource("GhostSuccess");
            StatusText.Text = $"Auto-refreshed {operation.Profile.Name} at {DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsRemoteOperationCurrent(operation))
            {
                _dashboardTimer.Stop();
                AutoRefreshToggle.IsChecked = false;
                ConnectionStatus.Text = "Auto-refresh stopped";
                ConnectionStatus.Foreground = (Brush)FindResource("GhostWarning");
                StatusText.Text = SafeError(ex);
            }
        }
        finally
        {
            _autoRefreshBusy = false;
        }
    }

    private SaveFileDialog CreateSnapshotSaveDialog(string title)
    {
        var serverName = SelectedProfile?.Name ?? "server";
        var invalid = Path.GetInvalidFileNameChars();
        var safeServerName = new string(
            serverName
                .Select(ch => invalid.Contains(ch) ? '_' : ch)
                .ToArray());

        if (string.IsNullOrWhiteSpace(safeServerName))
        {
            safeServerName = "server";
        }

        return new SaveFileDialog
        {
            Title = title,
            FileName = $"ghost-server-{safeServerName}-config-{DateTime.Now:yyyyMMdd-HHmmss}.tar.gz",
            Filter = "GZip archive (*.tar.gz)|*.tar.gz|All files|*.*",
            OverwritePrompt = true,
            InitialDirectory = Directory.Exists(_settings.DefaultBackupDirectory)
                ? _settings.DefaultBackupDirectory
                : null
        };
    }

    private Task<bool> ConfirmAdministrativeActionAsync(
        string title,
        string message,
        string confirmText) =>
        ShowGhostConfirmationAsync(
            title,
            message + "\n\nThe action is sent to the selected remote server.",
            confirmText,
            danger: true);

    private Task<bool> ShowGhostConfirmationAsync(
        string title,
        string message,
        string confirmText,
        bool danger)
    {
        if (_confirmationCompletion is not null)
        {
            return Task.FromResult(false);
        }

        _confirmationCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _focusBeforeOverlay = Keyboard.FocusedElement;

        GhostConfirmationTitle.Text = title;
        GhostConfirmationMessage.Text = message;
        GhostConfirmationConfirmButton.Content = confirmText;
        GhostConfirmationConfirmButton.Style = (Style)FindResource(
            danger ? "DangerButton" : "AccentButton");

        GhostConfirmationOverlay.Visibility = Visibility.Visible;
        GhostConfirmationCancelButton.Focus();

        return _confirmationCompletion.Task;
    }

    private void ConfirmGhostConfirmation_Click(object sender, RoutedEventArgs e) =>
        CompleteGhostConfirmation(true);

    private void CancelGhostConfirmation_Click(object sender, RoutedEventArgs e) =>
        CompleteGhostConfirmation(false);

    private void CompleteGhostConfirmation(bool result)
    {
        var completion = _confirmationCompletion;
        if (completion is null)
        {
            return;
        }

        _confirmationCompletion = null;
        GhostConfirmationOverlay.Visibility = Visibility.Collapsed;
        RestoreOverlayFocus();
        completion.TrySetResult(result);
    }

    private bool TryAcquireMutation(string message)
    {
        if (Interlocked.CompareExchange(ref _mutationActive, 1, 0) != 0)
        {
            StatusText.Text = "Another administrative action is already running.";
            return false;
        }

        SetAdministrativeMutationBusy(true);
        StatusText.Text = message;
        return true;
    }

    private void ReleaseAdministrativeMutation()
    {
        Interlocked.Exchange(ref _mutationActive, 0);
        SetAdministrativeMutationBusy(false);
    }

    private void SetAdministrativeMutationBusy(bool busy)
    {
        foreach (var button in GetAdministrativeMutationButtons())
        {
            button.IsEnabled = !busy;
        }
    }

    private IEnumerable<Button> GetAdministrativeMutationButtons()
    {
        yield return StartServiceButton;
        yield return StopServiceButton;
        yield return RestartServiceButton;
        yield return StartDockerButton;
        yield return StopDockerButton;
        yield return RestartDockerButton;
        yield return AllowFirewallPortButton;
        yield return RunSafeUpdateButton;
        yield return RestoreConfigSnapshotButton;
        yield return CreateConfigBackupButton;
        yield return CreateTaskButton;
        yield return DeleteTaskButton;
        yield return TerminateProcessButton;
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
            if (GhostConfirmationOverlay.Visibility == Visibility.Visible)
            {
                CancelGhostConfirmation_Click(sender, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            if (UnsavedSettingsOverlay.Visibility == Visibility.Visible)
            {
                CancelUnsavedSettingsClose_Click(sender, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

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

        if (GhostConfirmationOverlay.Visibility == Visibility.Visible ||
            UnsavedSettingsOverlay.Visibility == Visibility.Visible ||
            ConfirmOverlay.Visibility == Visibility.Visible ||
            AddServerOverlay.Visibility == Visibility.Visible)
        {
            return;
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

        if (FleetPage.Visibility == Visibility.Visible)
        {
            RefreshFleetInventory();
        }
        else if (AlertsPage.Visibility == Visibility.Visible)
        {
            RefreshAlertCenter();
        }
        else if (TrendsPage.Visibility == Visibility.Visible)
        {
            RefreshTrends();
        }
        else if (FilesPage.Visibility == Visibility.Visible)
        {
            await RefreshFilesAsync();
        }
        else if (ServicesPage.Visibility == Visibility.Visible)
        {
            await RefreshManagerServicesAsync();
        }
        else if (DockerPage.Visibility == Visibility.Visible)
        {
            await RefreshDockerAsync();
        }
        else if (NetworkPage.Visibility == Visibility.Visible)
        {
            await RefreshNetworkAsync();
        }
        else if (UpdatesPage.Visibility == Visibility.Visible)
        {
            await RefreshUpdatesAsync();
        }
        else if (TasksPage.Visibility == Visibility.Visible)
        {
            await RefreshTasksAsync();
        }
        else if (SystemPage.Visibility == Visibility.Visible)
        {
            await RefreshSystemAsync();
        }
        else if (DatabasesPage.Visibility == Visibility.Visible)
        {
            await RefreshDatabasesAsync();
        }
        else if (LogsPage.Visibility == Visibility.Visible)
        {
            await RefreshLogsAsync();
        }
        else if (SecurityPage.Visibility == Visibility.Visible)
        {
            SecurityScan_Click(sender, new RoutedEventArgs());
        }
        else if (DashboardPage.Visibility == Visibility.Visible)
        {
            Connect_Click(sender, new RoutedEventArgs());
        }
    }

    private void ConfigureInitialWindowBounds()
    {
        var workArea = SystemParameters.WorkArea;
        var maxWidth = Math.Max(MinWidth, workArea.Width * 0.94);
        var maxHeight = Math.Max(MinHeight, workArea.Height * 0.92);

        MaxWidth = maxWidth;
        MaxHeight = maxHeight;
        WindowState = WindowState.Normal;

        Width = Math.Min(StandardWindowWidth, Math.Max(MinWidth, workArea.Width * 0.84));
        Height = Math.Min(StandardWindowHeight, Math.Max(MinHeight, workArea.Height * 0.82));

        CenterWithinWorkArea(workArea);
        _fitWindowActive = false;
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        ApplyResponsiveLayout();

        if (!IsLoaded ||
            !_settingsLoaded ||
            _fitWindowActive ||
            !_settings.RememberWindowSize ||
            ActualWidth < MinWidth ||
            ActualHeight < MinHeight)
        {
            return;
        }

        CaptureCurrentWindowSize();
        _windowSizeSettingsDirty = true;
        _windowSettingsTimer.Stop();
        _windowSettingsTimer.Start();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (_windowStateCorrection || WindowState != WindowState.Maximized)
        {
            return;
        }

        _windowStateCorrection = true;
        try
        {
            WindowState = WindowState.Normal;
            FitWindowToWorkArea();
        }
        finally
        {
            _windowStateCorrection = false;
        }
    }

    private void ToggleFitWindow()
    {
        if (_fitWindowActive)
        {
            RestoreComfortableWindowSize();
        }
        else
        {
            FitWindowToWorkArea();
        }
    }

    private void FitWindowToWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        WindowState = WindowState.Normal;
        _fitWindowActive = true;
        _windowSettingsTimer.Stop();
        MaxWidth = Math.Max(MinWidth, workArea.Width * 0.94);
        MaxHeight = Math.Max(MinHeight, workArea.Height * 0.92);
        Width = Math.Min(MaxWidth, Math.Max(MinWidth, workArea.Width * 0.90));
        Height = Math.Min(MaxHeight, Math.Max(MinHeight, workArea.Height * 0.88));
        CenterWithinWorkArea(workArea);
        ApplyResponsiveLayout();
    }

    private void RestoreComfortableWindowSize()
    {
        var workArea = SystemParameters.WorkArea;
        WindowState = WindowState.Normal;
        _fitWindowActive = false;
        MaxWidth = Math.Max(MinWidth, workArea.Width * 0.94);
        MaxHeight = Math.Max(MinHeight, workArea.Height * 0.92);
        Width = Math.Min(StandardWindowWidth, Math.Min(MaxWidth, Math.Max(MinWidth, workArea.Width * 0.82)));
        Height = Math.Min(StandardWindowHeight, Math.Min(MaxHeight, Math.Max(MinHeight, workArea.Height * 0.80)));
        CenterWithinWorkArea(workArea);
        ApplyResponsiveLayout();
    }

    private void CenterWithinWorkArea(Rect workArea)
    {
        Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
        Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2);
    }

    private void ApplySavedWindowSize()
    {
        if (!_settings.RememberWindowSize ||
            _settings.WindowWidth is not double savedWidth ||
            _settings.WindowHeight is not double savedHeight)
        {
            return;
        }

        var workArea = SystemParameters.WorkArea;
        MaxWidth = Math.Max(MinWidth, workArea.Width * 0.94);
        MaxHeight = Math.Max(MinHeight, workArea.Height * 0.92);
        Width = Math.Clamp(savedWidth, MinWidth, MaxWidth);
        Height = Math.Clamp(savedHeight, MinHeight, MaxHeight);
        WindowState = WindowState.Normal;
        _fitWindowActive = false;
        CenterWithinWorkArea(workArea);
        ApplyResponsiveLayout();
    }

    private void CaptureCurrentWindowSize()
    {
        if (WindowState != WindowState.Normal || _fitWindowActive)
        {
            return;
        }

        _settings.WindowWidth = Math.Round(Math.Clamp(ActualWidth, MinWidth, MaxWidth), 0);
        _settings.WindowHeight = Math.Round(Math.Clamp(ActualHeight, MinHeight, MaxHeight), 0);
    }

    private async void WindowSettingsTimer_Tick(object? sender, EventArgs e)
    {
        _windowSettingsTimer.Stop();
        if (!_windowSizeSettingsDirty)
        {
            return;
        }

        try
        {
            await _settingsStore.SaveAsync(_settings);
            _windowSizeSettingsDirty = false;
        }
        catch
        {
            // Keep the dirty flag so close-time flushing can retry.
            _windowSizeSettingsDirty = true;
        }
    }

    private void CaptureOverlayFocus()
    {
        if (AddServerOverlay.Visibility != Visibility.Visible &&
            ConfirmOverlay.Visibility != Visibility.Visible)
        {
            _focusBeforeOverlay = Keyboard.FocusedElement;
        }
    }

    private void RestoreOverlayFocus()
    {
        var target = _focusBeforeOverlay;
        _focusBeforeOverlay = null;

        _ = Dispatcher.InvokeAsync(
            () =>
            {
                if (target is UIElement element && element.IsVisible && element.IsEnabled)
                {
                    Keyboard.Focus(element);
                    return;
                }

                DashboardNavButton.Focus();
            },
            DispatcherPriority.Input);
    }

    private void ApplyResponsiveLayout()
    {
        if (!IsLoaded)
        {
            return;
        }

        var compactSidebar = ActualWidth < CompactSidebarBreakpoint;
        var tightLayout = ActualWidth < TightLayoutBreakpoint;
        var reducedHeight = ActualHeight < 700;
        var narrowDashboard = ActualWidth < 760;

        // Modal cards track the live window size continuously. The heavier workspace
        // layout work below only runs when a responsive breakpoint actually changes.
        AddServerCard.Width = Math.Min(560, Math.Max(320, ActualWidth - 48));
        AddServerCard.MaxHeight = Math.Max(300, ActualHeight - 48);
        ConfirmCard.Width = Math.Min(470, Math.Max(300, ActualWidth - 48));
        GhostConfirmationCard.Width = Math.Min(520, Math.Max(300, ActualWidth - 48));
        UnsavedSettingsCard.Width = Math.Min(500, Math.Max(300, ActualWidth - 48));

        var signature =
            (compactSidebar ? 1 : 0) |
            (tightLayout ? 2 : 0) |
            (reducedHeight ? 4 : 0) |
            (narrowDashboard ? 8 : 0);

        if (_responsiveLayoutSignature == signature)
        {
            return;
        }

        _responsiveLayoutSignature = signature;

        SidebarColumn.Width = new GridLength(compactSidebar ? 72 : 198);
        SidebarInnerGrid.Margin = new Thickness(compactSidebar ? 8 : 12);

        SidebarTitle.Visibility = compactSidebar ? Visibility.Collapsed : Visibility.Visible;
        SidebarOverviewHeading.Visibility = compactSidebar ? Visibility.Collapsed : Visibility.Visible;
        SidebarManageHeading.Visibility = compactSidebar ? Visibility.Collapsed : Visibility.Visible;
        SidebarOperationsHeading.Visibility = compactSidebar ? Visibility.Collapsed : Visibility.Visible;
        SidebarAppHeading.Visibility = compactSidebar ? Visibility.Collapsed : Visibility.Visible;
        SidebarFooter.Visibility = compactSidebar || reducedHeight
            ? Visibility.Collapsed
            : Visibility.Visible;

        foreach (var button in SidebarNavigationPanel.Children.OfType<Button>())
        {
            if (button.Visibility != Visibility.Visible || button.Content is not Grid navGrid ||
                navGrid.ColumnDefinitions.Count < 2)
            {
                continue;
            }

            navGrid.ColumnDefinitions[0].Width = compactSidebar
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(32);
            navGrid.ColumnDefinitions[1].Width = compactSidebar
                ? new GridLength(0)
                : new GridLength(1, GridUnitType.Star);
        }

        ContentHeaderRow.Height = new GridLength(reducedHeight ? 68 : 84);
        ContentHeaderGrid.Margin = new Thickness(compactSidebar ? 14 : 24, 0, compactSidebar ? 14 : 24, 0);
        PageTitle.FontSize = compactSidebar ? 20 : 24;
        PageSubtitle.Visibility = tightLayout || reducedHeight
            ? Visibility.Collapsed
            : Visibility.Visible;
        HeaderAddServerText.Visibility = tightLayout ? Visibility.Collapsed : Visibility.Visible;
        StatusShortcutText.Visibility = compactSidebar
            ? Visibility.Collapsed
            : Visibility.Visible;

        DashboardServerColumn.Width = new GridLength(
            narrowDashboard
                ? 190
                : compactSidebar
                    ? 235
                    : 300);

        var workspaceMargin = new Thickness(compactSidebar ? 14 : 24);
        foreach (var page in GetResponsiveWorkspacePages())
        {
            page.Margin = workspaceMargin;
        }

        SettingsPage.Margin = workspaceMargin;
    }

    private IEnumerable<FrameworkElement> GetResponsiveWorkspacePages()
    {
        yield return FleetPage;
        yield return AlertsPage;
        yield return TrendsPage;
        yield return FilesPage;
        yield return ServicesPage;
        yield return DockerPage;
        yield return TasksPage;
        yield return SystemPage;
        yield return DatabasesPage;
        yield return LogsPage;
        yield return TerminalPage;
        yield return NetworkPage;
        yield return UpdatesPage;
        yield return BackupPage;
        yield return SecurityPage;
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowCloseAfterSettingsFlush || !_settingsLoaded)
        {
            return;
        }

        if (GhostConfirmationOverlay.Visibility == Visibility.Visible)
        {
            e.Cancel = true;
            CompleteGhostConfirmation(false);
            StatusText.Text = "Confirmation cancelled";
            return;
        }

        if (_settingsSaveBusy)
        {
            e.Cancel = true;
            StatusText.Text = "Settings are still saving. Close again when the save finishes.";
            return;
        }

        if (Volatile.Read(ref _mutationActive) != 0)
        {
            e.Cancel = true;

            var closeAnyway = await ShowGhostConfirmationAsync(
                "Administrative action in progress",
                "A remote administrative action is still running. Closing Ghost Server will stop local waiting and close the current operation context, but a command already started on the server may continue or may have partially completed. Keep Ghost Server open until the action finishes unless you intentionally want to stop monitoring it.",
                "Close anyway",
                danger: true);

            if (!closeAnyway)
            {
                StatusText.Text = "Close cancelled; administrative action is still running.";
                return;
            }

            CancelRemoteOperations();

            if (_settingsDirty)
            {
                ShowUnsavedSettingsCloseOverlay();
                return;
            }

            await FlushWindowSettingsAndCloseAsync();
            return;
        }

        if (_settingsDirty)
        {
            e.Cancel = true;
            ShowUnsavedSettingsCloseOverlay();
            return;
        }

        if (!_windowSizeSettingsDirty || !_settings.RememberWindowSize)
        {
            return;
        }

        e.Cancel = true;
        await FlushWindowSettingsAndCloseAsync();
    }

    private void ShowUnsavedSettingsCloseOverlay()
    {
        if (UnsavedSettingsOverlay.Visibility != Visibility.Visible)
        {
            _focusBeforeOverlay = Keyboard.FocusedElement;
            UnsavedSettingsOverlay.Visibility = Visibility.Visible;
        }

        UnsavedSettingsCancelButton.Focus();
    }

    private void CancelUnsavedSettingsClose_Click(object sender, RoutedEventArgs e)
    {
        UnsavedSettingsOverlay.Visibility = Visibility.Collapsed;
        RestoreOverlayFocus();
        StatusText.Text = "Close cancelled";
    }

    private async void DiscardUnsavedSettingsClose_Click(object sender, RoutedEventArgs e)
    {
        _settingsDirty = false;
        SaveSettingsButton.IsEnabled = false;
        UnsavedSettingsOverlay.Visibility = Visibility.Collapsed;
        await FlushWindowSettingsAndCloseAsync();
    }

    private async void SaveUnsavedSettingsClose_Click(object sender, RoutedEventArgs e)
    {
        if (!await SaveSettingsAsync())
        {
            UnsavedSettingsCancelButton.Focus();
            return;
        }

        UnsavedSettingsOverlay.Visibility = Visibility.Collapsed;
        await FlushWindowSettingsAndCloseAsync();
    }

    private async Task FlushWindowSettingsAndCloseAsync()
    {
        _windowSettingsTimer.Stop();

        if (_settingsLoaded &&
            _windowSizeSettingsDirty &&
            _settings.RememberWindowSize)
        {
            CaptureCurrentWindowSize();

            try
            {
                await _settingsStore.SaveAsync(_settings);
                _windowSizeSettingsDirty = false;
            }
            catch
            {
                // Window geometry persistence is best-effort and must never trap the user.
            }
        }

        _allowCloseAfterSettingsFlush = true;
        Close();
    }

    private void Window_Closed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _windowDisposed, 1) != 0)
        {
            return;
        }

        _dashboardTimer.Stop();
        _windowSettingsTimer.Stop();
        _terminalConnectCancellation?.Cancel();
        _terminalConnectCancellation?.Dispose();
        _terminalConnectCancellation = null;
        _remoteOperationsCancellation.Cancel();
        _remoteOperationsCancellation.Dispose();
        foreach (var retiredCancellation in _retiredRemoteCancellations)
        {
            retiredCancellation.Dispose();
        }

        _retiredRemoteCancellations.Clear();
        _activeUiOperations.Clear();
        _terminalSession.Dispose();
        GC.SuppressFinalize(this);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsInsideButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleFitWindow();
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

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleFitWindow();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
