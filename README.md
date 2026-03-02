# AvoPerformanceSetupAI

A WinUI 3 (Windows App SDK) desktop application for AI-assisted motorsport setup management.

---

## ⬇️ Cómo descargar el proyecto / How to Download

### Opción 1 — Git clone (recomendado / recommended)

Abre una terminal (PowerShell, CMD o Git Bash) y ejecuta:

```bash
git clone https://github.com/avotattoocastro-creator/setups.git
cd setups
```

> Si no tienes Git instalado, descárgalo en <https://git-scm.com/downloads>.

---

### Option 1 — Git clone (recommended)

Open a terminal (PowerShell, CMD, or Git Bash) and run:

```bash
git clone https://github.com/avotattoocastro-creator/setups.git
cd setups
```

> If you don't have Git, download it at <https://git-scm.com/downloads>.

---

### Opción 2 — Descargar ZIP / Download ZIP

1. Ve a la página principal del repositorio:  
   <https://github.com/avotattoocastro-creator/setups>
2. Haz clic en el botón verde **`< > Code`**.
3. Selecciona **Download ZIP**.
4. Extrae el archivo descargado en la carpeta que prefieras.

---

### Option 2 — Download ZIP

1. Go to the repository home page:  
   <https://github.com/avotattoocastro-creator/setups>
2. Click the green **`< > Code`** button.
3. Select **Download ZIP**.
4. Extract the downloaded archive to any folder.

---

### Opción 3 — GitHub Desktop

1. Descarga [GitHub Desktop](https://desktop.github.com/).
2. En la página del repositorio haz clic en **`< > Code` → Open with GitHub Desktop**.
3. Elige la carpeta local y haz clic en **Clone**.

---

### Option 3 — GitHub Desktop

1. Download [GitHub Desktop](https://desktop.github.com/).
2. On the repository page click **`< > Code` → Open with GitHub Desktop**.
3. Choose a local folder and click **Clone**.

---

## Prerequisites

- **Windows 10/11** (version 1809 / build 17763 or later; build 19041 recommended)
- **Visual Studio 2022** (version 17.8+)
  - Workload: **.NET Desktop Development**
  - Workload: **Windows Application Development** (includes Windows App SDK)
- **Windows App SDK 1.5** (installed automatically via NuGet)

> **Nota / Note — Visual Studio 18 Insiders:**  
> Si tienes instalado Visual Studio 18 Preview / Insiders **sin** el componente de
> C++ (`VC\Tools\MSVC`), el build fallará con el error `GetLatestMSVCVersion /
> DirectoryNotFoundException`. El archivo `Directory.Build.props` incluido en este
> repositorio ya contiene un workaround automático que omite esas instalaciones.  
> If you have Visual Studio 18 Preview / Insiders installed **without** the C++ workload,
> the build fails with `GetLatestMSVCVersion / DirectoryNotFoundException`.
> The `Directory.Build.props` file included in this repo automatically works around
> the issue by skipping VS installations that do not have `VC\Tools\MSVC`.

## Opening the Solution

1. Download the repository using one of the methods in [⬇️ Cómo descargar el proyecto](#️-cómo-descargar-el-proyecto--how-to-download) above
2. Open `AvoPerformanceSetupAI.sln` with Visual Studio 2022
3. Restore NuGet packages (right-click solution → Restore NuGet Packages)
4. Set build platform to **x64**
5. Press **F5** to build and run

## Architecture

- **MVVM** pattern using [CommunityToolkit.Mvvm](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/)
- **WinUI 3** with Windows App SDK 1.5 (packaged/MSIX)
- **DataGrid** via CommunityToolkit.WinUI.Controls.DataGrid
- **Localization**: Spanish (es-ES) and English (en-US) via `.resw` resource files

## Localization

Resource files are in `AvoPerformanceSetupAI/Strings/`:
- `en-US/Resources.resw` — English strings
- `es-ES/Resources.resw` — Spanish strings

## Project Structure

```
AvoPerformanceSetupAI/
├── Models/           # Data models (SetupIteration, Proposal, IniEntry, LogEntry)
├── ViewModels/       # MVVM ViewModels with CommunityToolkit.Mvvm
├── Views/            # Tab pages (Configuracion, Sesiones, Control, Terminal)
├── Converters/       # IValueConverter classes (CategoryToBadgeBrush, CategoryToText)
├── Services/         # AppLogger, SetupIniParser, SetupSettings
├── Strings/          # Localization resources (en-US, es-ES)
└── Assets/           # App icons and images
```

## Notes

- The app uses a **dark theme** with teal accent colors.
- Folder paths (setup root + output) are **persisted** via `ApplicationData.LocalSettings` and survive restarts.
- All pages share a single `SessionsViewModel.Shared` instance — changes in Sesiones are immediately reflected in the Control tab.
- The INI parser reads real Assetto Corsa / ACC setup files; proposals are generated from actual numeric parameters.

---

## Changelog

### v1.1.0 (2026-02)
- **Settings persistence** — `RootFolder` and `OutputFolder` are saved to `ApplicationData.LocalSettings` and automatically restored on next launch.
- **Shared ViewModel** — Added `SessionsViewModel.Shared` singleton so Sesiones and Control tabs always show the same session state.
- **ControlPage** — Replaced placeholder with a live-bound control panel (session info, telemetry status, risk level, Connect / Start / Stop / Apply Proposal / Rollback buttons).
- **Converters refactor** — `CategoryToBadgeBrushConverter` and `CategoryToTextBrushConverter` moved to a dedicated `Converters/CategoryConverters.cs` file.
- **Package** version bumped to `1.1.0.0`.

### v1.0.0 (2026-01)
- Initial WinUI 3 application with MVVM architecture.
- Sesiones tab with Car / Track / Mode selectors, Setup DataGrid, and AI proposal panel.
- Terminal tab with real-time log filtering (INFO / AI / DATA / WARN / ERROR).
- Configuración tab with folder picker (setup root + output destination).
- INI parser + section-aware apply and rollback.
