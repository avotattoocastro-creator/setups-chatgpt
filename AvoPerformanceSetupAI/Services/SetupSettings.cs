using System;
using System.IO;
using System.Text.Json;
using AvoPerformanceSetupAI.Services.Agent;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Windows.Storage;

namespace AvoPerformanceSetupAI.Services;

/// <summary>Application connection mode — drives which library/saver implementation is used.</summary>
public enum AppMode { Local, Remote }

/// <summary>
/// Application-wide settings singleton.
/// Holds the root folder path for setup files so all pages can observe it.
/// Values are persisted to <see cref="ApplicationData.Current.LocalSettings"/> so they
/// survive application restarts.
/// </summary>
public sealed partial class SetupSettings : ObservableObject
{
    private const string KeyRootFolder            = "RootFolder";
    private const string KeyOutputFolder          = "OutputFolder";
    private const string KeyUiScale               = "UiScale";
    private const string KeyBrandWatermarkEnabled = "BrandWatermarkEnabled";
    private const string KeyBrandWatermarkOpacity = "BrandWatermarkOpacity";
    private const string KeyShowSplashScreen      = "ShowSplashScreen";
    private const string KeyRaceViewEnabled       = "RaceViewEnabled";
    private const string KeyAppMode               = "AppMode";
    private const string KeyRemoteHost            = "RemoteHost";
    private const string KeyRemotePort            = "RemotePort";
    private const string KeyRemoteToken           = "RemoteToken";

    private readonly string _fallbackPath;

    private readonly bool _hasPackageIdentity;

    public static SetupSettings Instance { get; } = new SetupSettings();

    [ObservableProperty]
    private string _rootFolder = string.Empty;

    [ObservableProperty]
    private string _outputFolder = string.Empty;

    [ObservableProperty]
    private double _uiScale = 1.0;

    [ObservableProperty]
    private bool _brandWatermarkEnabled = true;

    [ObservableProperty]
    private double _brandWatermarkOpacity = 0.06;

    [ObservableProperty]
    private bool _showSplashScreen = true;

    [ObservableProperty]
    private bool _raceViewEnabled = false;

    [ObservableProperty]
    private AppMode _mode = AppMode.Local;

    [ObservableProperty]
    private string _remoteHost = "localhost";

    [ObservableProperty]
    private int _remotePort = AgentEndpointResolver.DefaultHttpPort;

    [ObservableProperty]
    private string _remoteToken = string.Empty;

    // ── Computed helpers ──────────────────────────────────────────────────────

    /// <summary>Base URL for the remote Agent REST API, e.g. "http://192.168.1.10:5000".</summary>
    public string AgentBaseUrl => $"http://{RemoteHost}:{RemotePort}";

    /// <summary>WebSocket URL for the remote Agent, including the auth token.</summary>
    public string AgentWsUrl   => $"ws://{RemoteHost}:{RemotePort}/ws?token={RemoteToken}";

    /// <summary>WebSocket URL for the Agent live-log stream (last 300 lines).</summary>
    public string AgentLogsWsUrl => $"ws://{RemoteHost}:{RemotePort}/ws/logs?token={RemoteToken}&tail=300";

    private SetupSettings()
    {
        _fallbackPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AvoPerformanceSetupAI",
            "setupsettings.json");

        try
        {
            var local = ApplicationData.Current.LocalSettings;
            _hasPackageIdentity = true;

            // Assign backing fields directly to avoid writing the loaded values back to
            // LocalSettings (which would happen if we used the property setters).
            _rootFolder   = local.Values[KeyRootFolder]   as string ?? string.Empty;
            _outputFolder = local.Values[KeyOutputFolder] as string ?? string.Empty;
            _uiScale      = local.Values[KeyUiScale] is double d ? d : 1.0;
            _brandWatermarkEnabled = local.Values[KeyBrandWatermarkEnabled] is bool bwm ? bwm : true;
            _brandWatermarkOpacity = local.Values[KeyBrandWatermarkOpacity] is double bwo ? bwo : 0.06;
            _showSplashScreen      = local.Values[KeyShowSplashScreen]      is bool sss ? sss : true;
            _raceViewEnabled       = local.Values[KeyRaceViewEnabled]       is bool rve ? rve : false;
            _mode                  = local.Values[KeyAppMode] is string ms && Enum.TryParse<AppMode>(ms, out var pm) ? pm : AppMode.Local;
            _remoteHost            = local.Values[KeyRemoteHost]  as string ?? "localhost";
            _remotePort            = local.Values[KeyRemotePort]  is int rp  ? rp  : AgentEndpointResolver.DefaultHttpPort;
            _remoteToken           = local.Values[KeyRemoteToken] as string ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            // Happens when running unpackaged (no package identity). Keep defaults and disable persistence.
            _hasPackageIdentity = false;
            _rootFolder  = string.Empty;
            _outputFolder = string.Empty;
            _uiScale     = 1.0;
            _brandWatermarkEnabled = true;
            _brandWatermarkOpacity = 0.06;
            _showSplashScreen      = true;
            _raceViewEnabled       = false;
            _mode                  = AppMode.Local;
            _remoteHost            = "localhost";
            _remotePort            = AgentEndpointResolver.DefaultHttpPort;
            _remoteToken           = string.Empty;
        }

        // Load fallback file (works in unpackaged/dev builds). Overrides defaults above.
        LoadFromFileIfExists();

        // Notify listeners (e.g., SessionsViewModel) so they can react to preloaded values on startup.
        NotifyLoaded();
    }

    partial void OnRootFolderChanged(string value)
    {
        if (_hasPackageIdentity)
            ApplicationData.Current.LocalSettings.Values[KeyRootFolder] = value;

        SaveToDisk();
    }

    partial void OnOutputFolderChanged(string value)
    {
        if (_hasPackageIdentity)
            ApplicationData.Current.LocalSettings.Values[KeyOutputFolder] = value;

        SaveToDisk();
    }

    partial void OnUiScaleChanged(double value)
    {
        if (_hasPackageIdentity)
            ApplicationData.Current.LocalSettings.Values[KeyUiScale] = value;

        SaveToDisk();

        // Push the new value into the live resource dictionary so converters
        // pick it up on the next page navigation / resource lookup.
        // If Application.Current is null (e.g. during early init or unit tests)
        // the live update is silently skipped; the persisted value is loaded
        // correctly on the next app launch via OnLaunched in App.xaml.cs.
        if (Application.Current?.Resources is { } res)
            res["UiScale"] = value;
    }

    partial void OnBrandWatermarkEnabledChanged(bool value)
    {
        if (_hasPackageIdentity)
            ApplicationData.Current.LocalSettings.Values[KeyBrandWatermarkEnabled] = value;

        SaveToDisk();
    }

    partial void OnBrandWatermarkOpacityChanged(double value)
    {
        if (_hasPackageIdentity)
            ApplicationData.Current.LocalSettings.Values[KeyBrandWatermarkOpacity] = value;

        SaveToDisk();
    }

    partial void OnShowSplashScreenChanged(bool value)
    {
        if (_hasPackageIdentity)
            ApplicationData.Current.LocalSettings.Values[KeyShowSplashScreen] = value;

        SaveToDisk();
    }

    partial void OnRaceViewEnabledChanged(bool value)
    {
        if (_hasPackageIdentity)
            ApplicationData.Current.LocalSettings.Values[KeyRaceViewEnabled] = value;

        SaveToDisk();
    }

    partial void OnModeChanged(AppMode value)
    {
        if (_hasPackageIdentity)
            ApplicationData.Current.LocalSettings.Values[KeyAppMode] = value.ToString();

        SaveToDisk();
    }

    partial void OnRemoteHostChanged(string value)
    {
        if (_hasPackageIdentity)
            ApplicationData.Current.LocalSettings.Values[KeyRemoteHost] = value;

        SaveToDisk();
    }

    partial void OnRemotePortChanged(int value)
    {
        if (_hasPackageIdentity)
            ApplicationData.Current.LocalSettings.Values[KeyRemotePort] = value;

        SaveToDisk();
    }

    partial void OnRemoteTokenChanged(string value)
    {
        if (_hasPackageIdentity)
            ApplicationData.Current.LocalSettings.Values[KeyRemoteToken] = value;

        // Also persist to fallback file so dev/unpackaged runs survive rebuilds.
        SaveToDisk();
    }

    private sealed class SettingsData
    {
        public string RootFolder { get; set; } = string.Empty;
        public string OutputFolder { get; set; } = string.Empty;
        public double UiScale { get; set; } = 1.0;
        public bool BrandWatermarkEnabled { get; set; } = true;
        public double BrandWatermarkOpacity { get; set; } = 0.06;
        public bool ShowSplashScreen { get; set; } = true;
        public bool RaceViewEnabled { get; set; } = false;
        public string Mode { get; set; } = AppMode.Local.ToString();
        public string RemoteHost { get; set; } = "localhost";
        public int RemotePort { get; set; } = AgentEndpointResolver.DefaultHttpPort;
        public string RemoteToken { get; set; } = string.Empty;
    }

    private void LoadFromFileIfExists()
    {
        try
        {
            if (!File.Exists(_fallbackPath)) return;
            var json = File.ReadAllText(_fallbackPath);
            var data = JsonSerializer.Deserialize<SettingsData>(json);
            if (data is null) return;

            _rootFolder            = data.RootFolder    ?? string.Empty;
            _outputFolder          = data.OutputFolder  ?? string.Empty;
            _uiScale               = data.UiScale;
            _brandWatermarkEnabled = data.BrandWatermarkEnabled;
            _brandWatermarkOpacity = data.BrandWatermarkOpacity;
            _showSplashScreen      = data.ShowSplashScreen;
            _raceViewEnabled       = data.RaceViewEnabled;
            _mode                  = Enum.TryParse<AppMode>(data.Mode, out var parsed) ? parsed : AppMode.Local;
            _remoteHost            = data.RemoteHost ?? "localhost";
            _remotePort            = data.RemotePort;
            _remoteToken           = data.RemoteToken ?? string.Empty;
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Warn($"No se pudo cargar configuración persistida: {ex.Message}");
        }
    }

    private void NotifyLoaded()
    {
        OnPropertyChanged(nameof(RootFolder));
        OnPropertyChanged(nameof(OutputFolder));
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(RemoteHost));
        OnPropertyChanged(nameof(RemotePort));
        OnPropertyChanged(nameof(RemoteToken));
    }

    public void SaveToDisk()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_fallbackPath)!);
            var data = new SettingsData
            {
                RootFolder            = RootFolder,
                OutputFolder          = OutputFolder,
                UiScale               = UiScale,
                BrandWatermarkEnabled = BrandWatermarkEnabled,
                BrandWatermarkOpacity = BrandWatermarkOpacity,
                ShowSplashScreen      = ShowSplashScreen,
                RaceViewEnabled       = RaceViewEnabled,
                Mode                  = Mode.ToString(),
                RemoteHost            = RemoteHost,
                RemotePort            = RemotePort,
                RemoteToken           = RemoteToken,
            };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_fallbackPath, json);
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Warn($"No se pudo guardar configuración: {ex.Message}");
        }
    }
}
