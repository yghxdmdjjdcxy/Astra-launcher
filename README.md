# Astra Launcher

Windows Minecraft Java Edition launcher (WPF, hand-written MVVM) built in verified stages.

**Current status:** the launcher can download and install vanilla Minecraft versions (official source with BMCLAPI mirror and automatic fallback, per-file parallel segmented downloads), manage instances, select Java runtimes, launch the game offline, keep game logs, and persist settings. Real end-to-end verification passed with **Minecraft 1.21.1** (download + offline launch into the main menu). Official Microsoft sign-in, mod loaders (Fabric/Quilt/Forge/NeoForge) and the server list are the remaining milestones. No installer/auto-updater is included by design.

## Requirements

- Windows 10 or later
- To build: .NET SDK 10.0.100 (or a compatible .NET 10 SDK)
- To run the single-file build: nothing else — the executable is self-contained

## Build, test and run

```powershell
dotnet restore AstraLauncher.sln
dotnet build AstraLauncher.sln --configuration Release --no-restore
dotnet test .\AstraLauncher.Tests\AstraLauncher.Tests.csproj --configuration Release --no-build
dotnet run --project .\AstraLauncher.App\AstraLauncher.App.csproj
```

## Single-file executable

```powershell
pwsh -File .\scripts\publish-single-file.ps1
```

Produces exactly one file: `artifacts/AstraLauncher-win-x64-single/AstraLauncher.exe` (self-contained win-x64, ~62 MB). It bundles the .NET runtime, native libraries and all managed assemblies, so the target machine does not need .NET installed. The first launch extracts native components to `%TEMP%\.net` (normal for single-file WPF apps), so it starts a little slower than the folder build. A folder-based release can still be produced with `dotnet publish` without the single-file properties.

## Project layout

- `AstraLauncher.App` — WPF entry point, DI composition root, MVVM shell/navigation, settings and instance/download pages.
- `AstraLauncher.Core` — dependency-free domain records and service contracts.
- `AstraLauncher.Infrastructure` — launcher paths, JSON settings, file logging, version manifest and download source resolution.
- `AstraLauncher.Game` — version metadata parsing/inheritance, install planning, safe local instance management, Java discovery, command construction, process launching and offline profiles.
- `AstraLauncher.Downloader` — asynchronous download queue with resume, retries, integrity checks and per-file HTTP Range segmented downloads.
- `AstraLauncher.Authentication` — reserved for official Microsoft/Xbox/Minecraft authentication and offline profiles.
- `AstraLauncher.Multiplayer` — reserved for the server list and later signalling/relay abstractions.
- `AstraLauncher.Tests` — xUnit automated tests (144 currently passing).
- `scripts` — build and packaging helpers.

Runtime data (instances, versions, libraries, assets, Java runtimes, logs, `settings.json`) is stored under `%LOCALAPPDATA%\AstraLauncher`. Logs are written to `%LOCALAPPDATA%\AstraLauncher\logs`. The single-file executable does not modify the folder it is started from.
