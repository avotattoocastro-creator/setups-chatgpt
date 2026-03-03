using Microsoft.UI.Xaml.Controls;
using AvoPerformanceSetupAI.ViewModels;

namespace AvoPerformanceSetupAI.Views;

public sealed partial class InicioPage : Page
{
    public InicioPage()
    {
        InitializeComponent();
        DataContext = SessionsViewModel.Shared;
    }
}
