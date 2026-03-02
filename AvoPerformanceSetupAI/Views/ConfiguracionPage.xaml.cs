using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Storage.Pickers;
using AvoPerformanceSetupAI.Services;
using AvoPerformanceSetupAI.Services.Agent;
using System.Text;
using System.Net.WebSockets;
using System.Threading;

namespace AvoPerformanceSetupAI.Views;

public sealed partial class ConfiguracionPage : Page
{
    /// <summary>
    /// Must match <c>StepFrequency</c> on <see cref="UiScaleSlider"/> in the XAML.
    /// </summary>
    private const double UiScaleStep = 0.05;
    public ConfiguracionPage()
    {
        this.InitializeComponent();

        // Show current folders (if already set)
        FolderPathBox.Text = SetupSettings.Instance.RootFolder;
        OutputFolderPathBox.Text = SetupSettings.Instance.OutputFolder;

        // Keep the textboxes in sync if the settings change from elsewhere
        SetupSettings.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SetupSettings.RootFolder))
                FolderPathBox.Text = SetupSettings.Instance.RootFolder;
            else if (e.PropertyName == nameof(SetupSettings.OutputFolder))
                OutputFolderPathBox.Text = SetupSettings.Instance.OutputFolder;
        };

        // Initialise the UI scale slider from persisted setting
        UiScaleSlider.Value = SetupSettings.Instance.UiScale;
        UiScaleValueText.Text = $"{SetupSettings.Instance.UiScale:F2}×";

        // Initialise branding controls from persisted settings
        WatermarkEnabledToggle.IsOn = SetupSettings.Instance.BrandWatermarkEnabled;
        WatermarkOpacitySlider.Value = SetupSettings.Instance.BrandWatermarkOpacity;
        WatermarkOpacityValueText.Text = $"{SetupSettings.Instance.BrandWatermarkOpacity * 100:F0}%";
        SplashScreenToggle.IsOn = SetupSettings.Instance.ShowSplashScreen;
        RaceViewToggle.IsOn     = SetupSettings.Instance.RaceViewEnabled;

        // Remote Agent fields
        RemoteModeToggle.IsOn    = SetupSettings.Instance.Mode == AppMode.Remote;
        RemoteHostBox.Text       = SetupSettings.Instance.RemoteHost;
        RemotePortBox.Value      = SetupSettings.Instance.RemotePort;
        RemoteTokenBox.Password  = SetupSettings.Instance.RemoteToken;
        UpdateAgentBaseUrlDisplay();
        UpdateTokenWarning();
    }

    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.SuggestedStartLocation = PickerLocationId.Desktop;
        picker.FileTypeFilter.Add("*");

        // WinUI 3 requires initializing the picker with the window handle
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(
            (Application.Current as App)!.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            SetupSettings.Instance.RootFolder = folder.Path;
            AppLogger.Instance.Info($"Carpeta de setups configurada: {folder.Path}");
        }
        else
        {
            AppLogger.Instance.Info("Selección de carpeta cancelada por el usuario.");
        }
    }

    private async void BrowseOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.SuggestedStartLocation = PickerLocationId.Desktop;
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(
            (Application.Current as App)!.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            SetupSettings.Instance.OutputFolder = folder.Path;
            AppLogger.Instance.Info($"Carpeta de destino configurada: {folder.Path}");
        }
        else
        {
            AppLogger.Instance.Info("Selección de carpeta de destino cancelada por el usuario.");
        }
    }

    private void UiScaleSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // Round to the nearest step to avoid floating-point noise
        double v = Math.Round(e.NewValue / UiScaleStep) * UiScaleStep;
        SetupSettings.Instance.UiScale = v;
        if (UiScaleValueText is not null)
            UiScaleValueText.Text = $"{v:F2}×";
    }

    private void WatermarkToggle_Toggled(object sender, RoutedEventArgs e)
    {
        SetupSettings.Instance.BrandWatermarkEnabled = WatermarkEnabledToggle.IsOn;
    }

    private void SplashScreenToggle_Toggled(object sender, RoutedEventArgs e)
    {
        SetupSettings.Instance.ShowSplashScreen = SplashScreenToggle.IsOn;
    }

    private void RaceViewToggle_Toggled(object sender, RoutedEventArgs e)
    {
        SetupSettings.Instance.RaceViewEnabled = RaceViewToggle.IsOn;
    }

    // ── Remote Agent ──────────────────────────────────────────────────────────

    private void RemoteModeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        SetupSettings.Instance.Mode = RemoteModeToggle.IsOn ? AppMode.Remote : AppMode.Local;
        UpdateTokenWarning();
    }

    private void RemoteHostBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SetupSettings.Instance.RemoteHost = RemoteHostBox.Text;
        UpdateAgentBaseUrlDisplay();
    }

    private void RemotePortBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (!double.IsNaN(e.NewValue))
        {
            SetupSettings.Instance.RemotePort = (int)e.NewValue;
            UpdateAgentBaseUrlDisplay();
        }
    }

    private void RemoteTokenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        SetupSettings.Instance.RemoteToken = RemoteTokenBox.Password;
        UpdateTokenWarning();
    }

    private async void TestAgent_Click(object sender, RoutedEventArgs e)
    {
        var s   = SetupSettings.Instance;
        var url = s.AgentBaseUrl;

        AppLogger.Instance.Info($"Connecting to Agent: {url}");
        AgentTestResultText.Text = $"Probando {url}…";
        AgentTestResultText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Microsoft.UI.ColorHelper.FromArgb(255, 138, 171, 171));

        using var client = new AgentApiClient(s.RemoteHost, s.RemotePort, s.RemoteToken);

        // ── Ping ──────────────────────────────────────────────────────────────
        bool pingOk = await client.PingAsync();
        AppLogger.Instance.Info(
            $"Ping {url}/api/ping → {(pingOk ? "OK" : "FAIL")}");

        // ── Admin state ───────────────────────────────────────────────────────
        bool stateOk;
        string stateDetail;
        try
        {
            var state = await client.GetAdminStateAsync();
            stateOk    = state is not null;
            stateDetail = stateOk
                ? $"OK (AC={state!.AcRunning}, Car={state.ActiveCarId})"
                : "FAIL (null response)";
        }
        catch (Exception ex)
        {
            stateOk     = false;
            stateDetail = $"FAIL: {ex.Message}";
        }
        AppLogger.Instance.Info($"State {url}/api/admin/state → {stateDetail}");

        // ── WebSocket ─────────────────────────────────────────────────────────
        bool   wsOk = false;
        string wsResult;
        try
        {
            using var wsCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var ws    = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(s.AgentWsUrl), wsCts.Token);
            wsOk     = true;
            wsResult = "OK";
            try
            {
                await ws.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "Test",
                    CancellationToken.None);
            }
            catch { /* ignore graceful-close errors */ }
        }
        catch (Exception ex)
        {
            wsResult = ex.Message;
        }
        AppLogger.Instance.Info($"WS {s.AgentWsUrl} → {(wsOk ? "OK" : $"FAIL: {wsResult}")}");

        // ── Display result ────────────────────────────────────────────────────
        bool allOk = pingOk && stateOk && wsOk;
        var sb = new StringBuilder();
        sb.AppendLine($"{(pingOk  ? "✔" : "✗")} Ping {url}/api/ping → {(pingOk ? "OK" : "FAIL")}");
        sb.AppendLine($"{(stateOk ? "✔" : "✗")} State {url}/api/admin/state → {stateDetail}");
        sb.Append(    $"{(wsOk   ? "✔" : "✗")} WS {s.AgentWsUrl} → {(wsOk ? "OK" : $"FAIL: {wsResult}")}");

        AgentTestResultText.Text = sb.ToString();
        AgentTestResultText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(allOk
            ? Microsoft.UI.ColorHelper.FromArgb(255,   0, 212, 180)
            : Microsoft.UI.ColorHelper.FromArgb(255, 255,  80,  80));
    }

    private void UpdateTokenWarning()
    {
        var showWarning = SetupSettings.Instance.Mode == AppMode.Remote &&
                          string.IsNullOrWhiteSpace(SetupSettings.Instance.RemoteToken);
        TokenWarningBorder.Visibility = showWarning ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateAgentBaseUrlDisplay()
    {
        AgentBaseUrlDisplayBox.Text  = SetupSettings.Instance.AgentBaseUrl;
        AgentStateUrlDisplayBox.Text = $"{SetupSettings.Instance.AgentBaseUrl}/api/admin/state";
    }

    private void WatermarkOpacity_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        const double step = 0.01;
        double v = Math.Round(e.NewValue / step) * step;
        SetupSettings.Instance.BrandWatermarkOpacity = v;
        if (WatermarkOpacityValueText is not null)
            WatermarkOpacityValueText.Text = $"{v * 100:F0}%";
    }
}
