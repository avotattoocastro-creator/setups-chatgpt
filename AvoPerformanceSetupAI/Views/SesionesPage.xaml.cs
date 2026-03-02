using Microsoft.UI.Xaml.Controls;
using AvoPerformanceSetupAI.ViewModels;

namespace AvoPerformanceSetupAI.Views;

public sealed partial class SesionesPage : Page
{
    public SesionesPage()
    {
        this.InitializeComponent();
        DataContext = SessionsViewModel.Shared;
    }
}
