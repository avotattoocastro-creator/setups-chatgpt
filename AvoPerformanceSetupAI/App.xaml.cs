using Microsoft.UI.Xaml;
using AvoPerformanceSetupAI.Services;

namespace AvoPerformanceSetupAI;

public partial class App : Application
{
    private Window? _window;

    /// <summary>Exposed so pages can obtain the HWND for native dialogs (e.g. FolderPicker).</summary>
    public Window? MainWindow => _window;

    public App()
    {
        this.InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // Apply the persisted UI scale before the window is created so all
        // StaticResource/Binding lookups pick up the correct value.
        this.Resources["UiScale"] = SetupSettings.Instance.UiScale;

        _window = new MainWindow();
        _window.Activate();
    }
}
