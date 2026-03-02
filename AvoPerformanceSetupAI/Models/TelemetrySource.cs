namespace AvoPerformanceSetupAI.Models;

/// <summary>Identifies the active telemetry data source.</summary>
public enum TelemetrySource
{
    /// <summary>Built-in simulation generator (no external connection required).</summary>
    Simulation,

    /// <summary>Assetto Corsa shared-memory interface (requires AC to be running).</summary>
    AssettoCorsa,
}
