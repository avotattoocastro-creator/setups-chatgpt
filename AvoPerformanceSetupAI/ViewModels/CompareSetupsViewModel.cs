using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AvoPerformanceSetupAI.Models;
using AvoPerformanceSetupAI.Services;

namespace AvoPerformanceSetupAI.ViewModels;

public class CompareSetupsViewModel : ObservableObject
{
    private readonly CompareReportExporter _exporter = new();

    private SetupModel? _baseline;
    public SetupModel? Baseline
    {
        get => _baseline;
        set => SetProperty(ref _baseline, value);
    }

    private SetupModel? _current;
    public SetupModel? Current
    {
        get => _current;
        set => SetProperty(ref _current, value);
    }

    private SetupModel? _selectedIteration;
    public SetupModel? SelectedIteration
    {
        get => _selectedIteration;
        set => SetProperty(ref _selectedIteration, value);
    }

    private CompareImpactModel _impact = new();
    public CompareImpactModel Impact
    {
        get => _impact;
        set => SetProperty(ref _impact, value);
    }

    public ObservableCollection<SetupModel> Iterations => SharedAppState.Instance.Iterations;

    public IRelayCommand SwapCompareTargetCommand { get; }
    public IRelayCommand ApplySelectedAsCurrentCommand { get; }
    public IRelayCommand CreateIterationFromDeltaCommand { get; }
    public IRelayCommand ExportReportCommand { get; }

    public CompareSetupsViewModel()
    {
        SwapCompareTargetCommand = new RelayCommand(SwapCompareTarget);
        ApplySelectedAsCurrentCommand = new RelayCommand(ApplySelectedAsCurrent);
        CreateIterationFromDeltaCommand = new RelayCommand(CreateIterationFromDelta);
        ExportReportCommand = new RelayCommand(ExportReport);

        Baseline = SharedAppState.Instance.BaselineSetup;
        Current = SharedAppState.Instance.CurrentSetup;
        SelectedIteration = Iterations.FirstOrDefault();
        UpdateImpact();
    }

    private void SwapCompareTarget()
    {
        (Baseline, Current) = (Current, Baseline);
        UpdateImpact();
    }

    private void ApplySelectedAsCurrent()
    {
        if (SelectedIteration is null) return;
        SharedAppState.Instance.CurrentSetup = SelectedIteration;
        Current = SelectedIteration;
        UpdateImpact();
    }

    private void CreateIterationFromDelta()
    {
        var iteration = new SetupModel
        {
            Id = $"iter_{DateTime.UtcNow:HHmmss}",
            Car = Current?.Car ?? string.Empty,
            Track = Current?.Track ?? string.Empty,
            Sections = new(Current?.Sections ?? new())
        };
        Iterations.Add(iteration);
        SelectedIteration = iteration;
    }

    private void ExportReport()
    {
        var baselineId = Baseline?.Id ?? "baseline";
        var currentId = Current?.Id ?? "current";
        var selectedId = SelectedIteration?.Id ?? "selected";
        _ = _exporter.ExportTextAsync(baselineId, currentId, selectedId, Impact);
        _ = _exporter.ExportJsonAsync(Impact);
    }

    private void UpdateImpact()
    {
        Impact = new CompareImpactModel
        {
            PredictedLapTime = 95.0 + (Current?.Sections.Count ?? 0) * 0.01,
            BalanceScore = 75,
            TyreHeatRisk = 40,
            StabilityRisk = 35,
            AeroEfficiency = 80
        };
    }
}
