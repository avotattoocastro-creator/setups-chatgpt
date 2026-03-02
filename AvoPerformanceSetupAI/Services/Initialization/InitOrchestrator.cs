using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using AvoPerformanceSetupAI.Profiles;
using AvoPerformanceSetupAI.UI.Progress;

namespace AvoPerformanceSetupAI.Services.Initialization;

/// <summary>
/// Runs staged application initialization, updating an <see cref="InitProgress"/>
/// instance so the splash overlay shows real progress to the user.
/// All <paramref name="progress"/> updates are marshalled to the UI thread via
/// <paramref name="dispatcher"/>.
/// </summary>
public static class InitOrchestrator
{
    private const int UiWarmupDelayMs  = 80;  // yields to let the frame render before heavy work
    private const int ReadyDisplayMs   = 200; // brief pause at 100 % so the user sees "¡Listo!"
    public static async Task RunAsync(
        InitProgress progress,
        DispatcherQueue dispatcher,
        CancellationToken ct = default)
    {
        // Helper: update title, detail and percentage on the UI thread.
        void Set(double pct, string title, string detail = "")
            => dispatcher.TryEnqueue(() =>
            {
                progress.Percent = pct;
                progress.Title   = title;
                if (detail.Length > 0) progress.Detail = detail;
            });

        // Helper: run a step completion flag setter on the UI thread.
        void Complete(Action action) => dispatcher.TryEnqueue(() => action());

        // ── Stage 1: UI warmup ─────────────────────────────────────────────────
        Set(5, "Cargando interfaz...", "Preparando componentes de UI");
        await Task.Delay(UiWarmupDelayMs, ct);

        // ── Stage 2: Settings ──────────────────────────────────────────────────
        Set(15, "Cargando ajustes...", "Leyendo configuración guardada");
        await Task.Run(() => { _ = SetupSettings.Instance; }, ct);
        Complete(() => { progress.StepUiDone = true; });

        // ── Stage 3: Prepare app directories ──────────────────────────────────
        Set(25, "Preparando carpetas...", "Perfiles, vueltas de referencia, modelos ML");
        await Task.Run(CreateAppDirectories, ct);

        // ── Stage 4: Load ProfileStore + DriverProfile ─────────────────────────
        Set(40, "Cargando perfiles...", "Perfil de conductor y configuraciones guardadas");
        await Task.Run(() =>
        {
            ProfileStore.Instance.ListAll();
            DriverProfile.Load();
        }, ct);
        Complete(() => { progress.StepSettingsDone = true; });

        // ── Stage 5: ML / RL model warmup (placeholder) ───────────────────────
        Set(70, "Cargando modelos ML/RL...", "Modelos predictivos y de calibración");
        // Models are loaded on-demand; this stage represents the future async
        // loading hook.  No blocking work here — just update progress and move on.
        Complete(() => { progress.StepModelsDone = true; });

        // ── Stage 6: TelemetryService base init ────────────────────────────────
        Set(90, "Inicializando telemetría...", "Preparando subsistema de telemetría");
        await TelemetryService.InitializeAsync();
        Complete(() => { progress.StepTelemetryDone = true; });

        // ── Stage 7: Ready ─────────────────────────────────────────────────────
        dispatcher.TryEnqueue(() =>
        {
            progress.Percent         = 100;
            progress.Title           = "¡Listo!";
            progress.Detail          = string.Empty;
            progress.IsIndeterminate = false;
        });
        await Task.Delay(ReadyDisplayMs, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void CreateAppDirectories()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var root = Path.Combine(docs, "AvoPerformanceSetupAI");
        Directory.CreateDirectory(Path.Combine(root, "Profiles"));
        Directory.CreateDirectory(Path.Combine(root, "ReferenceLaps"));
        Directory.CreateDirectory(Path.Combine(root, "ML"));
    }
}
