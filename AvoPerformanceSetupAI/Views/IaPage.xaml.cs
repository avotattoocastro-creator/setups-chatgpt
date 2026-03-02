using Microsoft.UI.Xaml.Controls;
using AvoPerformanceSetupAI.ViewModels;

namespace AvoPerformanceSetupAI.Views;

public sealed partial class IaPage : Page
{
    public SessionsViewModel ViewModel { get; } = SessionsViewModel.Shared;

    public IaPage()
    {
        this.InitializeComponent();
    }
}
