using System.Collections.ObjectModel;
using AvoPerformanceSetupAI.Models;

namespace AvoPerformanceSetupAI.Services;

public sealed class SharedAppState
{
    public static SharedAppState Instance { get; } = new();

    private SharedAppState() { }

    public SetupModel CurrentSetup { get; set; } = new();
    public SetupModel BaselineSetup { get; set; } = new();
    public ObservableCollection<SetupModel> Iterations { get; } = new();
    public TelemetryModel CurrentTelemetry { get; set; } = new();
}
