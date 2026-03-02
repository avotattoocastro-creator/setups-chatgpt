using System.Runtime.InteropServices;

namespace AvoPerformanceSetupAI.Telemetry;

// ─── AC session status ────────────────────────────────────────────────────────

/// <summary>Mirrors the AC_STATUS enum from the Assetto Corsa shared memory SDK.</summary>
public enum AcStatus
{
    Off    = 0,
    Replay = 1,
    Live   = 2,
    Pause  = 3,
}

// ─── Physics page  (acpmf_physics) ───────────────────────────────────────────
//
// Only the fields used by AcTelemetryReader are declared. Every [FieldOffset(n)]
// value was derived from the official Kunos shared-memory SDK header
// (SharedFileOut.h / AC_SHARED_MEMORY SDK) which uses Pack=4 throughout.
// Array element stride: each float/int = 4 bytes.
//   Array start offset + (element index × 4) = element offset.

/// <summary>
/// Blittable projection of <c>SPageFilePhysics</c>.
/// Fields are at their exact byte offsets inside the 800-byte shared-memory page.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
internal struct AcPhysicsData
{
    // ── Scalar driver inputs ──────────────────────────────────────────────────
    [FieldOffset(  4)] public float Gas;        // throttle 0..1
    [FieldOffset(  8)] public float Brake;      // brake    0..1
    [FieldOffset( 12)] public float Fuel;       // litres remaining
    [FieldOffset( 16)] public int   Gear;       // 0=R 1=N 2=1st …
    [FieldOffset( 20)] public int   Rpms;
    [FieldOffset( 24)] public float SteerAngle; // rad (positive = left)
    [FieldOffset( 28)] public float SpeedKmh;
    [FieldOffset(364)] public float Clutch;

    // ── accG[3]  (offset 44) ─────────────────────────────────────────────────
    [FieldOffset( 44)] public float AccGLateral;       // accG[0]
    [FieldOffset( 48)] public float AccGVertical;      // accG[1]
    [FieldOffset( 52)] public float AccGLongitudinal;  // accG[2]

    // ── wheelSlip[4]  (offset 56) ────────────────────────────────────────────
    [FieldOffset( 56)] public float WheelSlipFL;
    [FieldOffset( 60)] public float WheelSlipFR;
    [FieldOffset( 64)] public float WheelSlipRL;
    [FieldOffset( 68)] public float WheelSlipRR;

    // ── wheelsPressure[4]  (offset 88) ───────────────────────────────────────
    [FieldOffset( 88)] public float TyrePressureFL;
    [FieldOffset( 92)] public float TyrePressureFR;
    [FieldOffset( 96)] public float TyrePressureRL;
    [FieldOffset(100)] public float TyrePressureRR;

    // ── tyreCoreTemperature[4]  (offset 152) ─────────────────────────────────
    [FieldOffset(152)] public float TyreCoreTempFL;
    [FieldOffset(156)] public float TyreCoreTempFR;
    [FieldOffset(160)] public float TyreCoreTempRL;
    [FieldOffset(164)] public float TyreCoreTempRR;

    // ── localAngularVel[3]  (offset 296)  →  yaw = index 1  ─────────────────
    [FieldOffset(300)] public float YawRate;   // localAngularVel[1]

    // ── brakeTemp[4]  (offset 348) ───────────────────────────────────────────
    [FieldOffset(348)] public float BrakeTempFL;
    [FieldOffset(352)] public float BrakeTempFR;
    [FieldOffset(356)] public float BrakeTempRL;
    [FieldOffset(360)] public float BrakeTempRR;

    // ── slipAngle[4]  (offset 656) ───────────────────────────────────────────
    [FieldOffset(656)] public float SlipAngleFL;
    [FieldOffset(660)] public float SlipAngleFR;
    [FieldOffset(664)] public float SlipAngleRL;
    [FieldOffset(668)] public float SlipAngleRR;

    // ── tyreTemp[4] — surface temperature  (offset 696) ─────────────────────
    [FieldOffset(696)] public float TyreSurfaceTempFL;
    [FieldOffset(700)] public float TyreSurfaceTempFR;
    [FieldOffset(704)] public float TyreSurfaceTempRL;
    [FieldOffset(708)] public float TyreSurfaceTempRR;

    // ── brakePressure[4]  (offset 716) ───────────────────────────────────────
    [FieldOffset(716)] public float BrakePressureFL;
    [FieldOffset(720)] public float BrakePressureFR;
    [FieldOffset(724)] public float BrakePressureRL;
    [FieldOffset(728)] public float BrakePressureRR;
}

// ─── Graphics page  (acpmf_graphics) ─────────────────────────────────────────
//
// String fields (currentTime, lastTime, bestTime, split) are each wchar_t[15] = 30 bytes.
// tyreCompound[4][34] = 4 × 68 bytes = 272 bytes (offset 176..447).
// replayTimeMultiplier is at offset 448; normalizedCarPosition at 452.

/// <summary>
/// Blittable projection of <c>SPageFileGraphic</c>.
/// Fields are at their exact byte offsets inside the shared-memory page.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
internal struct AcGraphicsData
{
    [FieldOffset(  4)] public int   Status;              // AcStatus enum value
    [FieldOffset(132)] public int   CompletedLaps;
    [FieldOffset(140)] public int   ICurrentTimeMs;      // current lap time, ms
    [FieldOffset(144)] public int   ILastTimeMs;         // previous lap time, ms
    [FieldOffset(148)] public int   IBestTimeMs;         // best lap time, ms
    [FieldOffset(452)] public float NormalizedCarPos;    // 0..1 spline position
}

// ─── Statics page  (acpmf_static) ────────────────────────────────────────────
//
// smVersion  [wchar_t[15]] offset   0 (30 bytes)
// acVersion  [wchar_t[15]] offset  30 (30 bytes)
// numberOfSessions        offset  60
// numCars                 offset  64
// carModel   [wchar_t[33]] offset  68 (66 bytes)
// track      [wchar_t[33]] offset 134 (66 bytes)
// playerName [wchar_t[33]] offset 200 (66 bytes)
// playerSurname           offset 266 (66 bytes)
// playerNick              offset 332 (66 bytes)
// sectorCount             offset 398
// maxTorque               offset 402
// maxPower                offset 406
// maxRpm                  offset 410
// maxFuel                 offset 414

/// <summary>
/// Blittable projection of <c>SPageFileStatic</c>.
/// Fields are at their exact byte offsets inside the shared-memory page.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
internal struct AcStaticsData
{
    [FieldOffset(410)] public int   MaxRpm;
    [FieldOffset(414)] public float MaxFuel;
}
