using Microsoft.UI.Xaml.Controls;
using AvoPerformanceSetupAI.ViewModels;

namespace AvoPerformanceSetupAI.Views;

public sealed partial class ControlPage : Page
{
    public ControlPage()
    {
        this.InitializeComponent();
        DataContext = SessionsViewModel.Shared;
    }
}
