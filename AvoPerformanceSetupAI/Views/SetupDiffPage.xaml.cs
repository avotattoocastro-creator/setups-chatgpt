using Microsoft.UI.Xaml.Controls;
using AvoPerformanceSetupAI.ViewModels;

namespace AvoPerformanceSetupAI.Views;

public sealed partial class SetupDiffPage : Page
{
    public SetupDiffViewModel ViewModel { get; } = SetupDiffViewModel.Shared;

    public SetupDiffPage()
    {
        this.InitializeComponent();
    }
}
