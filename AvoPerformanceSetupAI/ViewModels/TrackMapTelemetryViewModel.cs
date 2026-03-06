using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AvoPerformanceSetupAI.Interfaces;
using AvoPerformanceSetupAI.Models;
using AvoPerformanceSetupAI.Services;

namespace AvoPerformanceSetupAI.ViewModels;

public partial class TrackMapTelemetryViewModel : ObservableObject
{
    private readonly ITelemetryProvider _telemetryProvider;
    private readonly TrackOverlayBuffer _overlay = new();
    private readonly ReferenceLapLoader _referenceLoader = new();

    [ObservableProperty] private TelemetryModel _currentTelemetry = new();
    [ObservableProperty] private ObservableCollection<ReferenceLapPoint> _referenceLap = new();
    [ObservableProperty] private ObservableCollection<TrackSegmentStat> _segments;
    [ObservableProperty] private double _s1Delta;
    [ObservableProperty] private double _s2Delta;
    [ObservableProperty] private double _s3Delta;

    public TrackMapTelemetryViewModel()
    {
        _telemetryProvider = new FakeTelemetryProvider(); // TODO: replace with AC shared memory provider
        _segments = new ObservableCollection<TrackSegmentStat>(_overlay.Segments);
        _telemetryProvider.TelemetryUpdated += OnTelemetryUpdated;
        _ = _telemetryProvider.StartAsync();
    }

    private void OnTelemetryUpdated(object? sender, TelemetryModel e)
    {
        CurrentTelemetry = e;
        _overlay.AddSample(e.LapDistPct, e.Throttle, e.Brake, e.SpeedKph);
    }

    [RelayCommand]
    private void ClearOverlay()
    {
        _overlay.Clear();
    }

    [RelayCommand]
    private async Task LoadReferenceLap()
    {
        // TODO: open file picker to choose CSV
        var points = await _referenceLoader.LoadCsvAsync("Car", "Track", "reference.csv");
        ReferenceLap = new ObservableCollection<ReferenceLapPoint>(points);
    }

    [RelayCommand]
    private void SetCurrentLapAsReference()
    {
        // TODO: persist current lap samples
    }

    [RelayCommand]
    private void ExportMapSnapshot()
    {
        // TODO: export canvas bitmap
    }
}
