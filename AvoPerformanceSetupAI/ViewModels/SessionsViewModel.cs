using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using AvoPerformanceSetupAI.Models;
using AvoPerformanceSetupAI.Services;
using AvoPerformanceSetupAI.Services.Agent;
using AvoPerformanceSetupAI.Services.Setup;

namespace AvoPerformanceSetupAI.ViewModels;

[SupportedOSPlatform("windows10.0.17763.0")]
public partial class SessionsViewModel : ObservableObject
{
    /// <summary>Application-wide shared instance used by all pages.</summary>
    public static SessionsViewModel Shared { get; } = new SessionsViewModel();

    // ── Config fields ────────────────────────────────────────────────────────
    [ObservableProperty] private string _carId = string.Empty;
    [ObservableProperty] private string _trackId = string.Empty;
    [ObservableProperty] private string _setupSource = "Local File";
    [ObservableProperty] private string _mode = "Hotlap";

    // ── Status ───────────────────────────────────────────────────────────────
    [ObservableProperty] private string _statusText = "● READY";
    [ObservableProperty] private string _brainInfo = "Local AI (C#)";
    [ObservableProperty] private string _riskLevel = "LOW";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isConnected;

    // ── Agent live state (polled every 1 s in Remote mode) ───────────────────
    /// <summary>True when the Agent last reported AC as running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyProposalPublic))]
    [NotifyPropertyChangedFor(nameof(LiveApplyBlockedReason))]
    [NotifyPropertyChangedFor(nameof(ApplyButtonTooltip))]
    [NotifyCanExecuteChangedFor(nameof(ApplyProposalCommand))]
    private bool _isAcRunning;

    /// <summary>True when the Agent has a valid shared-memory connection.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyProposalPublic))]
    [NotifyPropertyChangedFor(nameof(LiveApplyBlockedReason))]
    [NotifyPropertyChangedFor(nameof(ApplyButtonTooltip))]
    [NotifyCanExecuteChangedFor(nameof(ApplyProposalCommand))]
    private bool _isSharedMemoryConnected;

    /// <summary>Car folder name currently active in the simulator.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyProposalPublic))]
    [NotifyPropertyChangedFor(nameof(LiveApplyBlockedReason))]
    [NotifyPropertyChangedFor(nameof(ApplyButtonTooltip))]
    [NotifyCanExecuteChangedFor(nameof(ApplyProposalCommand))]
    private string _activeCarId = string.Empty;

    /// <summary>True when the Agent responded to the last state poll.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyProposalPublic))]
    [NotifyPropertyChangedFor(nameof(LiveApplyBlockedReason))]
    [NotifyPropertyChangedFor(nameof(ApplyButtonTooltip))]
    [NotifyPropertyChangedFor(nameof(ApplyButtonLabel))]
    [NotifyCanExecuteChangedFor(nameof(ApplyProposalCommand))]
    private bool _isAgentReachable;

    /// <summary>
    /// Human-readable reason why Live Apply is currently blocked, or null if not blocked.
    /// Shown as a tooltip on the disabled Apply button.
    /// </summary>
    public string? LiveApplyBlockedReason
    {
        get
        {
            if (IsHotlapMode) return null;
            if (!IsAgentReachable)          return "Agent not reachable — check connection settings.";
            if (!IsAcRunning)               return "Assetto Corsa is not running.";
            if (!IsSharedMemoryConnected)   return "Shared memory not connected.";
            if (!string.IsNullOrEmpty(CarId) &&
                !string.IsNullOrEmpty(ActiveCarId) &&
                !ActiveCarId.Equals(CarId, StringComparison.OrdinalIgnoreCase))
                return $"Car mismatch: selected '{CarId}' but AC has '{ActiveCarId}'.";
            return null;
        }
    }

    // 1-second background timer for agent state polling
    private System.Threading.Timer? _agentPollTimer;
    private bool _prevAcRunning;
    private bool _prevSharedMem;

    // Auto AI proposal loop (client-side). This never auto-applies.
    private System.Threading.Timer? _autoAiTimer;
    private DateTime _lastAutoAiUtc = DateTime.MinValue;

    // User toggle: allow auto-proposals in LIVE mode while AC is running.
    // Default true because proposals still require manual Apply.
    [ObservableProperty]
    private bool _autoAiEnabled = true;

    // Minimum seconds between auto proposal runs.
    [ObservableProperty]
    private int _autoAiIntervalSeconds = 15;

    /// <summary>True when the app is configured for Remote Agent mode.</summary>
    public bool IsRemoteMode => SetupSettings.Instance.Mode == AppMode.Remote;

    /// <summary>
    /// True when the current session mode is "Hotlap" (offline/save-only).
    /// In this mode the apply button acts as a pure save operation and
    /// <c>appliedOk == false</c> is never surfaced as a warning.
    /// </summary>
    public bool IsHotlapMode =>
        string.Equals(Mode, "Hotlap", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Label shown on the Apply/Save button.
    /// Changes reactively when <see cref="Mode"/> changes.
    /// </summary>
    /// <summary>
    /// Label shown on the RUN AI button (IA tab).
    /// </summary>
    public string RunButtonLabel => IsHotlapMode ? "▶ RUN (Hotlap)" : "▶ RUN (Live)";

    public string ApplyButtonLabel
        => (IsRemoteMode || IsAgentReachable) ? "💾 Save Proposal (REMOTE)" : "💾 Apply Proposal";

    /// <summary>
    /// Returns <see langword="false"/> when a remote Agent is active, signalling that
    /// automatic local-disk writes of versioned setup files are prohibited.
    /// A connected Agent is the authoritative save target; no local fallback is allowed.
    /// </summary>
    private bool ShouldWriteLocalIters()
        => !(IsAgentReachable && !string.IsNullOrWhiteSpace(SetupSettings.Instance.AgentBaseUrl));

    /// <summary>
    /// Tooltip shown under the Apply/Save button.
    /// </summary>
    public string ApplyButtonTooltip
        => "Builds and saves a versioned INI file with the AI proposals applied.";

    /// <summary>
    /// Short connection status badge text for the Telemetry page header.
    /// "REMOTE CONNECTED" / empty.
    /// </summary>
    [ObservableProperty] private string _agentStatusText = string.Empty;

    // ── Selected items ───────────────────────────────────────────────────────
    [ObservableProperty] private string? _selectedSetupFile;
    [ObservableProperty] private SetupIteration? _selectedIteration;

    /// <summary>Controls hint text visibility: Visible when no files are loaded.</summary>
    [ObservableProperty] private Visibility _setupFilesHintVisibility = Visibility.Visible;

    // ── Backup path for Rollback ─────────────────────────────────────────────
    private string? _backupPath;
    private const string BackupExtension = ".bak";

    // ── Base INI text (captured on Load, used for diff after Apply) ───────────
    private string _baseIniText = string.Empty;

    // ── Apply state ──────────────────────────────────────────────────────────
    /// <summary>True while an Apply operation is in progress — disables the Apply button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyProposalPublic))]
    private bool _isApplying;

    /// <summary>Label shown after a successful Apply, e.g. "base.ini → versioned__AI__v001.ini".</summary>
    [ObservableProperty] private string _appliedFileLabel = string.Empty;

    /// <summary>Surfaced for XAML CanExecute binding — mirrors the private <c>CanApplyProposal()</c>.</summary>
    public bool CanApplyProposalPublic => CanApplyProposal();

    /// <summary>
    /// True when <see cref="LastProposals"/> was populated by a RUN action and the proposal
    /// has not yet been applied or cleared.  Used to show/hide the proposals panel in the UI.
    /// </summary>
    public bool HasProposal => _hasProposalFromRun && LastProposals.Count > 0;

    /// <summary><see cref="Visibility.Visible"/> when <see cref="HasProposal"/> is true.</summary>
    public Visibility ProposalVisibility => HasProposal ? Visibility.Visible : Visibility.Collapsed;

    /// <summary><see cref="Visibility.Visible"/> when <see cref="HasProposal"/> is false (placeholder).</summary>
    public Visibility NoProposalVisibility => HasProposal ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Internal flag set to <see langword="true"/> only when the RUN command populates proposals.</summary>
    private bool _hasProposalFromRun;

    // ── Known simulator process names ────────────────────────────────────────
    private static readonly string[] SimProcessNames =
        ["acs", "AC2-Win64-Shipping", "AssettoCorsaCompetizione", "ACCS", "acc"];

    // ── Keys that carry integer selectors, not tunable numeric values ────────
    private static readonly HashSet<string> NonTunableKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "VERSION", "CARNAME", "TYPE_FRONT", "TYPE_REAR", "TYPE"
    };

    // ── Dynamic collections from file system ─────────────────────────────────
    public ObservableCollection<string> Cars { get; } = new();
    public ObservableCollection<string> Tracks { get; } = new();
    public ObservableCollection<string> SetupFiles { get; } = new();

    // ── Collections ───────────────────────────────────────────────────────────
    public ObservableCollection<SetupIteration> Iterations { get; } = new();
    public ObservableCollection<Proposal> LastProposals { get; } = new();

    /// <summary>
    /// Diagnostic log entries produced by Apply Proposal and similar commands.
    /// Forwarded to the Agent Logs tab by <see cref="TelemetryViewModel"/>.
    /// </summary>
    public ObservableCollection<AgentLogEntry> Logs { get; } = new();

    private void AddLog(string msg, string lvl = "SYS") =>
        Logs.Add(new AgentLogEntry
        {
            TUtc = DateTime.UtcNow.ToString("O"),
            Lvl  = lvl,
            Cat  = "Client",
            Msg  = msg,
        });

    /// <summary>
    /// All numeric tunable parameters from the currently loaded setup file,
    /// classified by <c>SetupParameterClassifier</c>.
    /// Consumed by the Setup Diff feature.
    /// </summary>
    public ObservableCollection<SetupParameter> ParsedParameters { get; } = new();

    /// <summary>The parameter universe built from the currently loaded setup INI file.
    /// Null when no setup is loaded. Used to restrict proposals to keys that exist.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UniverseInfo))]
    [NotifyPropertyChangedFor(nameof(CategoryCountInfo))]
    [NotifyPropertyChangedFor(nameof(CurrentSetupAllowlist))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private SetupParamUniverse? _currentUniverse;

    /// <summary>Human-readable summary of available parameters for the UI.</summary>
    public string UniverseInfo =>
        _currentUniverse is null
            ? "Selecciona y carga un setup primero."
            : $"Keys disponibles: {_currentUniverse.NumericCount} numéricos / {_currentUniverse.KeyCount} total";

    /// <summary>
    /// Current setup allowlist in <c>"[SECTION]KEY"</c> format.
    /// Populated when a setup file is loaded; <see langword="null"/> otherwise.
    /// RUN button is disabled while this is null or empty.
    /// </summary>
    public IReadOnlySet<string>? CurrentSetupAllowlist => CurrentUniverse?.AllowlistKeys;

    // ── Category filter ───────────────────────────────────────────────────────

    /// <summary>All category names, including the "All" catch-all option.</summary>
    public IReadOnlyList<string> Categories { get; } =
        new[] { "All" }.Concat(Enum.GetNames<SetupCategory>()).ToList();

    /// <summary>Currently selected category filter. Changing it rebuilds <see cref="LastProposals"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CategoryCountInfo))]
    private string _selectedCategory = "All";

    /// <summary>Shows how many numeric keys are available in the selected category.</summary>
    public string CategoryCountInfo
    {
        get
        {
            if (_currentUniverse is null) return string.Empty;
            var cat = _selectedCategory;
            if (string.IsNullOrEmpty(cat) || cat == "All") return string.Empty;
            if (!Enum.TryParse<SetupCategory>(cat, out var selectedCat)) return string.Empty;
            if (!_currentUniverse.ByCategory.TryGetValue(selectedCat, out var keys))
                return $"Disponible: 0 numéricos en {cat}";
            var numericCount = keys.Count(k => k.IsNumeric);
            return $"Disponible: {numericCount} numéricos en {cat}";
        }
    }

    // Cached parsed entries from the last successful INI read, used to rebuild proposals
    // when only the category filter changes (avoids re-reading the file from disk/network).
    private List<AvoPerformanceSetupAI.Models.IniEntry>? _cachedEntries;

    public ObservableCollection<string> SetupSources { get; } = new() { "Local File", "Server", "Git Repo" };
    public ObservableCollection<string> Modes { get; } = new() { "Hotlap", "Race", "Qualify", "Simulation" };

    // ── AI settings ───────────────────────────────────────────────────────────

    /// <summary>Engine types the user can select in the IA tab.</summary>
    public IReadOnlyList<string> EngineTypes { get; } =
        new[] { "Heuristic", "ML", "Adaptive" };

    /// <summary>Whether AI proposal generation is enabled. Persisted to settings.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isAiEnabled = true;

    /// <summary>AI engine type selected by the user, e.g. "Heuristic".</summary>
    [ObservableProperty]
    private string _aiEngineType = "Heuristic";

    /// <summary>Timestamp of the last RUN execution, or null.</summary>
    [ObservableProperty]
    private DateTime? _lastAiRunAt;

    /// <summary>Human-readable last-run timestamp.</summary>
    public string LastAiRunText =>
        _lastAiRunAt is null ? "—" : _lastAiRunAt.Value.ToString("HH:mm:ss");

    /// <summary>
    /// Driver vs Setup discrimination result updated after each RUN.
    /// Uses the explanation string from <see cref="AvoPerformanceSetupAI.Telemetry.DriverVsSetupDiscriminator"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DriverVsSetupText))]
    private AvoPerformanceSetupAI.Telemetry.RootCauseResult _lastRootCauseResult;

    /// <summary>Human-readable driver vs setup result for the IA tab.</summary>
    public string DriverVsSetupText =>
        _lastRootCauseResult.Cause == AvoPerformanceSetupAI.Telemetry.RootCauseType.Unknown
            ? "—"
            : _lastRootCauseResult.Explanation ?? _lastRootCauseResult.Cause.ToString();

    // ── Simulation plan engine ────────────────────────────────────────────────

    /// <summary>IDs blocked from re-selection within the last <see cref="RecentHardBlock"/> picks.</summary>
    private const int RecentHardBlock = 10;

    /// <summary>Capacity of the rolling recent-keys window.</summary>
    private const int RecentWindowSize = 30;

    /// <summary>
    /// Rolling FIFO of recently proposed "SECTION.KEY" ids (anti-repeat).
    /// Only accessed from the UI thread (property/command handlers), so no locking needed.
    /// </summary>
    private readonly Queue<string> _recentProposalKeys = new(RecentWindowSize);

    /// <summary>
    /// Round-robin cursor used when no explicit signal drives category selection.
    /// Only accessed from the UI thread.
    /// </summary>
    private int _simCatIndex;

    /// <summary>
    /// Categories cycled in round-robin order when no telemetry signal is available.
    /// Ordered by typical lap-time impact so diversity stays meaningful.
    /// </summary>
    private static readonly SetupCategory[] SimRoundRobinCats =
    [
        SetupCategory.Tyres,
        SetupCategory.Alignment,
        SetupCategory.Aero,
        SetupCategory.Suspension,
        SetupCategory.Drivetrain,
        SetupCategory.Electronics,
        SetupCategory.Brakes,
        SetupCategory.Gearing,
    ];

    public SessionsViewModel()
    {
        // Subscribe to root-folder and mode changes from Configuración
        SetupSettings.Instance.PropertyChanged += OnSettingsChanged;

        // Keep HasProposal and command availability in sync with the proposals collection.
        LastProposals.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasProposal));
            OnPropertyChanged(nameof(ProposalVisibility));
            OnPropertyChanged(nameof(NoProposalVisibility));
            OnPropertyChanged(nameof(CanApplyProposalPublic));
            ApplyProposalCommand.NotifyCanExecuteChanged();
        };

        // If a root folder is already configured, populate cars immediately
        if (!string.IsNullOrEmpty(SetupSettings.Instance.RootFolder))
            _ = LoadCarsAsync(SetupSettings.Instance.RootFolder);

        // Start 1-second Agent state polling when in Remote mode.
        if (SetupSettings.Instance.Mode == AppMode.Remote)
            StartAgentPolling();

        // Load persisted AI settings.
        LoadAiSettings();

        AppLogger.Instance.Data($"Sesión inicializada — Modo: {Mode}");
        AppLogger.Instance.Ai($"Motor IA listo — {BrainInfo}");
        AppLogger.Instance.Info($"Nivel de riesgo actual: {RiskLevel}");
    }

    // ── AI settings persistence ───────────────────────────────────────────────

    private const string AiEnabledKey    = "AiEnabled";
    private const string AiEngineTypeKey = "AiEngineType";

    private void LoadAiSettings()
    {
        try
        {
            var local = Windows.Storage.ApplicationData.Current.LocalSettings;
            _isAiEnabled    = local.Values[AiEnabledKey]    is bool b  ? b  : true;
            _aiEngineType   = local.Values[AiEngineTypeKey] as string  ?? "Heuristic";
        }
        catch { /* unpackaged or first-run — keep defaults */ }
    }

    partial void OnIsAiEnabledChanged(bool value)
    {
        try
        {
            Windows.Storage.ApplicationData.Current.LocalSettings.Values[AiEnabledKey] = value;
        }
        catch { }
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnAiEngineTypeChanged(string value)
    {
        try
        {
            Windows.Storage.ApplicationData.Current.LocalSettings.Values[AiEngineTypeKey] = value;
        }
        catch { }
    }

    // ── Provider factory ──────────────────────────────────────────────────────

    /// <summary>
    /// Cached remote client; recreated whenever the remote connection settings change.
    /// Disposed together with the provider when a new one is created.
    /// </summary>
    private AgentApiClient? _cachedAgentClient;
    private (string host, int port, string token) _cachedClientKey;

    private AgentApiClient GetOrCreateAgentClient()
    {
        var s = SetupSettings.Instance;
        var key = (s.RemoteHost, s.RemotePort, s.RemoteToken);
        if (_cachedAgentClient is null || _cachedClientKey != key)
        {
            _cachedAgentClient?.Dispose();
            _cachedAgentClient = new AgentApiClient(s.RemoteHost, s.RemotePort, s.RemoteToken);
            _cachedClientKey   = key;
        }
        return _cachedAgentClient;
    }

    private ISetupLibraryProvider CreateProvider()
    {
        if (SetupSettings.Instance.Mode == AppMode.Remote)
            return new RemoteSetupLibraryProvider(GetOrCreateAgentClient());

        return new LocalSetupLibraryProvider((Application.Current as App)!.MainWindow!);
    }

    /// <summary>
    /// Public accessor so adjacent ViewModels (e.g. SetupDiffViewModel) can load
    /// setup files using the same provider strategy (Local / Remote) as the sessions page.
    /// </summary>
    internal ISetupLibraryProvider CreateProviderPublic() => CreateProvider();

    private ISetupSaver CreateSaver()
    {
        if (SetupSettings.Instance.Mode == AppMode.Remote)
            return new RemoteSetupSaver(GetOrCreateAgentClient());

        return new LocalSetupSaver();
    }

    // ── Settings change handler ───────────────────────────────────────────────

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SetupSettings.RootFolder):
                _ = LoadCarsAsync(SetupSettings.Instance.RootFolder);
                break;
            case nameof(SetupSettings.Mode):
                OnPropertyChanged(nameof(IsRemoteMode));
                OnPropertyChanged(nameof(ApplyButtonLabel));
                _ = LoadCarsAsync(SetupSettings.Instance.RootFolder);
                // Start or stop agent polling when app mode changes.
                if (SetupSettings.Instance.Mode == AppMode.Remote)
                    StartAgentPolling();
                else
                    StopAgentPolling();
                break;
            case nameof(SetupSettings.RemoteHost):
            case nameof(SetupSettings.RemotePort):
            case nameof(SetupSettings.RemoteToken):
                // Re-load if already in Remote mode
                if (IsRemoteMode)
                    _ = LoadCarsAsync(SetupSettings.Instance.RootFolder);
                break;
        }
    }

    // ── Agent state polling ───────────────────────────────────────────────────

    private void StartAgentPolling()
    {
        _agentPollTimer?.Dispose();
        _agentPollTimer = new System.Threading.Timer(
            _ => _ = PollAgentStateAsync(),
            null,
            dueTime: TimeSpan.Zero,
            period: TimeSpan.FromSeconds(1));
    }

    private void StopAgentPolling()
    {
        _agentPollTimer?.Dispose();
        _agentPollTimer = null;
        // Reset state to show unavailable when polling stops.
        IsAgentReachable        = false;
        IsAcRunning             = false;
        IsSharedMemoryConnected = false;
        ActiveCarId             = string.Empty;
    }

    private async Task PollAgentStateAsync()
    {
        try
        {
            var state = await GetOrCreateAgentClient().GetAdminStateAsync();
            // All UI-bound properties must be updated on the UI thread.
            DispatchToUiThread(() =>
            {
                if (state is null)
                {
                    IsAgentReachable        = false;
                    IsAcRunning             = false;
                    IsSharedMemoryConnected = false;
                    ActiveCarId             = string.Empty;
                }
                else
                {
                    IsAgentReachable        = true;
                    IsAcRunning             = state.AcRunning;
                    IsSharedMemoryConnected = state.SharedMemoryConnected;
                    ActiveCarId             = state.ActiveCarId ?? string.Empty;
                }

                // Auto workflow: when AC becomes available, start auto proposal loop.
                // When AC goes away, stop it.
                var nowReady = IsAgentReachable && IsAcRunning && IsSharedMemoryConnected;
                var wasReady = _prevAcRunning && _prevSharedMem;

                if (nowReady && !wasReady)
                {
                    AppLogger.Instance.Ai("Assetto detected: LIVE signals available (AC running + SharedMemory connected).");
                    StartAutoAiLoopIfNeeded();
                }
                else if (!nowReady && wasReady)
                {
                    AppLogger.Instance.Ai("Assetto not available: stopping LIVE AI loop.");
                    StopAutoAiLoop();
                }

                _prevAcRunning = IsAcRunning;
                _prevSharedMem = IsSharedMemoryConnected;
            });
        }
        catch
        {
            // Polling must never crash the app — swallow all errors.
        }
    }

    private void StartAutoAiLoopIfNeeded()
    {
        if (!AutoAiEnabled) return;
        if (_autoAiTimer != null) return;

        // Run at a gentle cadence; never auto-apply.
        _autoAiTimer = new System.Threading.Timer(async _ =>
        {
            try
            {
                // Hard gate: only in LIVE mode + agent ready.
                if (!AutoAiEnabled) return;
                if (!IsAgentReachable || !IsAcRunning || !IsSharedMemoryConnected) return;
                if (!string.Equals(Mode, "Live", StringComparison.OrdinalIgnoreCase)) return;

                // Don't spam: interval gate.
                var now = DateTime.UtcNow;
                if ((now - _lastAutoAiUtc).TotalSeconds < AutoAiIntervalSeconds) return;

                // Only generate a proposal when none exists and user hasn't pressed RUN.
                if (LastProposals.Count > 0) return;
                if (IsRunning || IsApplying) return;

                _lastAutoAiUtc = now;
                await RunAiAsync();
            }
            catch
            {
                // Never crash background loop.
            }
        }, null, TimeSpan.FromSeconds(AutoAiIntervalSeconds), TimeSpan.FromSeconds(1));
    }

    private void StopAutoAiLoop()
    {
        try
        {
            _autoAiTimer?.Dispose();
        }
        catch { /* ignore */ }
        _autoAiTimer = null;
        _lastAutoAiUtc = DateTime.MinValue;
    }

    // ── UI dispatch helper ────────────────────────────────────────────────────

    /// <summary>
    /// Dispatches <paramref name="action"/> to the Windows App SDK dispatcher
    /// (UI thread).  Falls back to a direct call when no dispatcher is available
    /// (e.g. during unit tests).
    /// </summary>
    private static void DispatchToUiThread(Action action)
    {
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (dispatcher is null)
        {
            action();
            return;
        }
        dispatcher.TryEnqueue(() => action());
    }

    // ── Cascading property changes ────────────────────────────────────────────

    partial void OnCarIdChanged(string value)   => _ = LoadTracksAsync(value);
    partial void OnTrackIdChanged(string value) => _ = LoadSetupFilesAsync(value);

    partial void OnModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsHotlapMode));
        OnPropertyChanged(nameof(ApplyButtonLabel));
        OnPropertyChanged(nameof(RunButtonLabel));
        OnPropertyChanged(nameof(ApplyButtonTooltip));
    }

    partial void OnIsApplyingChanged(bool value) => ApplyProposalCommand.NotifyCanExecuteChanged();

    partial void OnSelectedSetupFileChanged(string? value)
    {
        _ = LoadProposalsFromFileAsync();
        ApplyCommand.NotifyCanExecuteChanged();
        ApplyProposalCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedIterationChanged(SetupIteration? value)
    {
        if (value != null)
            SelectedSetupFile = value.Setup;
        ApplyCommand.NotifyCanExecuteChanged();
        ApplyProposalCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Rebuilds proposals from cached entries when the category filter changes.</summary>
    partial void OnSelectedCategoryChanged(string value)
    {
        LastProposals.Clear();
        // Only rebuild if RUN was pressed — do not auto-generate proposals on category change.
        if (_hasProposalFromRun && _cachedEntries is not null)
            BuildProposals(_cachedEntries);
        ApplyProposalCommand.NotifyCanExecuteChanged();
    }

    // ── Provider-based loaders ────────────────────────────────────────────────

    /// <summary>
    /// Called from the Sesiones page "Select Folder" button (via command) so the
    /// provider can show its own picker (local or remote).
    /// </summary>
    [RelayCommand]
    private async Task SelectRootFolderAsync()
    {
        var provider = CreateProvider();
        var path = await provider.SelectRootAsync();
        if (path is not null)
        {
            AppLogger.Instance.Info($"Carpeta raíz configurada: {path}");
            await LoadCarsAsync(path);
        }
    }

    private async Task LoadCarsAsync(string rootFolder)
    {
        Cars.Clear();
        Tracks.Clear();
        SetupFiles.Clear();
        Iterations.Clear();

        var provider = CreateProvider();
        try
        {
            var cars = await provider.GetCarsAsync();
            foreach (var c in cars) Cars.Add(c);
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error($"Error al leer carpetas de coches: {ex.Message}");
            return;
        }

        AppLogger.Instance.Data($"Carpeta raíz cargada: {rootFolder}");
        AppLogger.Instance.Info($"Coches encontrados: {Cars.Count}");

        if (!string.IsNullOrEmpty(CarId) && Cars.Contains(CarId))
            await LoadTracksAsync(CarId);
        else
            CarId = Cars.Count > 0 ? Cars[0] : string.Empty;
    }

    private async Task LoadTracksAsync(string carId)
    {
        Tracks.Clear();
        SetupFiles.Clear();
        Iterations.Clear();

        if (string.IsNullOrEmpty(carId)) return;

        var provider = CreateProvider();
        try
        {
            var tracks = await provider.GetTracksAsync(carId);
            foreach (var t in tracks) Tracks.Add(t);
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error($"Error al leer circuitos: {ex.Message}");
            return;
        }

        AppLogger.Instance.Data($"Coche seleccionado: {carId}  |  Circuitos encontrados: {Tracks.Count}");

        if (!string.IsNullOrEmpty(TrackId) && Tracks.Contains(TrackId))
            await LoadSetupFilesAsync(TrackId);
        else
            TrackId = Tracks.Count > 0 ? Tracks[0] : string.Empty;
    }

    private async Task LoadSetupFilesAsync(string trackId)
    {
        SetupFiles.Clear();
        Iterations.Clear();

        if (string.IsNullOrEmpty(CarId) || string.IsNullOrEmpty(trackId))
        {
            SetupFilesHintVisibility = Visibility.Visible;
            return;
        }

        var provider = CreateProvider();
        try
        {
            var items = await provider.GetSetupsAsync(CarId, trackId);
            foreach (var s in items) SetupFiles.Add(s.FileName);

            Iterations.Clear();
            for (int i = 0; i < SetupFiles.Count; i++)
                Iterations.Add(new SetupIteration { Setup = SetupFiles[i], BestLap = "—", Iter = i, Exported = false });
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error($"Error al leer archivos de setup: {ex.Message}");
            SetupFilesHintVisibility = Visibility.Visible;
            return;
        }

        SetupFilesHintVisibility = SetupFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AppLogger.Instance.Data($"Circuito seleccionado: {trackId}  |  Archivos de setup: {SetupFiles.Count}");
    }

    // ── Proposal generation from real INI ────────────────────────────────────

    /// <summary>
    /// Reads and caches the currently selected setup <c>.ini</c> file (local or remote)
    /// and builds the parameter universe, but does NOT generate proposals.
    /// Proposals are only generated by <see cref="Start"/> (the RUN command).
    /// </summary>
    private async Task LoadProposalsFromFileAsync()
    {
        // Clear any existing proposals and reset the RUN flag — proposals are stale
        // once the selected file changes.
        LastProposals.Clear();
        _hasProposalFromRun = false;
        OnPropertyChanged(nameof(HasProposal));
        OnPropertyChanged(nameof(ProposalVisibility));
        OnPropertyChanged(nameof(NoProposalVisibility));

        if (string.IsNullOrEmpty(SelectedSetupFile) ||
            string.IsNullOrEmpty(CarId) ||
            string.IsNullOrEmpty(TrackId))
        {
            _cachedEntries  = null;
            CurrentUniverse = null;
            return;
        }

        string iniText;
        try
        {
            iniText = await CreateProvider().ReadSetupTextAsync(CarId, TrackId, SelectedSetupFile);
        }
        catch (Exception ex)
        {
            _cachedEntries  = null;
            CurrentUniverse = null;
            _baseIniText    = string.Empty;
            AppLogger.Instance.Error($"Error al leer setup: {ex.Message}");
            return;
        }

        // Persist raw text so ApplyProposalAsync can compute the base→proposed diff.
        _baseIniText = iniText;
        AppLogger.Instance.Info($"Loaded base file: {SelectedSetupFile}");
        AddLog($"Loaded base file: {SelectedSetupFile}");

        try
        {
            var allEntries  = SetupIniParser.ParseText(iniText);
            _cachedEntries  = allEntries;
            CurrentUniverse = SetupParamUniverse.Build(CarId, TrackId, SelectedSetupFile!, allEntries);

            // Log totals
            AppLogger.Instance.Data(
                $"Universe loaded: sections={CurrentUniverse.SectionCount} keys={CurrentUniverse.KeyCount} numeric={CurrentUniverse.NumericCount}");
            AddLog($"Universe loaded: sections={CurrentUniverse.SectionCount} keys={CurrentUniverse.KeyCount} numeric={CurrentUniverse.NumericCount}");

            // Log per-category breakdown
            var catLog = string.Join(" ", Enum.GetValues<SetupCategory>()
                .Where(c => CurrentUniverse.ByCategory.ContainsKey(c))
                .Select(c => $"{c}={CurrentUniverse.ByCategory[c].Count}"));
            AppLogger.Instance.Data($"Universe categorized: {catLog}");
            AddLog($"Universe categorized: {catLog}");

            // Log first 20 allowlist keys as a diagnostic summary (unsorted sample).
            var allowlistSample = string.Join(", ", CurrentUniverse.AllowlistKeys.Take(20));
            AppLogger.Instance.Data($"Allowlist sample (first 20): {allowlistSample}");
            AddLog($"Allowlist: {CurrentUniverse.AllowlistKeys.Count} keys — sample: {allowlistSample}");

            // Note: proposals are NOT generated here — only RUN (StartCommand) generates them.
            StartCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            _cachedEntries  = null;
            CurrentUniverse = null;
            AppLogger.Instance.Error($"Error al leer parámetros del setup: {ex.Message}");
        }

        ApplyProposalCommand.NotifyCanExecuteChanged();
    }

    // Maximum proposals shown in the UI — limited to 6 to keep the proposals card readable.
    private const int MaxProposals = 6;

    private void BuildProposals(IEnumerable<IniEntry> entries)
    {
        var allEntries = entries as List<IniEntry> ?? entries.ToList();

        // ── Route Simulation mode to the 3-step plan engine ──────────────────
        if (string.Equals(Mode, "Simulation", StringComparison.OrdinalIgnoreCase))
        {
            BuildSimulationPlan(allEntries);
            return;
        }

        // All numerically tunable entries (non-zero value, not a selector key).
        // Explicitly intersect with the loaded universe allowlist so that only
        // parameters actually present in the selected INI file are ever proposed.
         var universe = CurrentUniverse;        var tunable = allEntries
            .Where(e =>
                !NonTunableKeys.Contains(e.Key) &&
                (universe is null || universe.Contains(e.Section, e.Key)) &&
                double.TryParse(e.Value,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var v) && v != 0.0)
            .ToList();

        var sectionCount = allEntries.Select(e => e.Section).Distinct().Count();
        AppLogger.Instance.Data($"INI parsed: sections={sectionCount}, numericParams={tunable.Count}");

        if (tunable.Count == 0)
        {
            foreach (var e in allEntries.Take(20))
                AppLogger.Instance.Data($"  candidate: [{e.Section}] {e.Key}={e.Value}");
        }

        // ── Build SetupParameter list for the Setup Diff view ─────────────────
        ParsedParameters.Clear();
        foreach (var e in tunable)
        {
            var sp = SetupParameter.FromIniEntry(e);
            if (sp is null) continue;
            SetupParameterClassifier.Classify(sp);
            ParsedParameters.Add(sp);
        }

        // ── Categorize + weight every tunable entry ────────────────────────────
        var weighted = tunable
            .Select(e =>
            {
                var cat    = SetupParamClassifier.Classify(e.Section, e.Key);
                var weight = SetupParamClassifier.ImpactWeight(cat, e.Key);
                return (Entry: e, Category: cat, Weight: weight);
            })
            .ToList();

        // ── Apply category filter ──────────────────────────────────────────────
        var cat = _selectedCategory;
        List<(IniEntry Entry, SetupCategory Category, double Weight)> candidates;

        if (string.IsNullOrEmpty(cat) || cat == "All")
        {
            candidates = weighted;
        }
        else if (Enum.TryParse<SetupCategory>(cat, out var selectedCat))
        {
            candidates = weighted.Where(t => t.Category == selectedCat).ToList();
            if (candidates.Count == 0)
            {
                var noParamMsg = $"Este setup no tiene parámetros de {cat}.";
                AppLogger.Instance.Warn(noParamMsg);
                AddLog(noParamMsg, "WRN");
            }
        }
        else
        {
            candidates = weighted;
        }

        // ── Sort by weight descending (deterministic weighted selection) ───────
        candidates = candidates.OrderByDescending(t => t.Weight).ToList();

        // ── Log top candidates ─────────────────────────────────────────────────
        var topLog = string.Join(", ",
            candidates.Take(3).Select(t => $"{t.Entry.Key}={t.Weight:F2}"));
        var logMsg = $"AI candidates: category={cat ?? "All"} count={candidates.Count} top weights: {topLog}";
        AppLogger.Instance.Data(logMsg);
        AddLog(logMsg);

        // ── Emit top MaxProposals proposals with safe steps ────────────────────
        foreach (var (entry, category, _) in candidates.Take(MaxProposals))
        {
            if (!double.TryParse(entry.Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var current))
                continue;

            var step     = SetupParamClassifier.SafeStep(category, entry.Key, current);
            var proposed = Math.Round(current - step, 4);
            var deltaStr = $"-{step.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

            LastProposals.Add(new Proposal
            {
                Section   = entry.Section,
                Parameter = entry.Key,
                From      = entry.Value,
                To        = proposed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Delta     = deltaStr,
            });
        }

        AppLogger.Instance.Ai(
            $"Propuestas generadas desde '{SelectedSetupFile}' — " +
            $"{tunable.Count} parámetros disponibles, {LastProposals.Count} seleccionados.");

        if (tunable.Count == 0)
            AppLogger.Instance.Warn(
                "El archivo de setup no contiene parámetros numéricos reconocibles.");
    }

    // ── Simulation 3-step plan ─────────────────────────────────────────────────

    /// <summary>
    /// Generates a 3-step simulation plan:
    /// <list type="number">
    ///   <item><b>Step 1 – Primary change</b>: highest-weight key from
    ///     Aero / Tyres / Alignment (wing · pressure · camber · toe · arb),
    ///     not blocked by the recent queue.</item>
    ///   <item><b>Step 2 – Fine-tune</b>: second-best key from the <i>same</i>
    ///     category as Step 1, not recent.</item>
    ///   <item><b>Step 3 – Stability</b>: best available key from
    ///     Brakes / Electronics / Suspension, not recent.</item>
    /// </list>
    /// Falls back to round-robin category selection when no high-impact key is found
    /// for Step 1. All chosen keys are pushed to <see cref="_recentProposalKeys"/>.
    /// </summary>
    private void BuildSimulationPlan(List<IniEntry> allEntries)
    {
        const string modeName = "Simulation";

        // Hard-block set: last RecentHardBlock keys.
        var hardBlocked = _recentProposalKeys
            .Skip(Math.Max(0, _recentProposalKeys.Count - RecentHardBlock))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        AppLogger.Instance.Data(
            $"AI mode={modeName} recentBlock={RecentHardBlock} recentSoft={RecentWindowSize} " +
            $"recentKeys=[{string.Join(", ", _recentProposalKeys.TakeLast(5))}]");

        // Build weighted tunable list (same filter as BuildProposals).
        // Explicitly intersect with the loaded universe to ensure only INI-present params are proposed.
        var simUniverse = CurrentUniverse;
        var weighted = allEntries
            .Where(e =>
                !NonTunableKeys.Contains(e.Key) &&
                (simUniverse is null || simUniverse.Contains(e.Section, e.Key)) &&
                double.TryParse(e.Value,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var v) && v != 0.0)
            .Select(e =>
            {
                var cat    = SetupParamClassifier.Classify(e.Section, e.Key);
                var weight = SetupParamClassifier.ImpactWeight(cat, e.Key);
                return (Entry: e, Cat: cat, Weight: weight, Id: $"{e.Section}.{e.Key}");
            })
            .OrderByDescending(t => t.Weight)
            .ToList();

        if (weighted.Count == 0)
        {
            AppLogger.Instance.Warn("Simulation plan: no tunable parameters found.");
            return;
        }

        // ── Step 1: high-impact primary key ──────────────────────────────────
        // Preferred categories for the primary change.
        var highImpactCats = new HashSet<SetupCategory>
        {
            SetupCategory.Aero,
            SetupCategory.Tyres,
            SetupCategory.Alignment,
        };
        // High-impact keyword check (section-agnostic).
        static bool IsHighImpactKey(string key)
        {
            var k = key.ToUpperInvariant();
            return k.Contains("WING")     || k.Contains("SPLITTER")  ||
                   k.Contains("PRESSURE") || k.Contains("CAMBER")    ||
                   k.Contains("TOE")      || k.Contains("CASTER")    ||
                   k.Contains("ARB")      || k.Contains("ANTIROLL");
        }

        // First look for an unblocked high-impact key in the preferred categories.
        var step1Candidate = weighted
            .Where(t => highImpactCats.Contains(t.Cat) &&
                        IsHighImpactKey(t.Entry.Key) &&
                        !hardBlocked.Contains(t.Id))
            .FirstOrDefault();

        // If nothing found, widen to any unblocked key in preferred categories.
        if (step1Candidate == default)
            step1Candidate = weighted
                .Where(t => highImpactCats.Contains(t.Cat) && !hardBlocked.Contains(t.Id))
                .FirstOrDefault();

        // Still nothing — fall back to round-robin over all categories.
        SetupCategory step1Cat;
        if (step1Candidate == default)
        {
            step1Cat        = SimRoundRobinCats[_simCatIndex % SimRoundRobinCats.Length];
            _simCatIndex++;
            step1Candidate  = weighted
                .Where(t => t.Cat == step1Cat && !hardBlocked.Contains(t.Id))
                .FirstOrDefault();
            AppLogger.Instance.Data($"AI step1 fallback to round-robin category={step1Cat}");
        }

        if (step1Candidate == default)
        {
            AppLogger.Instance.Warn("Simulation plan: no unblocked candidate for Step 1.");
            return;
        }

        step1Cat = step1Candidate.Cat;
        AppLogger.Instance.Data(
            $"AI picked step1: category={step1Cat} key={step1Candidate.Id} weight={step1Candidate.Weight:F2}");

        // ── Step 2: fine-tune — same category, different key, unblocked ───────
        var step2Candidate = weighted
            .Where(t => t.Cat == step1Cat &&
                        t.Id  != step1Candidate.Id &&
                        !hardBlocked.Contains(t.Id))
            .FirstOrDefault();

        if (step2Candidate == default)
            AppLogger.Instance.Data($"AI step2: no second candidate in category={step1Cat}, skipping.");
        else
            AppLogger.Instance.Data(
                $"AI picked step2: category={step2Candidate.Cat} key={step2Candidate.Id} weight={step2Candidate.Weight:F2}");

        // ── Step 3: stability — Brakes / Electronics / Suspension ─────────────
        var stabilityCats = new HashSet<SetupCategory>
        {
            SetupCategory.Brakes,
            SetupCategory.Electronics,
            SetupCategory.Suspension,
        };

        var step3Candidate = weighted
            .Where(t => stabilityCats.Contains(t.Cat) &&
                        t.Id != step1Candidate.Id &&
                        (step2Candidate == default || t.Id != step2Candidate.Id) &&
                        !hardBlocked.Contains(t.Id))
            .FirstOrDefault();

        if (step3Candidate == default)
            AppLogger.Instance.Data("AI step3: no stability candidate found, skipping.");
        else
            AppLogger.Instance.Data(
                $"AI picked step3: category={step3Candidate.Cat} key={step3Candidate.Id} weight={step3Candidate.Weight:F2}");

        // ── Emit proposals + log plan ─────────────────────────────────────────
        var steps = new[]
        {
            (step1Candidate, "Step 1 – Primary change (high impact)"),
            (step2Candidate, "Step 2 – Fine-tune (same category)"),
            (step3Candidate, "Step 3 – Stability / safety"),
        };

        var planIds = new StringBuilder();
        foreach (var (candidate, rationale) in steps)
        {
            if (candidate == default) continue;

            if (!double.TryParse(candidate.Entry.Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var current))
                continue;

            var step     = SetupParamClassifier.SafeStep(candidate.Cat, candidate.Entry.Key, current);
            var proposed = Math.Round(current - step, 4);
            var deltaStr = $"-{step.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

            LastProposals.Add(new Proposal
            {
                Section   = candidate.Entry.Section,
                Parameter = candidate.Entry.Key,
                From      = candidate.Entry.Value,
                To        = proposed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Delta     = deltaStr,
                Reason    = rationale,
            });

            // Track in recent queue (FIFO, capped at RecentWindowSize).
            if (_recentProposalKeys.Count >= RecentWindowSize)
                _recentProposalKeys.Dequeue();
            _recentProposalKeys.Enqueue(candidate.Id);

            planIds.Append($"{candidate.Id}({candidate.Cat}) ");
        }

        var planLog = $"AI final plan [{modeName}]: {planIds.ToString().TrimEnd()}";
        AppLogger.Instance.Ai(planLog);
        AddLog(planLog, "AI");
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        if (string.IsNullOrEmpty(CarId) || string.IsNullOrEmpty(TrackId))
        {
            AppLogger.Instance.Warn("Selecciona un coche y un circuito antes de iniciar la sesión.");
            return;
        }

        IsRunning = true;
        StatusText = "● RUNNING";
        AppLogger.Instance.Info($"Sesión INICIADA — Coche: {CarId}  Circuito: {TrackId}  Modo: {Mode}");
        AppLogger.Instance.Ai("Motor IA activado. Esperando datos de telemetría...");
        AppLogger.Instance.Data("Canal de datos en tiempo real: ABIERTO");
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();

        // NOTE: Starting a session does NOT generate proposals.
        // Use the RUN AI action explicitly from the IA tab to generate proposals.
        ApplyProposalCommand.NotifyCanExecuteChanged();
    }

    private bool CanStart() => !IsRunning && _currentUniverse is not null;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        IsRunning = false;
        StatusText = "● READY";
        AppLogger.Instance.Info("Sesión DETENIDA por el usuario.");
        AppLogger.Instance.Ai("Motor IA pausado.");
        AppLogger.Instance.Data("Canal de datos en tiempo real: CERRADO");
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    
    /// <summary>
    /// Generates a new proposal set. This is the ONLY action that populates <see cref="LastProposals"/>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunAi))]
    private async Task RunAiAsync()
    {
        if (!IsAiEnabled)
        {
            AppLogger.Instance.Warn("AI is disabled.");
            return;
        }

        if (string.IsNullOrEmpty(CarId) || string.IsNullOrEmpty(TrackId))
        {
            AppLogger.Instance.Warn("Selecciona un coche y un circuito antes de ejecutar RUN.");
            return;
        }

        if (string.IsNullOrEmpty(SelectedSetupFile))
        {
            AppLogger.Instance.Warn("Selecciona un archivo de setup antes de ejecutar RUN.");
            return;
        }

        // Ensure universe/cache is loaded for the currently selected file.
        if (_cachedEntries is null || _currentUniverse is null)
            await LoadProposalsFromFileAsync();

        if (_cachedEntries is null || _currentUniverse is null)
        {
            AppLogger.Instance.Warn("No se pudo cargar el setup — RUN cancelado.");
            return;
        }

        LastProposals.Clear();

        BuildProposals(_cachedEntries);

        // Mark as produced by RUN (required for Apply).
        _hasProposalFromRun = LastProposals.Count > 0;
        OnPropertyChanged(nameof(HasProposal));
        OnPropertyChanged(nameof(ProposalVisibility));
        OnPropertyChanged(nameof(NoProposalVisibility));

        LastAiRunAt = DateTime.Now;
        OnPropertyChanged(nameof(LastAiRunText));

        if (_hasProposalFromRun)
        {
            AppLogger.Instance.Ai($"RUN → Propuesta(s) generada(s): {LastProposals.Count} cambio(s).");
            AddLog($"RUN → {LastProposals.Count} proposal(s) generated", "AI");
        }
        else
        {
            AppLogger.Instance.Warn("RUN → 0 cambios válidos para este setup.");
        }

        ApplyProposalCommand.NotifyCanExecuteChanged();
    }

    private bool CanRunAi() => !IsApplying && IsAiEnabled && _currentUniverse is not null;
private bool CanStop() => IsRunning;

    /// <summary>Clears the current proposal list (available from the IA tab).</summary>
    [RelayCommand]
    private void ClearProposal()
    {
        LastProposals.Clear();
        _hasProposalFromRun = false;
        OnPropertyChanged(nameof(HasProposal));
        OnPropertyChanged(nameof(ProposalVisibility));
        OnPropertyChanged(nameof(NoProposalVisibility));
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        // Pre-save diagnostic log
        AppLogger.Instance.Info(
            $"APPLY: AgentConnected={IsAgentReachable}  " +
            $"AgentBaseUrl={SetupSettings.Instance.AgentBaseUrl}  " +
            $"Mode={SetupSettings.Instance.Mode}  " +
            $"BaseFile={SelectedSetupFile}");

        var saver             = CreateSaver();

        // Guard: prohibit local writes when an Agent is active and reachable.
        // The saver is LocalSetupSaver only when Mode==Local; RemoteSetupSaver handles Remote mode
        // correctly already.  This guard catches the edge case where Mode is Local but an Agent
        // is reachable — in that state, writing locally would create ghost files the Agent does
        // not know about.
        if (!ShouldWriteLocalIters() && saver is LocalSetupSaver)
        {
            var blockMsg =
                $"LOCAL WRITE BLOCKED — Agent is connected ({SetupSettings.Instance.AgentBaseUrl}) " +
                $"but Mode is set to Local. To save via Agent: switch Mode to Remote. " +
                $"To save locally: disconnect the Agent first.";
            StatusText = "● WRITE BLOCKED";
            AddLog(blockMsg, "ERR");
            AppLogger.Instance.Error($"APPLY BLOCKED: {blockMsg}");
            return;
        }

        var iniText           = string.Empty;
        var versionedFileName = NextVersionedFileName(SelectedSetupFile!);

        // Read the current setup text (needed for remote save; for local we still copy the file)
        try
        {
            iniText = await CreateProvider().ReadSetupTextAsync(CarId, TrackId, SelectedSetupFile!);
        }
        catch (Exception ex)
        {
            StatusText = "● ERROR";
            AppLogger.Instance.Error($"Error al leer setup para guardar: {ex.Message}");
            return;
        }

        try
        {
            var savedPath = await saver.SaveAsync(CarId, TrackId, versionedFileName, iniText);

            StatusText = "● APPLIED";
            AppLogger.Instance.Info($"Setup guardado como: {versionedFileName}");
            AppLogger.Instance.Data(IsRemoteMode
                ? $"Setup guardado en PC simulador: {savedPath}"
                : $"Destino: {savedPath}");

            // Refresh file list so the new versioned file appears, then select it.
            await LoadSetupFilesAsync(TrackId);
            SelectedSetupFile = versionedFileName;
        }
        catch (Exception ex)
        {
            StatusText = "● APPLY ERROR";
            AppLogger.Instance.Error($"Error al guardar el setup: {ex.Message}");
        }
    }

    private bool CanApply() => !string.IsNullOrEmpty(SelectedSetupFile);

    /// <summary>
    /// Returns a new versioned file name based on <paramref name="baseName"/>.
    /// <para>
    /// Pattern: <c>{stem}_AI_{yyyyMMdd_HHmmss}_v{NNN}{ext}</c>, where NNN is the next
    /// three-digit integer after the highest existing version found in <see cref="SetupFiles"/>
    /// for files sharing the same base stem.
    /// </para>
    /// <example>
    /// If <c>SetupFiles</c> contains "Supra MKIV Race mid_AI_20260301_120000_v001.ini" and
    /// "Supra MKIV Race mid_AI_20260301_120010_v002.ini", the next name returned is
    /// "Supra MKIV Race mid_AI_20260301_120030_v003.ini".
    /// </example>
    /// </summary>
    private string NextVersionedFileName(string baseName)
    {
        var ext      = Path.GetExtension(baseName);
        var stem     = Path.GetFileNameWithoutExtension(baseName);
        var aiPrefix = $"{stem}_AI_";
        var ts       = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        // Find the highest existing v### among all _AI_ versioned files for this base stem.
        int maxVersion = 0;
        foreach (var f in SetupFiles)
        {
            var fStem = Path.GetFileNameWithoutExtension(f);
            if (!fStem.StartsWith(aiPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            // fStem pattern: {stem}_AI_{ts}_v{NNN}
            var vIdx = fStem.LastIndexOf("_v", StringComparison.OrdinalIgnoreCase);
            if (vIdx < 0) continue;
            var numStr = fStem[(vIdx + 2)..];
            if (numStr.Length > 0 &&
                numStr.All(char.IsDigit) &&
                int.TryParse(numStr, out int n) &&
                n > maxVersion)
                maxVersion = n;
        }

        return $"{stem}_AI_{ts}_v{(maxVersion + 1):D3}{ext}";
    }


    [RelayCommand]
    private void Connect()
    {
        var simProcessNames = SimProcessNames;
        var found = simProcessNames.Any(name => Process.GetProcessesByName(name).Length > 0);

        if (found)
        {
            IsConnected = true;
            StatusText = "● CONNECTED";
            AppLogger.Instance.Info("✔ Simulador detectado — conexión establecida.");
            AppLogger.Instance.Data($"Telemetría activa — Coche: {CarId}  Circuito: {TrackId}");
        }
        else
        {
            IsConnected = false;
            StatusText = "● SIM NOT FOUND";
            AppLogger.Instance.Warn("No se detectó ningún simulador en ejecución.");
            AppLogger.Instance.Info("Abre Assetto Corsa / ACC y pulsa Connect de nuevo.");
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplyProposal))]
    private async Task ApplyProposalAsync()
    {
        System.Diagnostics.Debug.WriteLine("[SessionsVM] APPLY CLICKED");
        AddLog("APPLY CLICKED");

        IsApplying = true;
        ApplyProposalCommand.NotifyCanExecuteChanged();

        try
        {
            await DoApplyProposalAsync();
        }
        finally
        {
            IsApplying = false;
            ApplyProposalCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task DoApplyProposalAsync()
    {
        // Pre-save diagnostic log
        AppLogger.Instance.Info(
            $"APPLY PROPOSAL: AgentConnected={IsAgentReachable}  " +
            $"AgentBaseUrl={SetupSettings.Instance.AgentBaseUrl}  " +
            $"Mode={SetupSettings.Instance.Mode}  " +
            $"BaseFile={SelectedSetupFile}");

        // Re-read the current base INI so the diff is always accurate, even if
        // _baseIniText was set from a previous load.
        string baseIniText;
        try
        {
            baseIniText  = await CreateProvider().ReadSetupTextAsync(CarId, TrackId, SelectedSetupFile!);
            _baseIniText = baseIniText;
        }
        catch (Exception ex)
        {
            var readErr = $"APPLY READ ERROR: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[SessionsVM] {readErr}");
            AddLog(readErr, "ERR");
            AppLogger.Instance.Error($"Error al leer setup: {ex.Message}");
            return;
        }

        // Safety filter: only apply proposals whose (Section, Parameter) exists in the universe.
        var universe = _currentUniverse;
        var proposalsToApply = universe is null
            ? LastProposals.ToList()
            : LastProposals.Where(p =>
            {
                if (universe.Contains(p.Section, p.Parameter)) return true;
                var filtered = $"Filtered out unsupported param: {p.Section}.{p.Parameter}";
                System.Diagnostics.Debug.WriteLine($"[SessionsVM] {filtered}");
                AddLog(filtered, "WRN");
                AppLogger.Instance.Warn(filtered);
                return false;
            }).ToList();

        // Apply changes in-memory to produce the modified text (used in both paths).
        var lines = SetupIniParser.NormalizeText(baseIniText)
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .ToList();
        foreach (var proposal in proposalsToApply)
        {
            bool applied = false;
            var  currentSection = string.Empty;

            // Format A (AC per-section): [PRESSURE_LF]\nVALUE=1.70
            bool isFormatA = proposal.Section.Equals(proposal.Parameter, StringComparison.OrdinalIgnoreCase);
            var  iniKey    = isFormatA ? "VALUE" : proposal.Parameter;
            var  newLine   = $"{iniKey}={proposal.To}";

            for (int i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    currentSection = trimmed[1..^1].Trim();
                    continue;
                }

                var eqIdx = lines[i].IndexOf('=');
                if (eqIdx > 0 &&
                    currentSection.Equals(proposal.Section, StringComparison.OrdinalIgnoreCase) &&
                    lines[i][..eqIdx].Trim().Equals(iniKey, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = newLine;
                    AppLogger.Instance.Data(
                        $"  [{proposal.Section}] {proposal.Parameter}: {proposal.From} → {proposal.To}  (Δ {proposal.Delta})");
                    applied = true;
                    break;
                }
            }

            if (!applied)
                AppLogger.Instance.Warn(
                    $"  Parámetro '[{proposal.Section}] {proposal.Parameter}' no encontrado en el archivo.");
        }

        var modifiedText  = string.Join("\n", lines);
        var versionedName = NextVersionedFileName(SelectedSetupFile!);

        try
        {
            string savedPath;

            if (IsRemoteMode)
            {
                // ── Remote: INI-only save via Agent ──────────────────────────────
                var saver = CreateSaver(); // RemoteSetupSaver
                savedPath = await saver.SaveAsync(CarId, TrackId, versionedName, modifiedText);
            }
            else
            {
                // ── Local: write the versioned file ──────────────────────────────
                // Guard: if an Agent is reachable, local writes are prohibited — no fallback.
                if (!ShouldWriteLocalIters())
                {
                    var blockMsg =
                        $"LOCAL WRITE BLOCKED — Agent is connected ({SetupSettings.Instance.AgentBaseUrl}). " +
                        $"To save via Agent: ensure Mode is set to Remote. " +
                        $"To save locally: disconnect the Agent first.";
                    AddLog(blockMsg, "ERR");
                    AppLogger.Instance.Error($"APPLY PROPOSAL BLOCKED: {blockMsg}");
                    StatusText = "● WRITE BLOCKED (REMOTE)";
                    return;
                }
                var destFolder = Path.Combine(SetupSettings.Instance.RootFolder, CarId, TrackId);
                Directory.CreateDirectory(destFolder);
                var filePath = Path.Combine(destFolder, versionedName);
                await File.WriteAllTextAsync(filePath, modifiedText);
                savedPath = filePath;
                AppLogger.Instance.Ai("Propuesta de IA aplicada al archivo de setup.");
            }

            // ── Show "Saved OK" with file name and location ──────────────────────
            var locationInfo = IsRemoteMode
                ? $"  [{CarId}/{TrackId}/{versionedName}]"
                : $"  {savedPath}";
            StatusText = $"✔ Saved OK: {versionedName}";

            AppliedFileLabel = $"{SelectedSetupFile} → {versionedName}";
            AppLogger.Instance.Info($"Setup guardado como: {versionedName}{locationInfo}");
            AddLog($"Saved OK → {versionedName}", "AI");

            // Capture base label before SelectedSetupFile changes.
            var baseLabel = Path.GetFileNameWithoutExtension(SelectedSetupFile ?? "base");

            // Refresh file list, select the new versioned file.
            await LoadSetupFilesAsync(TrackId);
            SelectedSetupFile = versionedName;

            // Push base-vs-proposed diff to SetupDiffViewModel for the Setup Diff tab.
            SetupDiffViewModel.Shared.Load(
                baseText:      baseIniText,
                proposedText:  modifiedText,
                baseLabel:     baseLabel,
                proposedLabel: Path.GetFileNameWithoutExtension(versionedName));

            // Clear proposals after a successful apply — user must press RUN again to get new ones.
            LastProposals.Clear();
            _hasProposalFromRun = false;
        }
        catch (Exception ex)
        {
            var inner   = ex.InnerException is not null ? $" ({ex.InnerException.Message})" : string.Empty;
            var failMsg = $"APPLY ERROR [{ex.GetType().Name}] {ex.Message}{inner}";
            System.Diagnostics.Debug.WriteLine($"[SessionsVM] {failMsg}");
            AddLog(failMsg, "ERR");
            StatusText = "● PROPOSAL ERROR";
            AppLogger.Instance.Error($"Error al aplicar propuesta: {ex.Message}");
        }
    }

    private bool CanApplyProposal()
    {
        // INI-only workflow: only allow when we have a proposal generated by RUN.
        if (IsApplying) return false;
        if (string.IsNullOrEmpty(SelectedSetupFile)) return false;
        if (LastProposals.Count == 0) return false;
        if (!_hasProposalFromRun) return false;
        return true;
    }
/// <summary>
    /// Restaura el backup creado por ApplyProposal, revertiendo el archivo .ini al estado anterior.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRollback))]
    private void Rollback()
    {
        if (string.IsNullOrEmpty(_backupPath) || !File.Exists(_backupPath))
        {
            AppLogger.Instance.Warn("No hay backup disponible para restaurar.");
            return;
        }

        var originalPath = _backupPath[..^BackupExtension.Length]; // quitar ".bak"
        try
        {
            File.Copy(_backupPath, originalPath, overwrite: true);
            File.Delete(_backupPath);
            _backupPath = null;

            StatusText = "● ROLLED BACK";
            AppLogger.Instance.Warn("Rollback ejecutado — setup restaurado al estado anterior.");
            RollbackCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error($"Error al restaurar backup: {ex.Message}");
        }
    }

    private bool CanRollback() => !string.IsNullOrEmpty(_backupPath) && File.Exists(_backupPath);

    // ── Telemetry integration ─────────────────────────────────────────────────

    /// <summary>
    /// Called by <see cref="TelemetryViewModel"/> on each setup-adjustment tick.
    /// Adds a new proposal or replaces an existing one with the same Section+Parameter
    /// in <see cref="LastProposals"/>, and increments the selected iteration counter.
    /// Does nothing when no iteration is selected.
    /// </summary>
    public void PushTelemetryProposal(Proposal p)
    {
        if (SelectedIteration is null) return;

        // Filter: skip proposals whose (Section, Parameter) is not in the loaded universe.
        if (_currentUniverse is not null && !_currentUniverse.Contains(p.Section, p.Parameter))
        {
            // Silent ignore: proposals MUST be limited to the loaded setup universe.
            return;
        }

        bool replaced = false;
        for (int i = 0; i < LastProposals.Count; i++)
        {
            if (LastProposals[i].Section.Equals(p.Section, StringComparison.OrdinalIgnoreCase) &&
                LastProposals[i].Parameter.Equals(p.Parameter, StringComparison.OrdinalIgnoreCase))
            {
                LastProposals[i] = p;
                replaced = true;
                break;
            }
        }

        if (!replaced)
            LastProposals.Add(p);

        SelectedIteration.Iter++;
    }

    /// <summary>
    /// Batch variant of <see cref="PushTelemetryProposal"/>: applies all proposals
    /// from <paramref name="proposals"/> in a single pass, updating the iteration
    /// counter only once. Does nothing when no iteration is selected or the array
    /// is empty.
    /// </summary>
    public void PushTelemetryProposals(Proposal[] proposals)
    {
        if (SelectedIteration is null || proposals.Length == 0) return;

        int ignored = 0;

        foreach (var p in proposals)
        {
            // Filter: skip proposals whose (Section, Parameter) is not in the loaded universe.
            if (_currentUniverse is not null && !_currentUniverse.Contains(p.Section, p.Parameter))
            {
                ignored++;
                continue;
            }

            bool replaced = false;
            for (int i = 0; i < LastProposals.Count; i++)
            {
                if (LastProposals[i].Section.Equals(p.Section, StringComparison.OrdinalIgnoreCase) &&
                    LastProposals[i].Parameter.Equals(p.Parameter, StringComparison.OrdinalIgnoreCase))
                {
                    LastProposals[i] = p;
                    replaced = true;
                    break;
                }
            }

            if (!replaced)
                LastProposals.Add(p);
        }

        if (ignored > 0)
            AppLogger.Instance.Ai($"Ignored {ignored} proposal(s) not present in selected setup.");

        SelectedIteration.Iter++;
    }
}
