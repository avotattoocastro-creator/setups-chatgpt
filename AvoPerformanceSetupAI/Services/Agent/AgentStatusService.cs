using CommunityToolkit.Mvvm.ComponentModel;

namespace AvoPerformanceSetupAI.Services.Agent;

/// <summary>
/// Shared status snapshot for the remote Agent and its Assetto Corsa hooks.
/// Updated by SessionsViewModel agent polling so other UI surfaces can display state.
/// </summary>
public sealed partial class AgentStatusService : ObservableObject
{
    public static AgentStatusService Instance { get; } = new();

    [ObservableProperty] private bool   _agentConnected;
    [ObservableProperty] private bool   _acRunning;
    [ObservableProperty] private bool   _sharedMemoryConnected;
    [ObservableProperty] private string _summary = string.Empty;

    private AgentStatusService()
    {
        // Initialize summary based on default false/false/false → shows as 🔴.
        Summary = BuildSummary();
    }

    public void Update(bool agentConnected, bool acRunning, bool sharedMemoryConnected)
    {
        AgentConnected        = agentConnected;
        AcRunning             = acRunning;
        SharedMemoryConnected = sharedMemoryConnected;
        Summary = BuildSummary();
    }

    private string BuildSummary()
    {
        static string Circle(bool green, bool amber, string label)
        {
            var icon = green ? "🟢" : amber ? "🟠" : "🔴";
            return $"{icon} {label}";
        }

        var agent = Circle(AgentConnected, amber: false, "Agent");

        // AC: amber when Agent conectado pero AC no está corriendo.
        var acAmber = AgentConnected && !AcRunning;
        var ac      = Circle(AcRunning, acAmber, "AC");

        // SharedMem: amber cuando AC está corriendo pero no hay shared memory.
        var smAmber = AcRunning && !SharedMemoryConnected;
        var sm      = Circle(SharedMemoryConnected, smAmber, "SharedMem");

        return $"{agent}  |  {ac}  |  {sm}";
    }
}
