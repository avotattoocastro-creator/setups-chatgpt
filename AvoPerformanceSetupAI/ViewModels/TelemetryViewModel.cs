using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using AvoPerformanceSetupAI.ML;
using AvoPerformanceSetupAI.Models;
using AvoPerformanceSetupAI.Reference;
using AvoPerformanceSetupAI.Reference.Import;
using AvoPerformanceSetupAI.Services;
using AvoPerformanceSetupAI.Services.Agent;
using AvoPerformanceSetupAI.Telemetry;

namespace AvoPerformanceSetupAI.ViewModels;

public partial class TelemetryViewModel : ObservableObject, IDisposable
{
    private DispatcherQueue? _dispatcher;
    private System.Threading.Timer? _updateTimer;
    private int  _tick;
    private readonly Random _rng = new(Environment.TickCount);

    // Stored so Initialize can subscribe and Dispose can unsubscribe (prevents memory leaks).
    private Action<bool, string>? _connectionChangedHandler;
    private Action?               _suggestSwitchHandler;
    private Action<AgentLogEntry>? _logEntryHandler;
    private Action<string>?        _logStatusHandler;
    private System.Collections.Specialized.NotifyCollectionChangedEventHandler? _sessionLogsHandler;

    // ── Telemetry service (AC shared memory + simulation routing) ─────────────

    private readonly TelemetryService _telemetryService = new();

    // ── Corner detector (stateful, thread-safe) ───────────────────────────────

    private readonly CornerDetector _cornerDetector = new();

    // ── Reference lap comparator & store ─────────────────────────────────────

    private readonly ReferenceLapComparator _comparator    = new();
    private readonly ReferenceLapStore      _refStore      = ReferenceLapStore.Instance;
    private readonly CsvReferenceImporter   _csvImporter   = new();

    // Recording state — all accessed on the UI thread (fast timer)
    private readonly List<ReferenceLapSample> _recordingBuffer   = [];
    private bool  _waitingForLapStart;
    private float _lastRecordedLapPos;
    private float _lastFastLapPos;

    /// <summary>
    /// Car and track identifiers used when saving recorded reference laps.
    /// These can be set by the caller (e.g. populated from AC static shared-memory
    /// when the car/track is known); they default to empty strings, which is still
    /// valid — <see cref="ReferenceLapStore"/> stores such laps under the
    /// <c>_unknown\_unknown</c> sub-folder.
    /// </summary>
    public string CurrentCarId   { get; set; } = string.Empty;
    public string CurrentTrackId { get; set; } = string.Empty;

    /// <summary>50 ms DispatcherTimer for real-time phase/corner updates on the UI thread.</summary>
    private DispatcherTimer? _fastTimer;

    /// <summary>CornerIndex of the last corner already reflected in the bindable properties.</summary>
    private int _lastReportedCornerIndex = -1;

    /// <summary>Number of 800 ms ticks between feature-analysis runs (6 × 800 ms ≈ 4.8 s).</summary>
    private const int FeatureAnalysisTickInterval = 6;

    /// <summary>Number of 800 ms ticks between corner-analysis runs (15 × 800 ms ≈ 12 s).</summary>
    private const int CornerAnalysisTickInterval  = 15;

    // ── AC sanity-check state ────────────────────────────────────────────────

    /// <summary>Wall-clock time when SpeedKmh first became 0 during a Live session; null when speed is non-zero.</summary>
    private DateTime? _speedZeroSince;

    /// <summary>
    /// <see langword="true"/> after a "Telemetry stalled" warning has been logged for the
    /// current stall episode, preventing duplicate entries until speed becomes non-zero again.
    /// </summary>
    private bool _stalledWarningFired;

    /// <summary>Last RPM value seen during an AC live session; <see langword="null"/> before the first sample.</summary>
    private int? _lastSanityRpm;

    /// <summary>Last Gear value seen during an AC live session; <see langword="null"/> before the first sample.</summary>
    private int? _lastSanityGear;

    /// <summary>Wall-clock time when RPM/Gear first became static while speed was still changing.</summary>
    private DateTime? _rpmGearFrozenSince;

    /// <summary>
    /// <see langword="true"/> after a "Partial data" warning has been logged for the
    /// current frozen episode, preventing duplicate entries until RPM/Gear change again.
    /// </summary>
    private bool _partialDataWarningFired;

    // ── Observable state ─────────────────────────────────────────────────────

    [ObservableProperty] private bool   _isSimulating;
    [ObservableProperty] private string _statusText            = "● DETENIDO";
    [ObservableProperty] private string _lapTimeReal           = "--:--.---";
    [ObservableProperty] private string _lapTimeIdeal          = "1:52.847";
    [ObservableProperty] private string _lapDelta              = "---";
    [ObservableProperty] private double _lapPosition;
    [ObservableProperty] private string _lapPositionText       = "Pos:  0%";
    [ObservableProperty] private bool   _isAcConnected;
    [ObservableProperty] private string _connectionStatusText  = "Simulation";
    [ObservableProperty] private TelemetrySource _selectedSource = TelemetrySource.Simulation;

    /// <summary>
    /// <see langword="true"/> when the service has exhausted
    /// <c>MaxRetriesBeforeSuggest</c> reconnect attempts and asks the user
    /// whether to switch to Simulation mode.
    /// </summary>
    [ObservableProperty] private bool _showSwitchToSimulationPrompt;

    // ── Reference lap observables ─────────────────────────────────────────────

    /// <summary><see langword="true"/> while actively recording samples for a new reference lap.</summary>
    [ObservableProperty] private bool   _isRecording;

    /// <summary>
    /// Fraction (0..1) of the current recording lap that has been captured.
    /// Drives the progress bar in the reference toolbar.
    /// </summary>
    [ObservableProperty] private double _recordingProgress;

    /// <summary>Display name of the currently loaded reference, or "Sin referencia".</summary>
    [ObservableProperty] private string _activeReferenceName = "Sin referencia";

    /// <summary>Source label of the active reference ("Grabada", "Importada", or "").</summary>
    [ObservableProperty] private string _referenceSource = string.Empty;

    // ── Live vs Ideal delta display ───────────────────────────────────────────

    /// <summary>EMA-smoothed speed delta formatted for display, e.g. "+3.2 km/h".</summary>
    [ObservableProperty] private string _liveDeltaSpeedText    = "—";

    /// <summary>EMA-smoothed brake delta formatted for display, e.g. "+5 %".</summary>
    [ObservableProperty] private string _liveDeltaBrakeText    = "—";

    /// <summary>EMA-smoothed throttle delta formatted for display, e.g. "-3 %".</summary>
    [ObservableProperty] private string _liveDeltaThrottleText = "—";

    /// <summary>EMA-smoothed yaw-gain delta formatted for display, e.g. "+0.12".</summary>
    [ObservableProperty] private string _liveDeltaYawGainText  = "—";

    /// <summary>Notable event text from the comparator, e.g. "You brake 6.3 m late vs ideal".</summary>
    [ObservableProperty] private string _referenceSummaryText  = string.Empty;

    /// <summary>Saved references for the current car/track, bound to the selector ComboBox.</summary>
    public ObservableCollection<ReferenceLapMeta> SavedReferences { get; } = [];

    [ObservableProperty] private ReferenceLapMeta? _selectedReference;

    partial void OnSelectedReferenceChanged(ReferenceLapMeta? value)
    {
        if (value is null) return;
        var lap = _refStore.LoadReference(value.FilePath);
        if (lap is null) return;
        _comparator.LoadReference(lap);
        ActiveReferenceName = value.DisplayName;
        ReferenceSource     = value.Source == ReferenceLapSource.Recorded ? "Grabada" : "Importada";
        AppLogger.Instance.Info($"Referencia cargada: {value.DisplayName}");
    }

    /// <summary>
    /// Convenience bool for binding a XAML ToggleSwitch:
    /// <see langword="true"/> when <see cref="SelectedSource"/> is
    /// <see cref="TelemetrySource.AssettoCorsa"/>.
    /// </summary>
    public bool IsAcSourceSelected
    {
        get => SelectedSource == TelemetrySource.AssettoCorsa;
        set
        {
            // Ignore deselect attempts — the user must click the other button to switch.
            if (!value) return;
            SelectedSource = TelemetrySource.AssettoCorsa;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Convenience bool for binding a segmented ToggleButton:
    /// <see langword="true"/> when <see cref="SelectedSource"/> is
    /// <see cref="TelemetrySource.Simulation"/>.
    /// </summary>
    public bool IsSimulationSelected
    {
        get => SelectedSource == TelemetrySource.Simulation;
        set
        {
            // Ignore deselect attempts — the user must click the other button to switch.
            if (!value) return;
            SelectedSource = TelemetrySource.Simulation;
            OnPropertyChanged();
        }
    }

    partial void OnSelectedSourceChanged(TelemetrySource value)
    {
        OnPropertyChanged(nameof(IsAcSourceSelected));
        OnPropertyChanged(nameof(IsSimulationSelected));
    }

    // ── Corner / phase observables ────────────────────────────────────────────

    /// <summary>Current driving phase inferred from the latest telemetry sample.</summary>
    [ObservableProperty] private string _currentPhase = "—";

    /// <summary>Direction of the most recently completed corner ("Left", "Right", or "—").</summary>
    [ObservableProperty] private string _lastCornerDirection = "—";

    /// <summary>Understeer index (0..1) during the entry (braking) phase of the last corner.</summary>
    [ObservableProperty] private float _lastCornerUndersteerEntry;

    /// <summary>Understeer index (0..1) during the mid-corner phase of the last corner.</summary>
    [ObservableProperty] private float _lastCornerUndersteerMid;

    /// <summary>Understeer index (0..1) during the exit (acceleration) phase of the last corner.</summary>
    [ObservableProperty] private float _lastCornerUndersteerExit;

    /// <summary>Oversteer index (0..1) during the entry (braking) phase of the last corner.</summary>
    [ObservableProperty] private float _lastCornerOversteerEntry;

    /// <summary>Oversteer index (0..1) during the exit (acceleration) phase of the last corner.</summary>
    [ObservableProperty] private float _lastCornerOversteerExit;

    /// <summary>Duration of the most recently completed corner as a formatted string, e.g. "3.2s".</summary>
    [ObservableProperty] private string _lastCornerDurationText = "—";

    // ── Collections ──────────────────────────────────────────────────────────

    /// <summary>Telemetry channels — each holds both the real (live) and ideal (target) value.</summary>
    public ObservableCollection<TelemetryChannel> Channels    { get; } = new();

    /// <summary>
    /// Live rule-based proposals from the latest completed corner, combining
    /// Entry (braking), Mid (balance), and Exit (traction) phase evaluations.
    /// Suitable for binding to a real-time proposals panel.
    /// </summary>
    public ObservableCollection<Proposal> Proposals { get; } = new();

    // ── Multi-parameter optimization ──────────────────────────────────────────

    /// <summary>
    /// When <see langword="true"/> the proposals panel shows 2-change combinations
    /// scored by <see cref="MultiParameterOptimizer"/> instead of single changes.
    /// Toggled by the "Multi-Optimize" button in the UI.
    /// </summary>
    [ObservableProperty]
    private bool _isMultiOptimizeMode;

    partial void OnIsMultiOptimizeModeChanged(bool value)
    {
        UltraSetupAdvisor.EnableMultiParameterOptimization = value;
        OnPropertyChanged(nameof(IsSingleMode));
        OnPropertyChanged(nameof(IsMultiMode));
        OnPropertyChanged(nameof(IsMultiEmptyState));
    }

    /// <summary>Convenience inverse of <see cref="IsMultiOptimizeMode"/> for ToggleButton binding.</summary>
    public bool IsSingleMode
    {
        get => !IsMultiOptimizeMode;
        set { if (value) IsMultiOptimizeMode = false; }
    }

    /// <summary>Convenience alias of <see cref="IsMultiOptimizeMode"/> for ToggleButton binding.</summary>
    public bool IsMultiMode
    {
        get => IsMultiOptimizeMode;
        set { if (value) IsMultiOptimizeMode = true; }
    }

    // ── Driving mode (Sprint / Endurance) ────────────────────────────────────

    /// <summary>
    /// Active driving session mode.  Changes the score weights used by
    /// <see cref="UltraSetupAdvisor"/> and the virtual-simulator lap weighting.
    /// </summary>
    [ObservableProperty]
    private DrivingMode _currentDrivingMode = DrivingMode.Endurance;

    partial void OnCurrentDrivingModeChanged(DrivingMode value)
    {
        UltraSetupAdvisor.CurrentMode = value;
        OnPropertyChanged(nameof(IsSprintMode));
        OnPropertyChanged(nameof(IsEnduranceMode));
    }

    // ── Race View mode ────────────────────────────────────────────────────────

    /// <summary>
    /// When <see langword="true"/> the TelemetryPage switches to the large-font
    /// Race View overlay, hiding the normal tab panel.
    /// Changes are persisted to <see cref="SetupSettings.RaceViewEnabled"/>.
    /// </summary>
    [ObservableProperty]
    private bool _isRaceViewActive;

    partial void OnIsRaceViewActiveChanged(bool value)
    {
        SetupSettings.Instance.RaceViewEnabled = value;
    }

    /// <summary>
    /// Convenience bool for the Sprint toggle button binding:
    /// <see langword="true"/> when <see cref="CurrentDrivingMode"/> is
    /// <see cref="DrivingMode.Sprint"/>.
    /// </summary>
    public bool IsSprintMode
    {
        get => CurrentDrivingMode == DrivingMode.Sprint;
        set { if (value) CurrentDrivingMode = DrivingMode.Sprint; }
    }

    /// <summary>
    /// Convenience bool for the Endurance toggle button binding:
    /// <see langword="true"/> when <see cref="CurrentDrivingMode"/> is
    /// <see cref="DrivingMode.Endurance"/>.
    /// </summary>
    public bool IsEnduranceMode
    {
        get => CurrentDrivingMode == DrivingMode.Endurance;
        set { if (value) CurrentDrivingMode = DrivingMode.Endurance; }
    }

    /// <summary>
    /// Combination proposals produced by <see cref="MultiParameterOptimizer"/> for
    /// display when <see cref="IsMultiOptimizeMode"/> is <see langword="true"/>.
    /// </summary>
    public ObservableCollection<CombinedProposal> CombinedProposals { get; } = new();

    /// <summary>
    /// <see langword="true"/> when multi-optimize mode is active but no combinations
    /// have been generated yet (used to show the empty-state hint in the UI).
    /// </summary>
    public bool IsMultiEmptyState => IsMultiOptimizeMode && CombinedProposals.Count == 0;

    /// <summary>
    /// The currently selected <see cref="CombinedProposal"/> in the multi-optimize
    /// list (null when nothing is selected).
    /// </summary>
    [ObservableProperty]
    private CombinedProposal? _selectedCombinedProposal;

    /// <summary>
    /// Executes a "Test Combo" action for the supplied
    /// <see cref="CombinedProposal"/>: logs both changes to <see cref="SetupLogs"/>
    /// and pushes the first change as a telemetry proposal for downstream tracking.
    /// </summary>
    [RelayCommand]
    private void TestCombo(CombinedProposal? combo)
    {
        if (combo is null) return;

        foreach (var change in combo.Changes)
        {
            Append(SetupLogs, "COMBO",
                $"{change.Section}:{change.Parameter} {change.Delta}  " +
                $"Δscore≈{change.EstimatedScoreDelta:+0.0;-0.0}  [{change.RiskLevel}]");
        }

        Append(SetupLogs, "COMBO",
            $"Combined Δscore≈{combo.CombinedScoreDelta:+0.0;-0.0}  " +
            $"Δlap≈{combo.EstimatedLapDelta:+0.00;-0.00}s  Risk:{combo.RiskLevel}" +
            (combo.ScoredByMlModel ? "  [ML]" : "  [heuristic]"));

        // Push first change as a trackable proposal
        if (combo.Changes.Length > 0)
        {
            var first = combo.Changes[0];
            SessionsViewModel.Shared.PushTelemetryProposal(new Proposal
            {
                Section    = first.Section,
                Parameter  = first.Parameter,
                Delta      = first.Delta,
                Reason     = combo.ChangesDisplay,
                Confidence = first.Confidence,
            });
        }
    }

    // ── Decision Inspector observables ────────────────────────────────────────

    /// <summary>
    /// Top-3 decision candidates (Safe / Balanced / Aggressive) produced by
    /// <see cref="RiskAwareDecisionEngine"/>, bound to the probabilistic
    /// candidate cards in the UI.
    /// </summary>
    public ObservableCollection<AvoPerformanceSetupAI.ML.DecisionCandidate> AdvisedCandidates { get; } = new();

    /// <summary>
    /// One-line summary of the currently active adaptive blend weights, e.g.
    /// "ML 52 % · RL 31 % · Heur 17 %".
    /// Displayed in the Decision Inspector panel.
    /// </summary>
    [ObservableProperty]
    private string _weightsDisplayText = "ML — · RL — · Heur —";

    /// <summary>
    /// Human-readable explanation from the Driver-vs-Setup discriminator, e.g.
    /// "Setup (0.72) — persistent mid-corner under-rotation".
    /// </summary>
    [ObservableProperty]
    private string _discriminatorExplanationText = "—";

    /// <summary>
    /// Multi-line text block that explains why the Safe / Balanced / Aggressive
    /// candidates were chosen over the others.
    /// </summary>
    [ObservableProperty]
    private string _decisionInspectorText = "Run telemetry to populate decision inspector.";

    // ── Helpers for populating Decision Inspector ──────────────────────────────

    /// <summary>
    /// Refreshes <see cref="AdvisedCandidates"/>, <see cref="WeightsDisplayText"/>,
    /// and <see cref="DecisionInspectorText"/> from a fresh set of candidates.
    /// Must be called on the UI thread.
    /// </summary>
    internal void UpdateDecisionInspector(
        AvoPerformanceSetupAI.ML.DecisionCandidate[] candidates,
        AvoPerformanceSetupAI.ML.AdaptiveWeightEngine? weightEngine,
        AvoPerformanceSetupAI.Telemetry.RootCauseResult rootCause)
    {
        // Update candidate cards
        AdvisedCandidates.Clear();
        foreach (var c in candidates)
            AdvisedCandidates.Add(c);

        // Weights display
        if (weightEngine != null)
        {
            WeightsDisplayText =
                $"ML {weightEngine.MlWeight:P0} · " +
                $"RL {weightEngine.RlWeight:P0} · " +
                $"Heur {weightEngine.HeuristicWeight:P0}";
        }

        // Discriminator explanation
        DiscriminatorExplanationText = string.IsNullOrEmpty(rootCause.Explanation)
            ? $"{rootCause.Cause} ({rootCause.Confidence:P0})"
            : rootCause.Explanation;

        // Decision inspector text
        if (candidates.Length == 0)
        {
            DecisionInspectorText = "No candidates available — waiting for corner data.";
            return;
        }

        var sb = new System.Text.StringBuilder();
        foreach (var c in candidates)
        {
            sb.AppendLine($"[{c.Tier}]  {c.Proposal.Section}:{c.Proposal.Parameter} {c.Proposal.Delta}");
            // UncertaintyEstimate.ToString() produces: "μ=+5.2 σ=1.4 [80%: +1.3..+9.1] [95%: ...]"
            sb.AppendLine($"  {c.Uncertainty}");
            sb.AppendLine($"  Conf={c.CalibratedConfidence:P0}  Risk={c.RiskLevel}  Utility={c.Utility:+0.0;-0.0}");
            sb.AppendLine($"  {c.Explanation}");
        }
        DecisionInspectorText = sb.ToString().TrimEnd();
    }

    /// <summary>Car-behaviour analysis log.</summary>
    public ObservableCollection<AnalysisEntry>    BehaviorLogs { get; } = new();

    /// <summary>Driving-time analysis log.</summary>
    public ObservableCollection<AnalysisEntry>    DrivingLogs  { get; } = new();

    /// <summary>Setup-improvement steps log.</summary>
    public ObservableCollection<AnalysisEntry>    SetupLogs    { get; } = new();

    // ── Agent log stream (Remote mode) ───────────────────────────────────────

    private readonly AgentLogStream _logStream = new();

    /// <summary>Maximum number of entries kept in <see cref="Logs"/>.</summary>
    private const int MaxLogEntries = 500;

    /// <summary>Live Agent log entries streamed via WebSocket.</summary>
    public ObservableCollection<AgentLogEntry> Logs { get; } = new();

    /// <summary>Per-corner phase analysis log.</summary>
    public ObservableCollection<AnalysisEntry>    CornerLogs   { get; } = new();

    /// <summary>Tracks the newest corner timestamp already written to <see cref="CornerLogs"/> to avoid duplicates.</summary>
    private DateTime _lastCornerTimestamp = DateTime.MinValue;

    // ── Channel definitions (name, unit, initial ideal) ──────────────────────
    // Order must match LapProfiles below (index 0..12).

    private static readonly (string Name, string Unit, double Ideal)[] ChannelDefs =
    [
        ("SPEED",    "km/h", 230.0),
        ("RPM",      "rpm",  7100.0),
        ("GEAR",     "",       5.0),
        ("THROTTLE", "%",     98.0),
        ("BRAKE",    "%",      0.0),
        ("STEER",    "°",      1.0),
        ("LAT_G",    "G",      0.1),
        ("LONG_G",   "G",      0.3),
        ("FUEL",     "L",     43.5),
        ("T_F",      "°C",   82.0),
        ("T_R",      "°C",   87.0),
        ("P_F",      "bar",   1.80),
        ("P_R",      "bar",   1.72),
    ];

    // ── Lap-position profiles (pos 0..1 → ideal value) ───────────────────────
    // Eight waypoints model a generic racing circuit:
    //   0.00 = start/finish (exit of last corner, full throttle)
    //   0.15 = heavy braking zone (T1)
    //   0.25 = slow-corner apex
    //   0.38 = acceleration out of slow corner
    //   0.52 = high-speed straight (mid-lap)
    //   0.65 = medium braking zone (T2)
    //   0.78 = fast sweeper
    //   0.88 = final chicane
    //   1.00 = back at start/finish (same as 0.00)
    // Index order must match ChannelDefs exactly.

    private static readonly (double Pos, double Ideal)[][] LapProfiles =
    [
        // 0 SPEED km/h
        [(0.00,230),(0.15, 85),(0.25, 62),(0.38,138),(0.52,248),(0.65,172),(0.78,200),(0.88,128),(1.00,230)],
        // 1 RPM
        [(0.00,7100),(0.15,3400),(0.25,3000),(0.38,5500),(0.52,7400),(0.65,5000),(0.78,6800),(0.88,4800),(1.00,7100)],
        // 2 GEAR
        [(0.00,5),(0.15,2),(0.25,2),(0.38,3),(0.52,6),(0.65,4),(0.78,5),(0.88,3),(1.00,5)],
        // 3 THROTTLE %
        [(0.00,98),(0.15,0),(0.25,22),(0.38,100),(0.52,97),(0.65,25),(0.78,82),(0.88,35),(1.00,98)],
        // 4 BRAKE %
        [(0.00,0),(0.15,88),(0.25,8),(0.38,0),(0.52,0),(0.65,72),(0.78,0),(0.88,62),(1.00,0)],
        // 5 STEER °
        [(0.00,1),(0.15,3),(0.25,14),(0.38,7),(0.52,2),(0.65,6),(0.78,11),(0.88,9),(1.00,1)],
        // 6 LAT_G G
        [(0.00,0.1),(0.15,0.4),(0.25,1.8),(0.38,0.9),(0.52,0.2),(0.65,1.3),(0.78,2.1),(0.88,1.6),(1.00,0.1)],
        // 7 LONG_G G
        [(0.00,0.3),(0.15,-2.4),(0.25,-0.3),(0.38,0.9),(0.52,0.2),(0.65,-2.0),(0.78,0.1),(0.88,-1.8),(1.00,0.3)],
        // 8 FUEL L — decreases through the lap
        [(0.00,43.5),(0.50,43.3),(1.00,43.1)],
        // 9 T_F °C — mild variation
        [(0.00,82),(0.25,85),(0.52,84),(0.78,83),(1.00,82)],
        // 10 T_R °C
        [(0.00,87),(0.25,91),(0.52,88),(0.78,90),(1.00,87)],
        // 11 P_F bar
        [(0.00,1.80),(0.25,1.82),(0.52,1.81),(0.78,1.79),(1.00,1.80)],
        // 12 P_R bar
        [(0.00,1.72),(0.25,1.74),(0.52,1.73),(0.78,1.71),(1.00,1.72)],
    ];

    // ── Ticks per simulated lap ───────────────────────────────────────────────

    private const int LapTicks = 50; // 50 × 800 ms ≈ 40 s simulated lap

    // ── Analysis message banks ────────────────────────────────────────────────

    private static readonly (string Tag, string Msg)[] BehaviorMsgs =
    [
        ("UNDERSTEER", "Subviraje leve en curvas rápidas — reducir spoiler delantero"),
        ("BALANCE",    "Balance freno/aceleración dentro de rango óptimo"),
        ("TEMP_R",     "Temperatura trasera superior a delantera (+5 °C) — revisar camber"),
        ("OVERSTEER",  "Sobreviraje detectado en curvas lentas — aumentar ARB trasero"),
        ("GRIP",       "Pérdida de grip trasero en frenada — verificar presión neumáticos"),
        ("BALANCE",    "Distribución de peso lateral equilibrada — dentro de límites"),
        ("TIRE_WEAR",  "Desgaste asimétrico en neumático delantero izquierdo detectado"),
        ("AERO",       "Eficiencia aerodinámica dentro de parámetros nominales"),
        ("OVERSTEER",  "Rotación excesiva en entrada de curva — reducir diferencial"),
        ("TEMP_F",     "Temperatura delantera óptima — rango 80-86 °C mantenido"),
    ];

    private static readonly (string Tag, string Msg)[] DrivingMsgs =
    [
        ("LAP+0.3",   "Vuelta actual +0.312 s sobre referencia ideal"),
        ("SECTOR 1",  "S1: +0.05 s — frenada tardía en curva 3"),
        ("SECTOR 2",  "S2: -0.02 s — línea de apexe óptima"),
        ("BRAKE",     "Punto de frenada consistente en 12/15 curvas (80 %)"),
        ("THROTTLE",  "Acelerador agresivo en S2 — riesgo de spin en curva 8"),
        ("LAP-0.1",   "Vuelta anterior -0.098 s — mejora confirmada en S3"),
        ("CONSIST",   "Consistencia: 87 % — margen de mejora en S1"),
        ("SPEED",     "Velocidad punta 241 km/h / ideal 245 km/h (−4 km/h)"),
        ("BRAKE",     "Distancia de frenada 5 m mayor que referencia en T1"),
        ("SECTOR 3",  "S3: +0.08 s — salida de última chicane subóptima"),
    ];

    // ── Structured setup steps — each carries the log text and an optional proposal ────

    private static readonly (string Tag, string Msg, Proposal? Proposal)[] SetupSteps =
    [
        ("PASO 1",    "Reducir P_F 1.85 → 1.80 bar para mejorar grip frontal",
            new Proposal { Section="TYRES",       Parameter="PRESSURE_LF",    From="1.85", To="1.80", Delta="-0.05" }),
        ("PASO 2",    "Ajustar camber delantero −0.2° para equilibrar desgaste",
            new Proposal { Section="ALIGNMENT",   Parameter="CAMBER_LF",      From="-2.8", To="-3.0", Delta="-0.2"  }),
        ("PASO 3",    "Aumentar ARB trasero 1 click para reducir sobreviraje",
            new Proposal { Section="ARB",         Parameter="REAR",           From="3",    To="4",    Delta="+1"    }),
        ("PROPUESTA", "Propuesta #3 lista — delta estimado: −0.18 s/vuelta",          null),
        ("PASO 4",    "Reducir spoiler delantero 2 mm — mayor velocidad punta",
            new Proposal { Section="AERO",        Parameter="FRONT_WING",     From="8",    To="6",    Delta="-2"    }),
        ("VERIFICAR", "Comprobar temperatura neumáticos tras aplicar setup",           null),
        ("PASO 5",    "Incrementar bump trasero 1 click — mejora estabilidad",
            new Proposal { Section="DAMPERS",     Parameter="BUMP_REAR",      From="4",    To="5",    Delta="+1"    }),
        ("ÓPTIMO",    "Setup óptimo estimado para condición actual: pista seca",       null),
        ("PASO 6",    "Ajustar diff aceleración +2 para mejor tracción en salidas",
            new Proposal { Section="ELECTRONICS", Parameter="DIFF_ACC",       From="50",   To="52",   Delta="+2"    }),
        ("PROPUESTA", "Propuesta #4 — reducir ride height trasero 2 mm",
            new Proposal { Section="SUSPENSION",  Parameter="ROD_LENGTH_RR",  From="10",   To="8",    Delta="-2"    }),
    ];

    // ── Constructor ───────────────────────────────────────────────────────────

    public TelemetryViewModel()
    {
        var rngSeed = new Random(42);
        foreach (var (name, unit, ideal) in ChannelDefs)
        {
            Channels.Add(new TelemetryChannel
            {
                Name       = name,
                Unit       = unit,
                IdealValue = ideal,
                RealValue  = Math.Round(ideal * (0.92 + rngSeed.NextDouble() * 0.06), 2),
            });
        }

        // Seed initial entries so the terminals are not empty on first open
        Append(BehaviorLogs, "INICIO",  "Módulo de análisis de comportamiento listo.");
        Append(BehaviorLogs, "INFO",    "Pulse ▶ Iniciar para activar la telemetría en tiempo real.");
        Append(DrivingLogs,  "INICIO",  "Analizador de tiempo de conducción activo.");
        Append(DrivingLogs,  "INFO",    "Los datos de vuelta aparecerán al iniciar la simulación.");
        Append(SetupLogs,    "INICIO",  "Motor de propuestas de setup inicializado.");
        Append(SetupLogs,    "INFO",    "Los pasos de mejora se generarán automáticamente.");
        Append(CornerLogs,   "INICIO",  "Analizador de fases de curva activo.");
        Append(CornerLogs,   "INFO",    "Los resúmenes de curva aparecerán al conectar Assetto Corsa.");

        // Keep IsMultiEmptyState in sync whenever CombinedProposals changes
        CombinedProposals.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsMultiEmptyState));

        // Read the persisted Race View state after SetupSettings is fully initialised.
        _isRaceViewActive = SetupSettings.Instance.RaceViewEnabled;
    }

    /// <summary>
    /// Must be called once from the UI thread (page constructor) to allow the timer
    /// callbacks to marshal back onto the UI dispatcher.
    /// </summary>
    public async Task InitializeAsync(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;

        // Wire TelemetryService.ConnectionChanged so background retries update
        // the UI-bound properties on the UI thread.
        _connectionChangedHandler = (isConnected, statusText) =>
            _dispatcher.TryEnqueue(() =>
            {
                IsAcConnected        = isConnected;
                ConnectionStatusText = statusText;
                AppLogger.Instance.Info(statusText);
            });
        _telemetryService.ConnectionChanged += _connectionChangedHandler;

        // Wire SuggestSwitchToSimulation: after 10 failed reconnect attempts the
        // service asks the user whether to fall back to simulation mode.
        _suggestSwitchHandler = () =>
            _dispatcher.TryEnqueue(() =>
            {
                ShowSwitchToSimulationPrompt = true;
                AppLogger.Instance.Warn(
                    "AC no responde tras 10 intentos — se sugiere cambiar a Simulación.");
            });
        _telemetryService.SuggestSwitchToSimulation += _suggestSwitchHandler;

        // Create a 50 ms DispatcherTimer for real-time phase/corner updates.
        // Must be created on the UI thread (DispatcherTimer fires on the thread it was created on).
        _fastTimer          = new DispatcherTimer();
        _fastTimer.Interval = TimeSpan.FromMilliseconds(50);
        _fastTimer.Tick    += OnFastTick;

        // Keep IsRaceViewActive in sync when the user changes the setting from
        // the Configuración page (fires on the UI thread via SetupSettings).
        SetupSettings.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SetupSettings.RaceViewEnabled))
                IsRaceViewActive = SetupSettings.Instance.RaceViewEnabled;
        };

        // Forward diagnostic entries from SessionsViewModel (Apply Proposal etc.) to this
        // Logs collection so they appear in the Agent Logs tab automatically.
        _sessionLogsHandler = (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add &&
                e.NewItems is not null)
            {
                foreach (var item in e.NewItems)
                {
                    if (item is not AgentLogEntry entry) continue;
                    var captured = entry;
                    dispatcher.TryEnqueue(() =>
                    {
                        Logs.Add(captured);
                        while (Logs.Count > MaxLogEntries)
                            Logs.RemoveAt(0);
                    });
                }
            }
        };
        SessionsViewModel.Shared.Logs.CollectionChanged += _sessionLogsHandler;

        // Start live-log stream when in Remote mode — awaited so failures surface
        // immediately rather than being lost in a fire-and-forget task.
        if (SetupSettings.Instance.Mode == AppMode.Remote)
        {
            try
            {
                await StartLogStreamAsync(dispatcher);
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException is not null ? $" ({ex.InnerException.Message})" : string.Empty;
                var msg = $"LOG WS FAILED [{ex.GetType().Name}]: {ex.Message}{inner}";
                AppLogger.Instance.Warn(msg);
                dispatcher.TryEnqueue(() =>
                    Logs.Add(new AgentLogEntry
                    {
                        TUtc = DateTime.UtcNow.ToString("O"),
                        Lvl  = "ERR",
                        Cat  = "Client",
                        Msg  = msg,
                    }));
            }
        }
    }

    /// <summary>Unsubscribes from <see cref="TelemetryService"/> events and releases resources.</summary>
    public void Dispose()
    {
        if (_connectionChangedHandler is not null)
        {
            _telemetryService.ConnectionChanged -= _connectionChangedHandler;
            _connectionChangedHandler = null;
        }
        if (_suggestSwitchHandler is not null)
        {
            _telemetryService.SuggestSwitchToSimulation -= _suggestSwitchHandler;
            _suggestSwitchHandler = null;
        }
        _telemetryService.Dispose();
        _updateTimer?.Dispose();

        if (_logEntryHandler is not null)
        {
            _logStream.OnLog -= _logEntryHandler;
            _logEntryHandler  = null;
        }
        if (_logStatusHandler is not null)
        {
            _logStream.OnStatus -= _logStatusHandler;
            _logStatusHandler    = null;
        }
        if (_sessionLogsHandler is not null)
        {
            SessionsViewModel.Shared.Logs.CollectionChanged -= _sessionLogsHandler;
            _sessionLogsHandler = null;
        }
        // Best-effort graceful shutdown of the WebSocket (fire-and-forget is acceptable
        // here because the ViewModel is being disposed — the read loop will self-terminate
        // when the cancellation token fires).
        _ = _logStream.StopAsync();
    }

    // ── Agent log stream helpers ──────────────────────────────────────────────

    private async Task StartLogStreamAsync(DispatcherQueue dispatcher)
    {
        void EnqueueLogEntry(AgentLogEntry entry) =>
            dispatcher.TryEnqueue(() =>
            {
                Logs.Add(entry);
                while (Logs.Count > MaxLogEntries)
                    Logs.RemoveAt(0);
            });

        void AddSyntheticEntry(string msg) =>
            EnqueueLogEntry(new AgentLogEntry
            {
                TUtc = DateTime.UtcNow.ToString("O"),
                Lvl  = "SYS",
                Cat  = "LogStream",
                Msg  = msg,
            });

        _logEntryHandler  = EnqueueLogEntry;
        _logStatusHandler = AddSyntheticEntry;

        _logStream.OnLog    += _logEntryHandler;
        _logStream.OnStatus += _logStatusHandler;

        var wsUrl = SetupSettings.Instance.AgentLogsWsUrl;
        AppLogger.Instance.Info($"[LogStream] Connecting to: {wsUrl}");

        try
        {
            await _logStream.StartAsync(wsUrl).ConfigureAwait(false);
            AppLogger.Instance.Info("[LogStream] StartAsync completed.");
        }
        catch (Exception ex)
        {
            var msg = $"Log WS connection failed: {ex.Message}";
            AppLogger.Instance.Warn(msg);
            AddSyntheticEntry(msg);
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void StartSimulation()
    {
        IsSimulating = true;
        StatusText   = "● ACTIVO";
        _tick        = 0;
        ResetAcSanityState();

        // Delegate source management to TelemetryService.
        // ConnectionChanged will update IsAcConnected + ConnectionStatusText asynchronously.
        _telemetryService.Start(SelectedSource);
        AppLogger.Instance.Ai("Telemetría en tiempo real ACTIVADA — tick cada 800 ms.");

        _updateTimer = new System.Threading.Timer(OnTick, null, 0, 800);
        _fastTimer?.Start();
        StartSimulationCommand.NotifyCanExecuteChanged();
        StopSimulationCommand.NotifyCanExecuteChanged();
    }

    private bool CanStart() => !IsSimulating;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void StopSimulation()
    {
        IsSimulating = false;
        StatusText   = "● DETENIDO";
        _updateTimer?.Dispose();
        _updateTimer = null;
        _fastTimer?.Stop();
        _telemetryService.Stop();
        IsAcConnected                = false;
        ConnectionStatusText         = "Simulation";
        ShowSwitchToSimulationPrompt = false;
        ResetAcSanityState();
        // Cancel any in-progress recording
        _recordingBuffer.Clear();
        _waitingForLapStart = false;
        IsRecording         = false;
        RecordingProgress   = 0.0;
        StartRecordingCommand.NotifyCanExecuteChanged();
        CancelRecordingCommand.NotifyCanExecuteChanged();
        CurrentPhase  = "—";
        AppLogger.Instance.Info("Telemetría pausada.");
        StartSimulationCommand.NotifyCanExecuteChanged();
        StopSimulationCommand.NotifyCanExecuteChanged();
    }

    private bool CanStop() => IsSimulating;

    // ── Reference recording commands ──────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanStartRecording))]
    private void StartRecording()
    {
        _recordingBuffer.Clear();
        _waitingForLapStart = true;
        IsRecording         = false;   // will flip to true once a clean lap-start arrives
        RecordingProgress   = 0.0;
        AppLogger.Instance.Info("Grabación de vuelta ideal: esperando inicio de vuelta…");
        StartRecordingCommand.NotifyCanExecuteChanged();
        CancelRecordingCommand.NotifyCanExecuteChanged();
    }

    private bool CanStartRecording() => IsSimulating && IsAcConnected && !IsRecording && !_waitingForLapStart;

    [RelayCommand(CanExecute = nameof(CanCancelRecording))]
    private void CancelRecording()
    {
        _recordingBuffer.Clear();
        _waitingForLapStart = false;
        IsRecording         = false;
        RecordingProgress   = 0.0;
        AppLogger.Instance.Info("Grabación de vuelta ideal cancelada.");
        StartRecordingCommand.NotifyCanExecuteChanged();
        CancelRecordingCommand.NotifyCanExecuteChanged();
    }

    private bool CanCancelRecording() => IsRecording || _waitingForLapStart;

    /// <summary>
    /// Called from <see cref="Views.TelemetryPage"/> after the FileOpenPicker
    /// returns a CSV path.  Runs the import on a background thread to keep the
    /// UI responsive and then loads the result into the comparator.
    /// </summary>
    public async Task ImportCsvFileAsync(string filePath)
    {
        try
        {
            var lap = await Task.Run(() => _csvImporter.Import(filePath));
            var path = _refStore.SaveReference(lap);
            _comparator.LoadReference(lap);
            ActiveReferenceName = System.IO.Path.GetFileNameWithoutExtension(path);
            ReferenceSource     = "Importada";
            RefreshSavedReferences(lap.CarId, lap.TrackId);
            AppLogger.Instance.Info($"CSV importado y guardado: {path}");
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Warn($"Error al importar CSV: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ClearAnalysis()
    {
        BehaviorLogs.Clear();
        DrivingLogs.Clear();
        SetupLogs.Clear();
        CornerLogs.Clear();
        Proposals.Clear();
        _lastReportedCornerIndex  = -1;
        CurrentPhase              = "—";
        LastCornerDirection       = "—";
        LastCornerUndersteerEntry = 0f;
        LastCornerUndersteerMid   = 0f;
        LastCornerUndersteerExit  = 0f;
        LastCornerOversteerEntry  = 0f;
        LastCornerOversteerExit   = 0f;
        LastCornerDurationText    = "—";
        LiveDeltaSpeedText        = "—";
        LiveDeltaBrakeText        = "—";
        LiveDeltaThrottleText     = "—";
        LiveDeltaYawGainText      = "—";
        ReferenceSummaryText      = string.Empty;
        // AcTelemetryReader stamps every sample with DateTime.UtcNow, so using
        // DateTime.UtcNow here guarantees only corners whose first sample arrives
        // after the clear will be logged — no stale corners re-appear.
        _lastCornerTimestamp = DateTime.UtcNow;
        AppLogger.Instance.Info("Paneles de análisis de telemetría limpiados.");
    }

    /// <summary>
    /// Accepts the "switch to Simulation?" suggestion: changes the source, dismisses the
    /// prompt, and re-starts telemetry in Simulation mode so the session continues
    /// without interruption.
    /// </summary>
    [RelayCommand]
    private void ConfirmSwitchToSimulation()
    {
        ShowSwitchToSimulationPrompt = false;
        SelectedSource = TelemetrySource.Simulation;
        if (IsSimulating)
        {
            _telemetryService.Start(TelemetrySource.Simulation);
            // ConnectionChanged will update status text asynchronously.
        }
        AppLogger.Instance.Info("Fuente cambiada a Simulación por solicitud del usuario.");
    }

    /// <summary>Dismisses the "switch to Simulation?" prompt without changing the source.</summary>
    [RelayCommand]
    private void DismissSwitchToSimulation()
    {
        ShowSwitchToSimulationPrompt = false;
        AppLogger.Instance.Info("Sugerencia de cambio a Simulación descartada — continuando reconexión.");
    }

    // ── Simulation tick ───────────────────────────────────────────────────────

    private void OnTick(object? state)
    {
        var t = ++_tick;

        _dispatcher?.TryEnqueue(() =>
        {
            UpdateChannels();

            if (t % 4  == 0) UpdateLapTime();
            if (t % 5  == 0) Append(BehaviorLogs, BehaviorMsgs[(t / 5)  % BehaviorMsgs.Length]);
            if (t % 7  == 0) Append(DrivingLogs,  DrivingMsgs [(t / 7)  % DrivingMsgs.Length]);
            if (t % 11 == 0)
            {
                var step = SetupSteps[(t / 11) % SetupSteps.Length];
                Append(SetupLogs, step.Tag, step.Msg);
                if (step.Proposal is not null)
                    SessionsViewModel.Shared.PushTelemetryProposal(step.Proposal);
            }

            // ── Feature analysis from AC ring buffer ──────────────────────────
            if (IsAcConnected && t % FeatureAnalysisTickInterval == 0)
                RunFeatureAnalysis();
            if (IsAcConnected && t % CornerAnalysisTickInterval == 0)
                RunCornerAnalysis();
        });
    }

    /// <summary>
    /// Extracts features from the most recent 2 seconds of samples in the ring buffer,
    /// appends human-readable results to the behaviour log, and pushes any rule-based
    /// setup proposals to <see cref="SessionsViewModel.Shared"/>.
    /// </summary>
    private void RunFeatureAnalysis()
    {
        var frame = FeatureExtractor.ExtractFrame(_telemetryService.Buffer, windowSeconds: 2.0);
        if (frame.SampleCount == 0) return;

        foreach (var (tag, msg) in FeatureExtractor.FormatLog(in frame))
            Append(BehaviorLogs, tag, msg);

        // Push rule-based proposals so they appear in the Sessions panel
        var proposals = RuleEngine.Evaluate(in frame);
        if (proposals.Length > 0)
            SessionsViewModel.Shared.PushTelemetryProposals(proposals);
    }

    /// <summary>
    /// Analyzes the most recent 30 seconds of samples for corner events and
    /// appends new per-corner summaries to <see cref="CornerLogs"/>.
    /// Only corners whose <see cref="CornerSummary.StartTimestamp"/> is newer
    /// than the last logged timestamp are added (prevents duplicates across calls).
    /// </summary>
    private void RunCornerAnalysis()
    {
        var corners = CornerPhaseAnalyzer.Analyze(_telemetryService.Buffer, windowSeconds: 30.0);
        foreach (var cs in corners)
        {
            if (cs.StartTime <= _lastCornerTimestamp) continue;
            foreach (var (tag, msg) in CornerPhaseAnalyzer.FormatLog(in cs))
                Append(CornerLogs, tag, msg);
        }
        if (corners.Length > 0)
            _lastCornerTimestamp = corners[^1].StartTime;
    }

    private void UpdateChannels()
    {
        // ── When AC is connected and in a live session, use real sample data ──
        if (IsAcConnected)
        {
            var sample = _telemetryService.Buffer.ReadLast();
            if (sample.AcStatus == (int)AcStatus.Live && sample.SpeedKmh >= 0)
            {
                UpdateChannelsFromSample(in sample);
                return;
            }
        }

        // ── Simulation fallback ───────────────────────────────────────────────

        // Advance lap position (cycles 0 → 1 over LapTicks ticks)
        LapPosition     = (_tick % LapTicks) / (double)LapTicks;
        LapPositionText = $"Pos: {LapPosition * 100,3:F0}%";

        for (int i = 0; i < Channels.Count && i < LapProfiles.Length; i++)
        {
            var ch    = Channels[i];
            var ideal = LerpProfile(LapProfiles[i], LapPosition);
            ch.IdealValue = Math.Round(ideal, 2);

            // Oscillate real value ±4 % around the position-adjusted ideal
            var noise = (_rng.NextDouble() - 0.5) * 0.08;
            ch.RealValue = Math.Round(ideal * (1.0 + noise), 2);
        }
    }

    /// <summary>
    /// Updates all channels from a real AC <see cref="TelemetrySample"/>.
    /// Ideal values still come from the <see cref="LapProfiles"/> interpolation,
    /// keyed on the actual normalised lap position reported by AC.
    /// Real values map directly from the sample fields.
    /// </summary>
    private void UpdateChannelsFromSample(in TelemetrySample s)
    {
        // Lap position from AC spline
        LapPosition     = Math.Clamp(s.NormalizedLapPos, 0.0, 1.0);
        LapPositionText = $"Pos: {LapPosition * 100,3:F0}%";

        // Prepare real values for each named channel
        // Index order must match ChannelDefs (SPEED, RPM, GEAR, THROTTLE, BRAKE,
        //   STEER, LAT_G, LONG_G, FUEL, T_F, T_R, P_F, P_R)
        var realValues = new double[]
        {
            s.SpeedKmh,
            s.Rpms,
            Math.Max(0, s.Gear - 1),                                   // AC: 0=R,1=N,2=1st → show 0..n
            s.Throttle * 100.0,
            s.Brake    * 100.0,
            s.SteerAngle * (180.0 / Math.PI),                          // rad → degrees
            s.AccGLateral,
            s.AccGLongitudinal,
            s.Fuel,
            (s.TyreTempFL + s.TyreTempFR) * 0.5,                      // front average °C
            (s.TyreTempRL + s.TyreTempRR) * 0.5,                      // rear  average °C
            (s.TyrePressureFL + s.TyrePressureFR) * 0.5,              // front average bar
            (s.TyrePressureRL + s.TyrePressureRR) * 0.5,              // rear  average bar
        };

        for (int i = 0; i < Channels.Count && i < LapProfiles.Length && i < realValues.Length; i++)
        {
            var ch    = Channels[i];
            var ideal = LerpProfile(LapProfiles[i], LapPosition);
            ch.IdealValue = Math.Round(ideal, 2);
            ch.RealValue  = Math.Round(realValues[i], 2);
        }
    }

    /// <summary>
    /// Linearly interpolates <paramref name="profile"/> at the given lap
    /// <paramref name="pos"/> (0..1). The profile must start at pos 0 and end at pos 1.
    /// </summary>
    private static double LerpProfile((double Pos, double Ideal)[] profile, double pos)
    {
        for (int i = 0; i < profile.Length - 1; i++)
        {
            if (pos >= profile[i].Pos && pos <= profile[i + 1].Pos)
            {
                var span = profile[i + 1].Pos - profile[i].Pos;
                var lerpT = span > 0 ? (pos - profile[i].Pos) / span : 0;
                return profile[i].Ideal + lerpT * (profile[i + 1].Ideal - profile[i].Ideal);
            }
        }
        return profile[^1].Ideal;
    }

    private void UpdateLapTime()
    {
        const int baseMs = 112847; // 1:52.847
        // Range: -0.4 × 600 = -240 ms to +0.6 × 600 = +360 ms; avg bias ≈ +60 ms
        var deltaMs      = (int)((_rng.NextDouble() - 0.4) * 600);
        var realMs       = baseMs + deltaMs;
        var m            = realMs / 60000;
        var s            = (realMs % 60000) / 1000;
        var ms           = realMs % 1000;
        LapTimeReal      = $"{m}:{s:D2}.{ms:D3}";
        LapDelta         = deltaMs >= 0 ? $"+{deltaMs / 1000.0:F3}" : $"{deltaMs / 1000.0:F3}";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // ── 50 ms fast-path: phase detection + corner summary ─────────────────────

    /// <summary>
    /// Fires every 50 ms on the UI thread. Keeps <see cref="CurrentPhase"/>,
    /// the <c>LastCorner*</c> observables, and <see cref="Proposals"/> up-to-date
    /// without blocking the 250 Hz AC poll loop.
    /// </summary>
    private void OnFastTick(object? sender, object e)
    {
        if (!IsAcConnected) return;

        // 1. Derive current driving phase from the most recent sample
        var sample = _telemetryService.Buffer.ReadLast();
        if (sample.AcStatus == (int)AcStatus.Live)
            CurrentPhase = DerivePhaseLabel(in sample);
        else
            CurrentPhase = "—";

        // 2. AC sanity checks (stalled telemetry / partial data)
        if (sample.AcStatus == (int)AcStatus.Live)
            CheckAcSanity(in sample);

        // 3. Feed new samples into the corner detector (non-blocking)
        _cornerDetector.Update(_telemetryService.Buffer);

        // 4. Reflect the latest completed corner in the bindable properties
        var latest = _cornerDetector.LatestCorner;
        if (latest.HasValue && latest.Value.CornerIndex != _lastReportedCornerIndex)
        {
            var cs = latest.Value;
            _lastReportedCornerIndex  = cs.CornerIndex;
            LastCornerDirection       = cs.Direction == CornerDirection.Left  ? "Left"
                                      : cs.Direction == CornerDirection.Right ? "Right" : "—";
            LastCornerUndersteerEntry = cs.EntryFrame.UndersteerEntry;
            LastCornerUndersteerMid   = cs.MidFrame.UndersteerMid;
            LastCornerUndersteerExit  = cs.ExitFrame.UndersteerExit;
            LastCornerOversteerEntry  = cs.EntryFrame.OversteerEntry;
            LastCornerOversteerExit   = cs.ExitFrame.OversteerExit;
            LastCornerDurationText    = $"{cs.Duration.TotalSeconds:F1}s";

            // 5. Phase-aware RuleEngine evaluation
            UpdateProposalsFromCorner(in cs);
        }

        // 6. Reference lap recording
        if ((IsRecording || _waitingForLapStart) && sample.AcStatus == (int)AcStatus.Live)
            UpdateRecording(in sample);

        // 7. Live vs Ideal comparison (distance-aligned, EMA-smoothed)
        if (_comparator.IsActive && sample.AcStatus == (int)AcStatus.Live)
            UpdateLiveDeltas(in sample);
    }

    // ── Recording helpers ─────────────────────────────────────────────────────

    private const float LapWrapThreshold   = 0.10f; // lapPos < this after being > 0.90 = new lap
    private const float LapWrapHighWater   = 0.90f;

    /// <summary>
    /// State machine for capturing a clean single lap.
    /// Waits for NormalizedLapPos to cross the start/finish line, then collects
    /// one sample per fast-tick (≈20 Hz) until the next crossing, then auto-saves.
    /// </summary>
    private void UpdateRecording(in TelemetrySample s)
    {
        var lapPos = s.NormalizedLapPos;

        // Detect start/finish crossing: lapPos wraps from high → low
        var crossed = _lastFastLapPos > LapWrapHighWater && lapPos < LapWrapThreshold;
        _lastFastLapPos = lapPos;

        if (_waitingForLapStart)
        {
            if (crossed)
            {
                // Clean lap start detected — begin recording
                _waitingForLapStart = false;
                IsRecording         = true;
                _recordingBuffer.Clear();
                _lastRecordedLapPos = lapPos;
                RecordingProgress   = 0.0;
                AppLogger.Instance.Info("Grabación de vuelta ideal iniciada.");
                StartRecordingCommand.NotifyCanExecuteChanged();
                CancelRecordingCommand.NotifyCanExecuteChanged();
            }
            return;
        }

        if (!IsRecording) return;

        // Lap complete: crossing detected while already recording
        if (crossed && _recordingBuffer.Count > 50)
        {
            FinaliseLapRecording();
            return;
        }

        // Collect sample — only when lapPos has advanced (distance-based dedup)
        if (Math.Abs(lapPos - _lastRecordedLapPos) >= 0.0005f || _recordingBuffer.Count == 0)
        {
            _recordingBuffer.Add(BuildRecordingSample(in s));
            _lastRecordedLapPos = lapPos;
            RecordingProgress   = lapPos;
        }
    }

    private void FinaliseLapRecording()
    {
        IsRecording       = false;
        RecordingProgress = 1.0;
        var raw           = new List<ReferenceLapSample>(_recordingBuffer);
        _recordingBuffer.Clear();
        StartRecordingCommand.NotifyCanExecuteChanged();
        CancelRecordingCommand.NotifyCanExecuteChanged();

        if (raw.Count < 2)
        {
            AppLogger.Instance.Warn("Grabación descartada — menos de 2 muestras.");
            return;
        }

        try
        {
            var lap = new ReferenceLap
            {
                CarId      = CurrentCarId,
                TrackId    = CurrentTrackId,
                Source     = ReferenceLapSource.Recorded,
                CreatedUtc = DateTime.UtcNow,
                Samples    = ReferenceLapResampler.Resample(raw),
            };
            var path = _refStore.SaveReference(lap);
            _comparator.LoadReference(lap);
            ActiveReferenceName = $"Vuelta grabada {lap.CreatedUtc:HH:mm:ss}";
            ReferenceSource     = "Grabada";
            RefreshSavedReferences(lap.CarId, lap.TrackId);
            AppLogger.Instance.Ai($"Vuelta ideal grabada y guardada: {path}");
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Warn($"Error al guardar vuelta ideal: {ex.Message}");
        }
    }

    private static ReferenceLapSample BuildRecordingSample(in TelemetrySample s)
    {
        var yawGain = BalanceMetrics.ComputeYawGain(s.YawRate, s.SteerAngle, s.SpeedKmh);
        return new ReferenceLapSample
        {
            LapDistPct        = s.NormalizedLapPos,
            SpeedKmh          = s.SpeedKmh,
            Throttle          = s.Throttle,
            Brake             = s.Brake,
            Steering          = s.SteerAngle,
            Gear              = s.Gear,
            Rpm               = s.Rpms,
            LatG              = s.AccGLateral,
            LongG             = s.AccGLongitudinal,
            YawGain           = yawGain,
            SlipAngleFrontAvg = (Math.Abs(s.SlipAngleFL) + Math.Abs(s.SlipAngleFR)) * 0.5f,
            SlipAngleRearAvg  = (Math.Abs(s.SlipAngleRL) + Math.Abs(s.SlipAngleRR)) * 0.5f,
            WheelSlipRearAvg  = (Math.Abs(s.WheelSlipRL) + Math.Abs(s.WheelSlipRR)) * 0.5f,
            TyreTempAvg       = (s.TyreTempFL + s.TyreTempFR + s.TyreTempRL + s.TyreTempRR) * 0.25f,
            TyreTempFL        = s.TyreTempFL,
            TyreTempFR        = s.TyreTempFR,
            TyreTempRL        = s.TyreTempRL,
            TyreTempRR        = s.TyreTempRR,
            TyrePressureFL    = s.TyrePressureFL,
            TyrePressureFR    = s.TyrePressureFR,
            TyrePressureRL    = s.TyrePressureRL,
            TyrePressureRR    = s.TyrePressureRR,
        };
    }

    // ── Live vs Ideal update ──────────────────────────────────────────────────

    /// <summary>
    /// Calls the comparator with the latest live sample (distance-aligned,
    /// EMA-smoothed) and updates the four display properties.
    /// </summary>
    private void UpdateLiveDeltas(in TelemetrySample s)
    {
        var frame = _comparator.Update(in s);
        if (frame is null) return;

        static string FmtSpeed(float v)    => v >= 0 ? $"+{v:F1} km/h" : $"{v:F1} km/h";
        static string FmtPct(float v)      => v >= 0 ? $"+{v * 100f:F0} %" : $"{v * 100f:F0} %";
        static string FmtYaw(float v)      => v >= 0 ? $"+{v:F2}" : $"{v:F2}";

        LiveDeltaSpeedText    = FmtSpeed(frame.DeltaSpeedKmh);
        LiveDeltaBrakeText    = FmtPct(frame.DeltaBrake);
        LiveDeltaThrottleText = FmtPct(frame.DeltaThrottle);
        LiveDeltaYawGainText  = FmtYaw(frame.DeltaYawGain);

        if (!string.IsNullOrEmpty(frame.SummaryText))
            ReferenceSummaryText = frame.SummaryText;
    }

    // ── Saved-reference list refresh ─────────────────────────────────────────

    private void RefreshSavedReferences(string carId, string trackId)
    {
        SavedReferences.Clear();
        foreach (var meta in _refStore.ListReferences(carId, trackId))
            SavedReferences.Add(meta);
    }

    /// <summary>
    /// Derives a human-readable driving phase label from a single
    /// <see cref="TelemetrySample"/>, using the same thresholds as
    /// <see cref="CornerDetector"/> for consistency.
    /// </summary>
    private static string DerivePhaseLabel(in TelemetrySample s)
    {
        if (s.Brake > 0.12f)                                                    return "Entry";
        if (s.Throttle > 0.25f)                                                 return "Exit";
        if (Math.Abs(s.AccGLateral) > 0.2f || Math.Abs(s.SteerAngle) > 0.12f) return "Mid";
        return "Straight";
    }

    /// <summary>
    /// Evaluates <see cref="RuleEngine"/> on each phase-specific
    /// <see cref="FeatureFrame"/> of the completed corner, merges results by
    /// <c>Section/Parameter</c> (keeping highest confidence), and updates
    /// <see cref="Proposals"/>. Also pushes to
    /// <see cref="SessionsViewModel.Shared"/> for the Sessions panel.
    /// </summary>
    private void UpdateProposalsFromCorner(in CornerSummary cs)
    {
        // Phase-aware evaluation: Entry for braking, Mid for balance, Exit for traction
        var entryProps = RuleEngine.Evaluate(cs.EntryFrame);
        var midProps   = RuleEngine.Evaluate(cs.MidFrame);
        var exitProps  = RuleEngine.Evaluate(cs.ExitFrame);

        // Merge by Section/Parameter, keeping the highest-confidence proposal per key
        var merged = new Dictionary<string, Proposal>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in entryProps.Concat(midProps).Concat(exitProps))
        {
            var key = $"{p.Section}/{p.Parameter}";
            if (!merged.TryGetValue(key, out var existing) || p.Confidence > existing.Confidence)
                merged[key] = p;
        }

        Proposals.Clear();
        foreach (var p in merged.Values.OrderByDescending(p => p.Confidence).Take(RuleEngine.MaxProposals))
            Proposals.Add(p);

        if (Proposals.Count > 0)
            SessionsViewModel.Shared.PushTelemetryProposals([.. Proposals]);

        // ── Refresh CombinedProposals when multi-optimize mode is active ──────
        if (IsMultiOptimizeMode && Proposals.Count >= MultiParameterOptimizer.MinCandidatesForCombo)
        {
            // Build a minimal AdvisedProposal pool from the current Proposals list
            // (no ML predictor available here — use heuristic scoring only).
            var advisedPool = Proposals
                .Take(MultiParameterOptimizer.MaxCandidatePool)
                .Select(p =>
                {
                    var (bal, trac, brk, lap, risk) =
                        UltraSetupAdvisor.HeuristicEstimate(p.Section, p.Parameter, p.Confidence);
                    float score = bal * 0.35f + trac * 0.25f + brk * 0.20f;
                    return new AdvisedProposal
                    {
                        Section              = p.Section,
                        Parameter            = p.Parameter,
                        Delta                = p.Delta,
                        Reason               = p.Reason,
                        Confidence           = p.Confidence,
                        EstimatedLapDeltaSec = lap,
                        EstimatedScoreDelta  = Math.Clamp(score, 0f, 30f),
                        RiskLevel            = risk,
                        ScoredByMlModel      = false,
                    };
                })
                .ToArray();

            var combos = MultiParameterOptimizer.Optimize(
                advisedPool, default, null, null);

            CombinedProposals.Clear();
            foreach (var c in combos)
                CombinedProposals.Add(c);
        }
        else if (!IsMultiOptimizeMode)
        {
            CombinedProposals.Clear();
        }
    }

    private static void Append(ObservableCollection<AnalysisEntry> col, string tag, string msg)
    {
        col.Add(new AnalysisEntry { Tag = tag, Message = msg });
        while (col.Count > 60) col.RemoveAt(0); // keep last 60 entries
    }

    private static void Append(ObservableCollection<AnalysisEntry> col, (string Tag, string Msg) entry)
        => Append(col, entry.Tag, entry.Msg);

    // ── AC sanity checks ──────────────────────────────────────────────────────

    /// <summary>
    /// Called every 50 ms when connected to AC in a Live session.
    /// Detects two failure modes and logs a warning entry to
    /// <see cref="BehaviorLogs"/> once per episode:
    /// <list type="bullet">
    ///   <item><b>Telemetry stalled</b> — SpeedKmh has been 0 for &gt;3 s.</item>
    ///   <item><b>Partial data</b>  — RPM and Gear have not changed for &gt;5 s
    ///   while SpeedKmh is varying.</item>
    /// </list>
    /// </summary>
    private void CheckAcSanity(in TelemetrySample s)
    {
        var now = DateTime.UtcNow;

        // ── Check 1: Stalled telemetry ────────────────────────────────────────
        if (s.SpeedKmh < 0.5f)
        {
            // Speed is (effectively) zero
            _speedZeroSince ??= now;
            if (!_stalledWarningFired && (now - _speedZeroSince.Value).TotalSeconds > 3.0)
            {
                _stalledWarningFired = true;
                Append(BehaviorLogs, "WARN", "Telemetry stalled — SpeedKmh = 0 for > 3 s while session is Live.");
                AppLogger.Instance.Warn("AC sanity: telemetry stalled (SpeedKmh = 0 for > 3 s).");
            }
        }
        else
        {
            // Speed is non-zero — reset stall tracking
            _speedZeroSince      = null;
            _stalledWarningFired = false;
        }

        // ── Check 2: Partial data (RPM/Gear frozen while speed changes) ───────
        // On the very first sample we have no prior values — record and skip the timer.
        if (_lastSanityRpm is null)
        {
            _lastSanityRpm  = s.Rpms;
            _lastSanityGear = s.Gear;
        }
        else
        {
            bool rpmGearChanged = (s.Rpms != _lastSanityRpm.Value || s.Gear != _lastSanityGear!.Value);

            if (rpmGearChanged)
            {
                // Data is updating — reset frozen tracking
                _lastSanityRpm           = s.Rpms;
                _lastSanityGear          = s.Gear;
                _rpmGearFrozenSince      = null;
                _partialDataWarningFired = false;
            }
            else if (s.SpeedKmh >= 0.5f)
            {
                // RPM/Gear are static but speed is changing → potential partial data
                _rpmGearFrozenSince ??= now;
                if (!_partialDataWarningFired && (now - _rpmGearFrozenSince.Value).TotalSeconds > 5.0)
                {
                    _partialDataWarningFired = true;
                    Append(BehaviorLogs, "WARN", "Partial data — RPM/Gear have not changed for > 5 s while speed is varying.");
                    AppLogger.Instance.Warn("AC sanity: partial data (RPM/Gear frozen for > 5 s while speed changes).");
                }
            }
        }
    }

    /// <summary>Resets all AC sanity-check state (called on Stop or source switch).</summary>
    private void ResetAcSanityState()
    {
        _speedZeroSince          = null;
        _stalledWarningFired     = false;
        _lastSanityRpm           = null;
        _lastSanityGear          = null;
        _rpmGearFrozenSince      = null;
        _partialDataWarningFired = false;
    }
}
