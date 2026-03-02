using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AvoPerformanceSetupAI.Models;
using AvoPerformanceSetupAI.Services;
using AvoPerformanceSetupAI.Services.Setup;

namespace AvoPerformanceSetupAI.ViewModels;

public partial class SetupDiffViewModel : ObservableObject
{
    // ── Application-wide shared instance ─────────────────────────────────────

    /// <summary>
    /// Shared singleton used by <see cref="SessionsViewModel"/> to push the
    /// auto-diff result (base vs. applied) and by <see cref="Views.SetupDiffPage"/>
    /// to display it.
    /// </summary>
    public static SetupDiffViewModel Shared { get; } = new();

    // ── Services ──────────────────────────────────────────────────────────────

    private static readonly SetupDiffEngine  LegacyEngine = new();
    private static readonly SetupDiffService DiffService  = new();

    // ── File selection (manual RunDiff) ───────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunDiffCommand))]
    private string? _baselineFile;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunDiffCommand))]
    private string? _candidateFile;

    // ── Results ───────────────────────────────────────────────────────────────

    public ObservableCollection<SetupDiffEntry>   DiffEntries    { get; } = new();
    public ObservableCollection<GroupSummaryRow>  GroupSummaries { get; } = new();

    /// <summary>Diff grouped by <see cref="SetupCategory"/> — drives the grouped UI.</summary>
    public ObservableCollection<DiffCategoryGroup> CategoryGroups { get; } = new();

    [ObservableProperty] private string _statusText       = "Selecciona dos setups y pulsa Run Diff, o aplica una propuesta.";
    [ObservableProperty] private bool   _isBusy;

    /// <summary>
    /// Label shown when a diff was auto-triggered by an Apply operation,
    /// e.g. "base.ini → versioned__AI__v001.ini".
    /// </summary>
    [ObservableProperty] private string _lastAppliedLabel = string.Empty;

    // ── File list (mirrors SessionsViewModel so the ComboBoxes stay in sync) ──

    /// <summary>Files available for selection — reflects the currently loaded car/track.</summary>
    public ObservableCollection<string> SetupFiles => SessionsViewModel.Shared.SetupFiles;

    // ── Manual RunDiff command ────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanRunDiff))]
    private async System.Threading.Tasks.Task RunDiffAsync()
    {
        DiffEntries.Clear();
        GroupSummaries.Clear();
        CategoryGroups.Clear();
        StatusText = "Cargando…";
        IsBusy = true;

        try
        {
            var vm       = SessionsViewModel.Shared;
            var carId    = vm.CarId;
            var trackId  = vm.TrackId;
            var provider = vm.CreateProviderPublic();

            if (string.IsNullOrEmpty(carId) || string.IsNullOrEmpty(trackId))
            {
                StatusText = "Selecciona un coche y circuito primero.";
                return;
            }

            // Load both files in parallel
            var taskA = provider.ReadSetupTextAsync(carId, trackId, BaselineFile!);
            var taskB = provider.ReadSetupTextAsync(carId, trackId, CandidateFile!);
            await System.Threading.Tasks.Task.WhenAll(taskA, taskB);

            LastAppliedLabel = $"{BaselineFile} → {CandidateFile}";
            ApplyDiff(taskA.Result, taskB.Result);
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            AppLogger.Instance.Error($"Setup Diff error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRunDiff() =>
        !string.IsNullOrEmpty(BaselineFile) &&
        !string.IsNullOrEmpty(CandidateFile) &&
        BaselineFile != CandidateFile;

    // ── Auto-diff (triggered by SessionsViewModel after Apply) ───────────────

    /// <summary>
    /// Computes and displays the diff between <paramref name="baseText"/> (original INI)
    /// and <paramref name="proposedText"/> (INI after applying proposals).
    /// Called from <see cref="SessionsViewModel"/> on the UI thread after a successful Apply.
    /// </summary>
    public void Load(string baseText, string proposedText, string baseLabel, string proposedLabel)
    {
        DiffEntries.Clear();
        GroupSummaries.Clear();
        CategoryGroups.Clear();

        LastAppliedLabel = $"{baseLabel} → {proposedLabel}";
        ApplyDiff(baseText, proposedText);
    }

    // ── Copy diff to clipboard ────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanCopyDiff))]
    private void CopyDiff()
    {
        var sb = new StringBuilder();
        foreach (var group in CategoryGroups)
        {
            foreach (var entry in group.Entries)
            {
                sb.AppendLine(
                    $"[{entry.Category}] {entry.Section}.{entry.Name}: " +
                    $"{entry.OldRaw} -> {entry.NewRaw}");
            }
        }

        var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dataPackage.SetText(sb.ToString().TrimEnd());
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
    }

    private bool CanCopyDiff() => CategoryGroups.Count > 0;

    // ── Core diff builder ─────────────────────────────────────────────────────

    private void ApplyDiff(string baseText, string proposedText)
    {
        var diff = DiffService.ComputeDiff(baseText, proposedText);

        foreach (var entry in diff)
            DiffEntries.Add(entry);

        // Group by Category, ordered by name
        var groups = diff
            .GroupBy(e => e.Category)
            .OrderBy(g => g.Key.ToString())
            .Select(g => new DiffCategoryGroup(
                categoryName: g.Key.ToString(),
                count:        g.Count(),
                entries:      g.OrderBy(e => e.Section).ThenBy(e => e.Name).ToList()));

        foreach (var group in groups)
            CategoryGroups.Add(group);

        // Legacy group summary (for backward compat with GroupSummaries binding)
        foreach (var (group, count) in LegacyEngine.GroupSummary(diff))
            GroupSummaries.Add(new GroupSummaryRow(group, count));

        StatusText = diff.Count == 0
            ? "Sin diferencias."
            : $"{diff.Count} cambio(s) — "
              + $"{diff.Count(e => e.Kind == SetupDiffKind.Changed)} modificados, "
              + $"{diff.Count(e => e.Kind == SetupDiffKind.Added)} añadidos, "
              + $"{diff.Count(e => e.Kind == SetupDiffKind.Removed)} eliminados "
              + $"en {CategoryGroups.Count} categoría(s).";

        CopyDiffCommand.NotifyCanExecuteChanged();

        AppLogger.Instance.Data(
            $"Setup Diff: {LastAppliedLabel} — {diff.Count} cambio(s)");
    }
}

// ── Supporting types ──────────────────────────────────────────────────────────

/// <summary>One group of diff entries sharing the same <see cref="SetupCategory"/>.</summary>
public sealed class DiffCategoryGroup
{
    public string                   CategoryName { get; }
    public int                      Count        { get; }
    public IReadOnlyList<SetupDiffEntry> Entries { get; }

    /// <summary>Badge text showing the count of changes in this category.</summary>
    public string CountBadge => Count.ToString(CultureInfo.InvariantCulture);

    public DiffCategoryGroup(string categoryName, int count, IReadOnlyList<SetupDiffEntry> entries)
    {
        CategoryName = categoryName;
        Count        = count;
        Entries      = entries;
    }
}

/// <summary>One row in the group-summary panel.</summary>
public sealed record GroupSummaryRow(string Group, int Count)
{
    public string Display => $"{Group}: {Count}";
}

