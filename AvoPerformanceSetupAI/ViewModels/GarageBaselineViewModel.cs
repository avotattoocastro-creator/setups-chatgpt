using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AvoPerformanceSetupAI.Models;
using AvoPerformanceSetupAI.Services;

namespace AvoPerformanceSetupAI.ViewModels;

public class GarageBaselineViewModel : ObservableObject
{
    private readonly BaselineStorage _storage = new();

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    private string _carFilter = string.Empty;
    public string CarFilter
    {
        get => _carFilter;
        set => SetProperty(ref _carFilter, value);
    }

    private string _trackFilter = string.Empty;
    public string TrackFilter
    {
        get => _trackFilter;
        set => SetProperty(ref _trackFilter, value);
    }

    private string _sessionFilter = string.Empty;
    public string SessionFilter
    {
        get => _sessionFilter;
        set => SetProperty(ref _sessionFilter, value);
    }

    private BaselineEntry? _selectedBaseline;
    public BaselineEntry? SelectedBaseline
    {
        get => _selectedBaseline;
        set => SetProperty(ref _selectedBaseline, value);
    }

    public ObservableCollection<BaselineEntry> Baselines { get; } = new();

    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand NewBaselineCommand { get; }
    public IAsyncRelayCommand DuplicateCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }
    public IAsyncRelayCommand ExportCommand { get; }
    public IAsyncRelayCommand ImportCommand { get; }
    public IRelayCommand ApplyToCurrentSetupCommand { get; }
    public IRelayCommand SetAsCurrentBaselineCommand { get; }

    public GarageBaselineViewModel()
    {
        LoadCommand = new AsyncRelayCommand(LoadAsync);
        NewBaselineCommand = new AsyncRelayCommand(NewBaselineAsync);
        DuplicateCommand = new AsyncRelayCommand(DuplicateAsync);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync);
        ExportCommand = new AsyncRelayCommand(ExportAsync);
        ImportCommand = new AsyncRelayCommand<string?>(ImportAsync);
        ApplyToCurrentSetupCommand = new RelayCommand(ApplyToCurrentSetup);
        SetAsCurrentBaselineCommand = new RelayCommand(SetAsCurrentBaseline);
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var items = await _storage.LoadAllAsync(CarFilter, TrackFilter);
        var filtered = items.Where(b => string.IsNullOrWhiteSpace(SearchText)
            || b.Name.Contains(SearchText, System.StringComparison.OrdinalIgnoreCase)
            || b.Car.Contains(SearchText, System.StringComparison.OrdinalIgnoreCase)
            || b.Track.Contains(SearchText, System.StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(SessionFilter))
        {
            filtered = filtered.Where(b => b.SessionType.Equals(SessionFilter, System.StringComparison.OrdinalIgnoreCase));
        }
        Baselines.Clear();
        foreach (var b in filtered) Baselines.Add(b);
    }

    private async Task NewBaselineAsync()
    {
        var entry = new BaselineEntry { Name = "New Baseline", SessionType = "RACE" };
        await _storage.SaveAsync(entry);
        await LoadAsync();
        SelectedBaseline = entry;
    }

    private async Task DuplicateAsync()
    {
        if (SelectedBaseline is null) return;
        var copy = new BaselineEntry
        {
            Name = SelectedBaseline.Name + " Copy",
            Car = SelectedBaseline.Car,
            Track = SelectedBaseline.Track,
            SessionType = SelectedBaseline.SessionType,
            Notes = SelectedBaseline.Notes,
            KeyParams = new(SelectedBaseline.KeyParams),
            TyreTargets = new(SelectedBaseline.TyreTargets),
            AeroFront = SelectedBaseline.AeroFront,
            AeroRear = SelectedBaseline.AeroRear,
            AeroBalance = SelectedBaseline.AeroBalance
        };
        await _storage.SaveAsync(copy);
        await LoadAsync();
        SelectedBaseline = copy;
    }

    private async Task DeleteAsync()
    {
        if (SelectedBaseline is null) return;
        await _storage.DeleteAsync(SelectedBaseline);
        await LoadAsync();
    }

    private async Task ExportAsync()
    {
        if (SelectedBaseline is null) return;
        var path = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments), "AvoPerformanceSetupAI", SelectedBaseline.Name + ".json");
        await _storage.ExportAsync(SelectedBaseline, path);
    }

    private async Task ImportAsync(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return;
        await _storage.ImportAsync(sourcePath);
        await LoadAsync();
    }

    private void ApplyToCurrentSetup()
    {
        SharedAppState.Instance.BaselineSetup = new SetupModel
        {
            Id = SelectedBaseline?.Id ?? string.Empty,
            Car = SelectedBaseline?.Car ?? string.Empty,
            Track = SelectedBaseline?.Track ?? string.Empty,
            Sections = SelectedBaseline?.KeyParams.ToDictionary(k => k.Key, v => v.Value) ?? new()
        };
    }

    private void SetAsCurrentBaseline()
    {
        if (SelectedBaseline is null) return;
        SharedAppState.Instance.BaselineSetup = new SetupModel
        {
            Id = SelectedBaseline.Id,
            Car = SelectedBaseline.Car,
            Track = SelectedBaseline.Track,
            Sections = SelectedBaseline.KeyParams.ToDictionary(k => k.Key, v => v.Value)
        };
    }
}
