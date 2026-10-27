# Astra Launcher

Windows Minecraft Java Edition launcher (WPF, hand-written MVVM) built in verified stages.

**Current status:** the launcher can download and install vanilla Minecraft versions (official source with BMCLAPI mirror and automatic fallback, per-file parallel segmented downloads), manage instances, select Java runtimes, launch the game offline, keep game logs, persist settings, and manage accounts (offline profiles plus the complete official Microsoft → Xbox Live → XSTS → Minecraft authentication chain with DPAPI-protected tokens). Real end-to-end verification passed with **Minecraft 1.21.1** (download + offline launch into the main menu) and with an offline account selected. A real Microsoft sign-in additionally needs an Azure public-client id (see below). Mod loaders (Fabric/Quilt/Forge/NeoForge) and the server list are the remaining milestones. No installer/auto-updater is included by design.

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

## User interface

The WPF shell uses a clear navigation sidebar, a header with the current account and a footer with the recommended start flow. Every page follows the same card layout with Chinese labels and short explanations: the welcome page guides through the three steps (download → create instance → launch) and summarizes the Minecraft directory, account, Java and version status; instances, downloads, accounts and settings are grouped into clearly labelled sections; the server page explains the planned M6 features. Screenshots are kept under `docs/evidence/ui-redesign/`.

## Project layout

- `AstraLauncher.App` — WPF entry point, DI composition root, MVVM shell/navigation, settings and instance/download pages.
- `AstraLauncher.Core` — dependency-free domain records and service contracts.
- `AstraLauncher.Infrastructure` — launcher paths, JSON settings, file logging, version manifest and download source resolution.
- `AstraLauncher.Game` — version metadata parsing/inheritance, install planning, safe local instance management, Java discovery, command construction, process launching and offline profiles.
- `AstraLauncher.Downloader` — asynchronous download queue with resume, retries, integrity checks and per-file HTTP Range segmented downloads.
- `AstraLauncher.Authentication` — device-code Microsoft OAuth, Xbox Live/XSTS, Minecraft services login, DPAPI token protection and the account service.
- `AstraLauncher.Multiplayer` — reserved for the server list and later signalling/relay abstractions.
- `AstraLauncher.Tests` — xUnit automated tests (220 currently passing).
- `scripts` — build and packaging helpers.

### Accounts

- **Offline profiles** work out of the box and derive the standard Java-compatible offline UUID from the player name.
- **Official Microsoft sign-in** uses the OAuth 2.0 device-code flow (no client secret, tokens stored encrypted with Windows DPAPI for the current user). It requires an Azure app registration used as a public client:
  1. In the Azure portal create an app registration, add a **mobile and desktop application** platform and enable public client flows,
  2. Add the delegated permission **XboxLive.signin** (and `offline_access`) on Microsoft Graph,
  3. Paste the application (client) id into the **Client ID** field on the Accounts page and click **添加 Microsoft 账户**; a device code and verification URL are shown and the account appears after sign-in.
- The selected account is used by the launch pipeline: Microsoft accounts send a freshly refreshed Minecraft access token (with XUID/client id), offline accounts launch with their offline UUID.

Runtime data is split on purpose:

- **Minecraft files** — `versions`, `libraries` and `assets` live in a shared **Minecraft directory**, normally a `.minecraft` folder:
  1. an existing `.minecraft` next to the executable wins (portable mode),
  2. otherwise an existing `%APPDATA%\.minecraft` (the official launcher/PCL layout) is reused,
  3. otherwise a new `.minecraft` folder is created next to the executable.
  The directory is shown and can be changed (with a folder picker) on the Settings page. The layout is the standard Minecraft one, so other launchers (official launcher, PCL, …) see the same versions/libraries/assets; existing files are verified and reused, only missing files are downloaded.
- **Launcher data** — settings, accounts (DPAPI-protected tokens), logs, instance directories and caches are stored in **`<MinecraftDirectory>\AstraLauncher`**, so a portable `.minecraft` folder contains everything the launcher needs and can be moved as a whole. On the first start after upgrading, data from the legacy `%LOCALAPPDATA%\AstraLauncher` location is migrated automatically (instance paths are rewritten; the old directory is removed after a successful move).

The single-file executable does not modify the folder it is started from (apart from creating the portable `.minecraft` when that default is chosen).
