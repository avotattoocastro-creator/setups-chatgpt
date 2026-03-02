using System;
using System.Collections.Generic;
using System.Linq;
using AvoPerformanceSetupAI.ML;
using AvoPerformanceSetupAI.Models;

namespace AvoPerformanceSetupAI.Telemetry;

/// <summary>Describes the current state of an A/B test run managed by <see cref="AbTestManager"/>.</summary>
public enum AbTestState
{
    /// <summary>No test is in progress.</summary>
    Idle,

    /// <summary>Collecting baseline corners (before setup change).</summary>
    CollectingBaseline,

    /// <summary>
    /// Baseline collection is complete; waiting for the user to apply the change
    /// and call <see cref="AbTestManager.MarkChangeApplied"/>.
    /// </summary>
    AwaitingChange,

    /// <summary>Collecting corners with the new setup applied.</summary>
    CollectingChanged,

    /// <summary>
    /// Comparison complete; the result is available via
    /// <see cref="AbTestManager.LatestResult"/>.
    /// </summary>
    Complete,
}

/// <summary>
/// Manages a single A/B test session for a setup <see cref="Proposal"/>.
/// Collects corner samples in two phases (baseline / changed), computes
/// <see cref="AbTestMetrics"/> for each phase, and produces an
/// <see cref="AbTestResult"/> with an <see cref="AbTestResult.Improved"/> flag
/// and a confidence score.
/// </summary>
/// <remarks>
/// <para><b>Typical workflow:</b></para>
/// <list type="number">
///   <item><see cref="StartTest"/> — choose a proposal to test.</item>
///   <item>Call <see cref="NotifyCornerCompleted"/> for each corner completed by
///     <see cref="CornerDetector"/>. The first matching corner sets the direction
///     and speed reference; subsequent compatible corners fill the baseline.</item>
///   <item>After <see cref="CornersRequired"/> baseline corners are collected, the
///     state moves to <see cref="AbTestState.AwaitingChange"/>. The user applies
///     the setup change manually.</item>
///   <item><see cref="MarkChangeApplied"/> — signals the app that the change is
///     live and collection can resume.</item>
///   <item>Continue calling <see cref="NotifyCornerCompleted"/>; once the same
///     number of compatible corners are collected with the new setup, the test
///     finalises and an <see cref="AbTestResult"/> is returned.</item>
///   <item><see cref="PromoteRuleWeight"/> — if the result shows a consistent
///     improvement, boost the proposal's weight so it is ranked higher in future
///     rule evaluations.</item>
/// </list>
/// <para>Thread-safety: this class is <b>not</b> thread-safe. Call from the UI
/// thread (or synchronise externally).</para>
/// </remarks>
public sealed class AbTestManager
{
    // ── Constants ─────────────────────────────────────────────────────────────

    /// <summary>Number of comparable corners required per phase (baseline and changed).</summary>
    public const int CornersRequired = 2;

    /// <summary>
    /// Tolerance used to match corners by speed proxy (PeakLateralG × 100).
    /// Equivalent to approximately ±15 km/h at mid-corner.
    /// </summary>
    public const float SpeedToleranceKmh = 15f;

    // ── State ─────────────────────────────────────────────────────────────────

    private AbTestState    _state           = AbTestState.Idle;
    private Proposal?      _targetProposal;
    private CornerDirection _targetDirection = CornerDirection.Unknown;
    private float           _targetSpeedProxy;

    private readonly List<CornerSummary> _baselineCorners = new();
    private readonly List<CornerSummary> _changedCorners  = new();
    private readonly List<AbTestResult>  _history         = new();

    /// <summary>
    /// Rule-weight overrides indexed by "Section/Parameter" key.
    /// Values start at 1.0 and are boosted by <see cref="PromoteRuleWeight"/>
    /// when a proposal consistently improves the car's behaviour.
    /// </summary>
    private readonly Dictionary<string, float> _ruleWeights =
        new(StringComparer.OrdinalIgnoreCase);

    // ── ML training context ───────────────────────────────────────────────────

    /// <summary>
    /// Optional <see cref="ImpactPredictor"/> to hot-swap after retraining.
    /// Set via <see cref="SetMlContext"/>.
    /// </summary>
    private ImpactPredictor? _predictor;

    /// <summary>
    /// Aggregate <see cref="FeatureFrame"/> captured when <see cref="StartTest"/>
    /// is called. Used to build the training sample on test completion.
    /// </summary>
    private FeatureFrame _baselineFrame;

    /// <summary>
    /// <see cref="DrivingScores"/> captured when <see cref="StartTest"/> is
    /// called. Used to populate score fields in the training sample.
    /// </summary>
    private DrivingScores? _baselineScores;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Current state of the A/B test session.</summary>
    public AbTestState State => _state;

    /// <summary>The proposal currently under test, or <see langword="null"/> when idle.</summary>
    public Proposal? TestedProposal => _targetProposal;

    /// <summary>
    /// Most recently completed result, or <see langword="null"/> when no test has
    /// finished yet.
    /// </summary>
    public AbTestResult? LatestResult => _history.Count > 0 ? _history[^1] : null;

    /// <summary>Complete history of A/B test results (oldest first).</summary>
    public IReadOnlyList<AbTestResult> History => _history;

    /// <summary>
    /// Read-only view of the rule-weight dictionary.
    /// Keys are "Section/Parameter" strings; values ≥ 1.0 represent boosted weights.
    /// </summary>
    public IReadOnlyDictionary<string, float> RuleWeights => _ruleWeights;

    /// <summary>Number of baseline corners collected so far (0 when idle).</summary>
    public int BaselineCount => _baselineCorners.Count;

    /// <summary>
    /// Number of post-change corners collected so far (0 before the changed phase
    /// begins).
    /// </summary>
    public int ChangedCount => _changedCorners.Count;

    /// <summary>
    /// Registers the ML predictor to use for hot-swapping after a background
    /// retrain. Call once during app startup.
    /// </summary>
    /// <param name="predictor">
    /// The <see cref="ImpactPredictor"/> instance shared with
    /// <see cref="UltraSetupAdvisor"/>. May be <see langword="null"/> to disable
    /// ML training.
    /// </param>
    public void SetMlContext(ImpactPredictor? predictor)
        => _predictor = predictor;

    // ── Test lifecycle ────────────────────────────────────────────────────────

    /// <summary>
    /// Begins a new A/B test for <paramref name="proposal"/>.
    /// The first corner passed to <see cref="NotifyCornerCompleted"/> will set the
    /// reference direction and speed; baseline collection then starts.
    /// </summary>
    /// <param name="proposal">The setup change to test.</param>
    /// <param name="baselineFrame">
    /// Aggregate <see cref="FeatureFrame"/> at the time the test begins.
    /// Used later to build a training sample when the test completes.
    /// </param>
    /// <param name="baselineScores">
    /// Current <see cref="DrivingScores"/> when the test starts.
    /// Used to populate score fields in the training sample.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="proposal"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A test is already in progress.</exception>
    public void StartTest(
        Proposal       proposal,
        in FeatureFrame baselineFrame  = default,
        DrivingScores?  baselineScores = null)
    {
        if (proposal is null) throw new ArgumentNullException(nameof(proposal));
        if (_state != AbTestState.Idle)
            throw new InvalidOperationException(
                $"Cannot start a test while state is {_state}. Call Reset() first.");

        _targetProposal  = proposal;
        _targetDirection  = CornerDirection.Unknown;
        _targetSpeedProxy = 0f;
        _baselineFrame    = baselineFrame;
        _baselineScores   = baselineScores;
        _baselineCorners.Clear();
        _changedCorners.Clear();
        _state = AbTestState.CollectingBaseline;
    }

    /// <summary>
    /// Cancels any in-progress test and returns the manager to
    /// <see cref="AbTestState.Idle"/>. Does not affect history or rule weights.
    /// </summary>
    public void Reset()
    {
        _state            = AbTestState.Idle;
        _targetProposal   = null;
        _targetDirection  = CornerDirection.Unknown;
        _targetSpeedProxy = 0f;
        _baselineCorners.Clear();
        _changedCorners.Clear();
    }

    /// <summary>
    /// Signals that the setup change has been applied and the manager should now
    /// collect "changed" corners. Only valid in
    /// <see cref="AbTestState.AwaitingChange"/>; silently ignored otherwise.
    /// </summary>
    public void MarkChangeApplied()
    {
        if (_state == AbTestState.AwaitingChange)
            _state = AbTestState.CollectingChanged;
    }

    /// <summary>
    /// Feeds a completed <see cref="CornerSummary"/> into the A/B test pipeline.
    /// <list type="bullet">
    ///   <item>During <see cref="AbTestState.CollectingBaseline"/> the first
    ///   eligible corner sets the reference direction and speed proxy; subsequent
    ///   compatible corners are accumulated until
    ///   <see cref="CornersRequired"/> is reached.</item>
    ///   <item>During <see cref="AbTestState.CollectingChanged"/> the same
    ///   comparability filter is applied.</item>
    ///   <item>When the changed phase reaches <see cref="CornersRequired"/> an
    ///   <see cref="AbTestResult"/> is produced and returned.</item>
    /// </list>
    /// </summary>
    /// <returns>
    /// The completed <see cref="AbTestResult"/> when the test finishes, or
    /// <see langword="null"/> when more corners are still needed.
    /// </returns>
    public AbTestResult? NotifyCornerCompleted(in CornerSummary corner)
    {
        switch (_state)
        {
            case AbTestState.CollectingBaseline:
                AcceptCorner(in corner, _baselineCorners);
                if (_baselineCorners.Count >= CornersRequired)
                    _state = AbTestState.AwaitingChange;
                return null;

            case AbTestState.CollectingChanged:
                AcceptCorner(in corner, _changedCorners);
                if (_changedCorners.Count >= CornersRequired)
                    return Finalize();
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Increases the rule weight for the "Section/Parameter" key of
    /// <paramref name="proposal"/> by <paramref name="weightBoost"/>, up to a
    /// maximum of 3.0. The boost is only applied when the test history contains at
    /// least one result for this proposal where
    /// <see cref="AbTestResult.Improved"/> is <see langword="true"/>—evidence is
    /// required before a weight can be promoted.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="proposal"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="weightBoost"/> is ≤ 0.</exception>
    public void PromoteRuleWeight(Proposal proposal, float weightBoost = 0.1f)
    {
        if (proposal    is null) throw new ArgumentNullException(nameof(proposal));
        if (weightBoost <= 0f)   throw new ArgumentOutOfRangeException(nameof(weightBoost));

        var key = ProposalKey(proposal);

        // Require at least one successful test for this proposal before boosting
        var hasEvidence = _history.Any(r =>
            r.Improved &&
            string.Equals(ProposalKey(r.TestedProposal), key,
                          StringComparison.OrdinalIgnoreCase));

        if (!hasEvidence) return;

        var current = _ruleWeights.TryGetValue(key, out var w) ? w : 1.0f;
        _ruleWeights[key] = Math.Min(current + weightBoost, 3.0f);
    }

    /// <summary>
    /// Returns the stored rule weight for <paramref name="proposal"/>.
    /// Returns 1.0 when the proposal has not been promoted.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="proposal"/> is <see langword="null"/>.</exception>
    public float GetRuleWeight(Proposal proposal)
    {
        if (proposal is null) throw new ArgumentNullException(nameof(proposal));
        return _ruleWeights.TryGetValue(ProposalKey(proposal), out var w) ? w : 1.0f;
    }

    // ── Comparability filter ──────────────────────────────────────────────────

    /// <summary>
    /// Adds <paramref name="corner"/> to <paramref name="bucket"/> when it is
    /// compatible with the reference direction and speed. The very first corner
    /// ever accepted (into the baseline bucket) sets the reference values.
    /// </summary>
    private void AcceptCorner(in CornerSummary corner, List<CornerSummary> bucket)
    {
        // First corner ever accepted: set the reference
        if (_targetDirection == CornerDirection.Unknown)
        {
            _targetDirection  = corner.Direction;
            _targetSpeedProxy = SpeedProxy(in corner);
            bucket.Add(corner);
            return;
        }

        // Subsequent corners must match direction and be within the speed tolerance
        if (corner.Direction == _targetDirection &&
            Math.Abs(SpeedProxy(in corner) - _targetSpeedProxy) <= SpeedToleranceKmh)
        {
            bucket.Add(corner);
        }
    }

    /// <summary>
    /// Speed proxy derived from <see cref="CornerSummary.PeakLateralG"/>.
    /// Multiplied by 100 so that the numeric range is comparable to km/h and
    /// <see cref="SpeedToleranceKmh"/> can be applied directly.
    /// </summary>
    private static float SpeedProxy(in CornerSummary cs) => cs.PeakLateralG * 100f;

    // ── Finalization ──────────────────────────────────────────────────────────

    private AbTestResult Finalize()
    {
        var baseline   = ComputeMetrics(_baselineCorners);
        var changed    = ComputeMetrics(_changedCorners);
        var deltas     = Subtract(changed, baseline);
        var improved   = IsImproved(in deltas);
        var confidence = ComputeConfidence(_baselineCorners, _changedCorners);
        var summary    = BuildSummary(in baseline, in changed, in deltas, improved, confidence);

        var result = new AbTestResult
        {
            TestedProposal  = _targetProposal!,
            BaselineMetrics = baseline,
            ChangedMetrics  = changed,
            Deltas          = deltas,
            Improved        = improved,
            SummaryText     = summary,
            Confidence      = confidence,
            Timestamp       = DateTime.UtcNow,
        };

        _history.Add(result);
        _state = AbTestState.Complete;

        // ── ML training pipeline ──────────────────────────────────────────────
        // Capture locals so the lambda doesn't close over mutable fields.
        var capturedFrame   = _baselineFrame;
        var capturedScores  = _baselineScores;
        var capturedPredictor = _predictor;

        ImpactModelTrainer.AppendSample(result, capturedScores, in capturedFrame);
        if (capturedPredictor != null)
            ImpactModelTrainer.RetrainIfReady(capturedPredictor);

        return result;
    }

    // ── Metrics computation ───────────────────────────────────────────────────

    private static AbTestMetrics ComputeMetrics(List<CornerSummary> corners)
    {
        if (corners.Count == 0) return new AbTestMetrics();

        float usMid = 0f, osExit = 0f, wsRear = 0f, brakeStab = 0f;
        float osEntry = 0f, oscillation = 0f;

        foreach (var cs in corners)
        {
            usMid       += cs.MidFrame.UndersteerMid;
            osExit      += cs.ExitFrame.OversteerExit;
            wsRear      += cs.TotalFrame.WheelspinRatioRear;
            brakeStab   += cs.EntryFrame.BrakeStabilityIndex;
            osEntry     += cs.EntryFrame.OversteerEntry;
            oscillation += cs.TotalFrame.SuspensionOscillationIndex;
        }

        var n          = (float)corners.Count;
        var avgOsEntry = osEntry    / n;
        var avgOscil   = oscillation / n;

        return new AbTestMetrics
        {
            UndersteerMidAvg       = usMid    / n,
            OversteerExitAvg       = osExit   / n,
            WheelspinRatioRearAvg  = wsRear   / n,
            BrakeStabilityAvg      = brakeStab / n,
            StabilityScore         = 1f - (avgOsEntry + avgOscil) / 2f,
            SampleCount            = corners.Count,
        };
    }

    /// <summary>Returns Changed − Baseline for each metric.</summary>
    private static AbTestMetrics Subtract(AbTestMetrics changed, AbTestMetrics baseline)
        => new()
        {
            UndersteerMidAvg       = changed.UndersteerMidAvg       - baseline.UndersteerMidAvg,
            OversteerExitAvg       = changed.OversteerExitAvg       - baseline.OversteerExitAvg,
            WheelspinRatioRearAvg  = changed.WheelspinRatioRearAvg  - baseline.WheelspinRatioRearAvg,
            BrakeStabilityAvg      = changed.BrakeStabilityAvg      - baseline.BrakeStabilityAvg,
            StabilityScore         = changed.StabilityScore         - baseline.StabilityScore,
            SampleCount            = changed.SampleCount,
        };

    /// <summary>
    /// Returns <see langword="true"/> when at least 3 of the 5 metrics moved in
    /// the beneficial direction.
    /// </summary>
    private static bool IsImproved(in AbTestMetrics deltas)
    {
        int benefit = 0;
        if (deltas.UndersteerMidAvg      < 0f) benefit++;   // less understeer  = better
        if (deltas.OversteerExitAvg      < 0f) benefit++;   // less oversteer   = better
        if (deltas.WheelspinRatioRearAvg < 0f) benefit++;   // less wheelspin   = better
        if (deltas.BrakeStabilityAvg     < 0f) benefit++;   // less instability = better
        if (deltas.StabilityScore        > 0f) benefit++;   // higher score     = better
        return benefit >= 3;
    }

    /// <summary>
    /// Confidence (0..1) based on total sample count and intra-phase variance.
    /// More corners and lower variance → higher confidence.
    /// </summary>
    private static float ComputeConfidence(
        List<CornerSummary> baseline,
        List<CornerSummary> changed)
    {
        // Count confidence: asymptotically approaches 1.0 as n grows
        // n=2 → 0.40; n=4 → 0.57; n=8 → 0.73; n=20 → 0.87
        var n               = baseline.Count + changed.Count;
        var countConfidence = 1f - 1f / (1f + n * 0.2f);

        // Variance penalty: high intra-phase spread reduces confidence
        var baseVar = StdDevUndersteerMid(baseline);
        var chanVar = StdDevUndersteerMid(changed);
        var varPenalty = (baseVar + chanVar) * 0.5f;

        var varianceConfidence = Math.Max(0f, 1f - varPenalty);

        return Math.Clamp(countConfidence * varianceConfidence, 0f, 1f);
    }

    /// <summary>
    /// Standard deviation of <see cref="FeatureFrame.UndersteerMid"/> across the
    /// supplied corners (0 when fewer than 2 corners).
    /// </summary>
    private static float StdDevUndersteerMid(List<CornerSummary> corners)
    {
        if (corners.Count < 2) return 0f;
        var vals = corners.Select(c => (double)c.MidFrame.UndersteerMid).ToArray();
        var mean = vals.Average();
        var variance = vals.Average(v => (v - mean) * (v - mean));
        return (float)Math.Sqrt(variance);
    }

    // ── Summary text ──────────────────────────────────────────────────────────

    private static string BuildSummary(
        in AbTestMetrics baseline,
        in AbTestMetrics changed,
        in AbTestMetrics deltas,
        bool   improved,
        float  confidence)
    {
        var verdict = improved ? "✓ MEJORA CONFIRMADA" : "✗ SIN MEJORA";
        return
            $"{verdict}  (confianza {confidence:P0})\n" +
            $"  Subviraje mid  {FormatDelta(deltas.UndersteerMidAvg,      invert: true )}" +
            $"  {baseline.UndersteerMidAvg:P0} → {changed.UndersteerMidAvg:P0}\n" +
            $"  Sobreviraje sal{FormatDelta(deltas.OversteerExitAvg,      invert: true )}" +
            $"  {baseline.OversteerExitAvg:P0} → {changed.OversteerExitAvg:P0}\n" +
            $"  Patinamiento tra{FormatDelta(deltas.WheelspinRatioRearAvg, invert: true )}" +
            $"  {baseline.WheelspinRatioRearAvg:P0} → {changed.WheelspinRatioRearAvg:P0}\n" +
            $"  Est. frenada   {FormatDelta(deltas.BrakeStabilityAvg,     invert: true )}" +
            $"  {baseline.BrakeStabilityAvg:P0} → {changed.BrakeStabilityAvg:P0}\n" +
            $"  Puntuación est.{FormatDelta(deltas.StabilityScore,        invert: false)}" +
            $"  {baseline.StabilityScore:P0} → {changed.StabilityScore:P0}";
    }

    /// <param name="delta">Signed difference (Changed − Baseline).</param>
    /// <param name="invert">
    /// <see langword="true"/> when a <em>negative</em> delta is beneficial (e.g.
    /// less understeer). <see langword="false"/> when a <em>positive</em> delta is
    /// beneficial (e.g. higher stability score).
    /// </param>
    private static string FormatDelta(float delta, bool invert)
    {
        var isBetter = invert ? delta < 0f : delta > 0f;
        var arrow    = isBetter ? "▼" : "▲";
        var sign     = delta >= 0f ? "+" : "";
        return $" [{arrow}{sign}{delta:P0}]";
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static string ProposalKey(Proposal p) =>
        $"{p.Section}/{p.Parameter}";
}
