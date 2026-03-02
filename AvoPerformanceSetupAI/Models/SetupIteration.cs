using CommunityToolkit.Mvvm.ComponentModel;

namespace AvoPerformanceSetupAI.Models;

public partial class SetupIteration : ObservableObject
{
    [ObservableProperty] private string _setup = string.Empty;
    [ObservableProperty] private string _bestLap = string.Empty;
    [ObservableProperty] private int _iter;
    [ObservableProperty] private bool _exported;
    [ObservableProperty] private bool _isSelected;
}
