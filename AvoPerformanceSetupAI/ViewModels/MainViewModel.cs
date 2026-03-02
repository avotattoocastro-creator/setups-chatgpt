using CommunityToolkit.Mvvm.ComponentModel;

namespace AvoPerformanceSetupAI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _appTitle = "AvoPerformanceSetupAI";

    [ObservableProperty]
    private string _selectedLanguage = "ES";

    public SessionsViewModel Sessions => SessionsViewModel.Shared;
}
