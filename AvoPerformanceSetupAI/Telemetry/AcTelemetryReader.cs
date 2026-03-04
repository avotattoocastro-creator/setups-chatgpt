using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Threading;

namespace AvoPerformanceSetupAI.Telemetry;

/// <summary>
/// Connects to the three Assetto Corsa shared-memory pages
/// (<c>acpmf_physics</c>, <c>acpmf_graphics</c>, <c>acpmf_static</c>),
/// runs a 250 Hz polling loop on a dedicated background thread, and feeds
/// every sample into a <see cref="TelemetryRingBuffer"/>.
/// </summary>
/// <remarks>
/// Call <see cref="TryConnect"/> once; it opens the memory-mapped files and
/// starts the background thread.  Call <see cref="Disconnect"/> to stop.
/// Both methods are idempotent and thread-safe.
/// </remarks>
public sealed class AcTelemetryReader : IDisposable
{
    // ── Shared-memory file names used by Assetto Corsa ────────────────────────

    private const string PhysicsMapName  = "Local\\acpmf_physics";
    private const string GraphicsMapName = "Local\\acpmf_graphics";
    private const string StaticsMapName  = "Local\\acpmf_static";

    // ── Polling timing constants ──────────────────────────────────────────────

    private static readonly TimeSpan PollInterval         = TimeSpan.FromMilliseconds(4);
    private const           int      SpinThresholdMs      = 2; // switch to spin-wait within this many ms of deadline
    private const           int      SleepSafetyMarginMs  = 1; // sleep until (remaining - this) ms before deadline

    // ── Internal state ────────────────────────────────────────────────────────

    private MemoryMappedFile?         _mmfPhysics;
    private MemoryMappedFile?         _mmfGraphics;
    private MemoryMappedFile?         _mmfStatics;
    private MemoryMappedViewAccessor? _viewPhysics;
    private MemoryMappedViewAccessor? _viewGraphics;
    private MemoryMappedViewAccessor? _viewStatics;

    private Thread?                   _pollThread;
    private CancellationTokenSource?  _cts;

    private readonly object           _stateLock = new();

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Ring buffer that receives every polled sample. Capacity: 30 000 (≈ 120 s at 250 Hz).</summary>
    public TelemetryRingBuffer Buffer { get; } = new(capacity: 30_000);

    /// <summary>
    /// <see langword="true"/> after a successful <see cref="TryConnect"/>;
    /// <see langword="false"/> after <see cref="Disconnect"/> or if AC is not running.
    /// </summary>
    public bool IsConnected { get; private set; }

    /// <summary>
    /// Raised on the poll thread when AC closes unexpectedly (shared memory becomes
    /// unavailable mid-session).  <em>Not</em> raised when <see cref="Disconnect"/>
    /// is called intentionally.
    /// </summary>
    public event Action? Disconnected;

    /// <summary>
    /// Maximum RPM read from the statics page on connect; 0 when not connected.
    /// </summary>
    public int MaxRpm { get; private set; }

    /// <summary>
    /// Maximum fuel capacity (litres) read from the statics page on connect; 0 when not connected.
    /// </summary>
    public float MaxFuel { get; private set; }

    /// <summary>
    /// Attempts to open the three AC shared-memory pages and start the 250 Hz
    /// poll loop.  Returns <see langword="true"/> on success.  If AC is not
    /// running the call returns <see langword="false"/> without throwing.
    /// </summary>
    public bool TryConnect()
    {
        lock (_stateLock)
        {
            if (IsConnected) return true;

            try
            {
                _mmfPhysics  = MemoryMappedFile.OpenExisting(PhysicsMapName,
                                   MemoryMappedFileRights.Read);
                _mmfGraphics = MemoryMappedFile.OpenExisting(GraphicsMapName,
                                   MemoryMappedFileRights.Read);
                _mmfStatics  = MemoryMappedFile.OpenExisting(StaticsMapName,
                                   MemoryMappedFileRights.Read);

                _viewPhysics  = _mmfPhysics .CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                _viewGraphics = _mmfGraphics.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                _viewStatics  = _mmfStatics .CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

                // Read static info once and cache for embedding in every sample
                var statics = ReadStruct<AcStaticsData>(_viewStatics);
                MaxRpm  = statics.MaxRpm > 0 ? statics.MaxRpm : 9000;
                MaxFuel = statics.MaxFuel;

                IsConnected = true;
                StartPollThread();
                return true;
            }
            catch (Exception)
            {
                // AC is not running or shared memory is unavailable
                CloseHandles();
                return false;
            }
        }
    }

    /// <summary>Stops the poll loop and releases all shared-memory handles.</summary>
    public void Disconnect()
    {
        lock (_stateLock)
        {
            StopPollThread();
            CloseHandles();
            IsConnected = false;
            MaxRpm      = 0;
            MaxFuel     = 0.0f;
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose() => Disconnect();

    // ── Private helpers ───────────────────────────────────────────────────────

    private void StartPollThread()
    {
        _cts        = new CancellationTokenSource();
        _pollThread = new Thread(PollLoop)
        {
            IsBackground = true,
            Name         = "AcTelemetryPoll-250Hz",
            Priority     = ThreadPriority.AboveNormal,
        };
        _pollThread.Start(_cts.Token);
    }

    private void StopPollThread()
    {
        _cts?.Cancel();
        _pollThread?.Join(TimeSpan.FromSeconds(2));
        _cts?.Dispose();
        _cts        = null;
        _pollThread = null;
    }

    private void CloseHandles()
    {
        _viewPhysics?.Dispose();  _viewPhysics  = null;
        _viewGraphics?.Dispose(); _viewGraphics = null;
        _viewStatics?.Dispose();  _viewStatics  = null;
        _mmfPhysics?.Dispose();   _mmfPhysics   = null;
        _mmfGraphics?.Dispose();  _mmfGraphics  = null;
        _mmfStatics?.Dispose();   _mmfStatics   = null;
    }

    // ── 250 Hz poll loop ──────────────────────────────────────────────────────

    private void PollLoop(object? param)
    {
        var ct       = param is CancellationToken token ? token : CancellationToken.None;
        var sw       = Stopwatch.StartNew();
        var spinWait = new SpinWait();

        while (!ct.IsCancellationRequested)
        {
            // ── Precision 4 ms timer: sleep for most of the interval, spin the rest ──
            var remaining = PollInterval - sw.Elapsed;
            if (remaining > TimeSpan.FromMilliseconds(SpinThresholdMs))
                Thread.Sleep((int)(remaining.TotalMilliseconds - SleepSafetyMarginMs));

            while (sw.Elapsed < PollInterval)
                spinWait.SpinOnce();

            sw.Restart();

            try
            {
                ReadAndPush();
            }
            catch (Exception)
            {
                // Shared memory became unavailable (AC closed mid-session); stop silently
                break;
            }
        }

        // If the loop exited because AC closed (not because Disconnect() was called),
        // notify observers so they can start a reconnect cycle.
        if (!ct.IsCancellationRequested)
            Disconnected?.Invoke();
    }

    private void ReadAndPush()
    {
        // These accessors are read-only references to private fields; no null check
        // needed here because PollLoop only runs while IsConnected == true.
        var physics  = ReadStruct<AcPhysicsData> (_viewPhysics!);
        var graphics = ReadStruct<AcGraphicsData>(_viewGraphics!);

        var sample = new TelemetrySample
        {
            Timestamp = DateTime.UtcNow,

            Throttle   = physics.Gas,
            Brake      = physics.Brake,
            Clutch     = physics.Clutch,
            SteerAngle = physics.SteerAngle,
            Gear       = physics.Gear,
            Rpms       = physics.Rpms,
            SpeedKmh   = physics.SpeedKmh,
            Fuel       = physics.Fuel,

            AccGLateral      = physics.AccGLateral,
            AccGVertical     = physics.AccGVertical,
            AccGLongitudinal = physics.AccGLongitudinal,
            YawRate          = physics.YawRate,

            WheelSlipFL = physics.WheelSlipFL,
            WheelSlipFR = physics.WheelSlipFR,
            WheelSlipRL = physics.WheelSlipRL,
            WheelSlipRR = physics.WheelSlipRR,

            TyreTempFL = physics.TyreCoreTempFL,
            TyreTempFR = physics.TyreCoreTempFR,
            TyreTempRL = physics.TyreCoreTempRL,
            TyreTempRR = physics.TyreCoreTempRR,

            TyrePressureFL = physics.TyrePressureFL,
            TyrePressureFR = physics.TyrePressureFR,
            TyrePressureRL = physics.TyrePressureRL,
            TyrePressureRR = physics.TyrePressureRR,

            BrakePressureFL = physics.BrakePressureFL,
            BrakePressureFR = physics.BrakePressureFR,
            BrakePressureRL = physics.BrakePressureRL,
            BrakePressureRR = physics.BrakePressureRR,

            SlipAngleFL = physics.SlipAngleFL,
            SlipAngleFR = physics.SlipAngleFR,
            SlipAngleRL = physics.SlipAngleRL,
            SlipAngleRR = physics.SlipAngleRR,

            NormalizedLapPos = graphics.NormalizedCarPos,
            LapTimeMs        = graphics.ICurrentTimeMs,
            LastLapMs        = graphics.ILastTimeMs,
            BestLapMs        = graphics.IBestTimeMs,
            CompletedLaps    = graphics.CompletedLaps,
            AcStatus         = graphics.Status,

            MaxRpm  = MaxRpm,
            MaxFuel = MaxFuel,
        };

        Buffer.Push(in sample);
    }

    // ── Low-level blittable struct reader ─────────────────────────────────────

    private static unsafe T ReadStruct<T>(MemoryMappedViewAccessor accessor) where T : unmanaged
    {
        byte* ptr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        try
        {
            return Unsafe.ReadUnaligned<T>(ptr);
        }
        finally
        {
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }
}
