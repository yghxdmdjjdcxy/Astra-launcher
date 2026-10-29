# Astra Launcher

Windows Minecraft Java Edition launcher (WPF, hand-written MVVM) built in verified stages.

**Current status:** the launcher can download and install vanilla Minecraft versions (official source with BMCLAPI mirror and automatic fallback, per-file parallel segmented downloads), manage instances, detect locally installed versions (including PCL/other-launcher profiles), install mod loaders (Fabric/Quilt via Meta profiles, Forge/NeoForge via their official installers), select Java runtimes, launch the game offline, keep game logs, persist settings, and manage accounts (offline profiles plus the complete official Microsoft → Xbox Live → XSTS → Minecraft authentication chain with DPAPI-protected tokens). Real end-to-end verification passed with **Minecraft 1.21.1** (download + offline launch into the main menu) and with an offline account selected, and with **Fabric, Quilt and Forge** 1.21.1 profiles installed through the launcher and launched to the main menu. NeoForge installation works, but its 1.21.1 launch currently stops inside the NeoForge BootstrapLauncher (known limitation, see below). A real Microsoft sign-in additionally needs an Azure public-client id (see below). The server list is the remaining milestone. No installer/auto-updater is included by design.

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

The WPF shell uses a clear navigation sidebar, a header with the current account and a footer with the recommended start flow. Every page follows the same card layout with Chinese labels and short explanations: the welcome page guides through the three steps (download → create instance → launch) and summarizes the Minecraft directory, account, Java and version status; instances, downloads, accounts and settings are grouped into clearly labelled sections; the server page explains the planned M6 features. Screenshots are kept under `docs/evidence/ui-redesign/` and `docs/evidence/m5/`.

## Mod loaders

The Downloads page has a loader card below the version list: choose a Minecraft version, then a loader (Fabric, Quilt, Forge, NeoForge), refresh its version list (with a recommended build preselected) and click **安装加载器**. Installed loader profiles appear in the instance version picker together with locally installed versions from `.minecraft/versions` (for example PCL profiles).

- **Fabric/Quilt** — the loader profile JSON is fetched from Fabric Meta / Quilt Meta, written as `versions/<loader>-<version>/…json`, and missing libraries/asset files are completed by the regular install pipeline. Libraries whose metadata only names a Maven repository URL are resolved through the repository fallback.
- **Forge** — the official installer jar is downloaded, run with a Java 21/17 runtime (`--installClient`), then the generated profile is verified/completed by the install pipeline. A minimal `launcher_profiles.json` is created first because the installer refuses to run without one.
- **NeoForge** — the installer jar and Maven artifacts are mirrored through BMCLAPI when `maven.neoforged.net` is unreachable; the embedded version profile is installed and its libraries are completed through the mirror-aware downloader. **Known limitation:** launching a NeoForge 1.21.1 profile currently fails inside NeoForge's BootstrapLauncher (`NoSuchElementException` while building its service module layer), while installation itself succeeds. Fabric/Quilt/Forge launching is fully verified.

Loader metadata is merged with the vanilla parent profile; library deduplication keeps classifier (natives) variants, which is required for inherited loader launches (a regression test covers this).

## Project layout

- `AstraLauncher.App` — WPF entry point, DI composition root, MVVM shell/navigation, settings and instance/download pages.
- `AstraLauncher.Core` — dependency-free domain records and service contracts.
- `AstraLauncher.Infrastructure` — launcher paths, JSON settings, file logging, version manifest and download source resolution.
- `AstraLauncher.Game` — version metadata parsing/inheritance, install planning, safe local instance management, Java discovery, command construction, process launching and offline profiles.
- `AstraLauncher.Downloader` — asynchronous download queue with resume, retries, integrity checks and per-file HTTP Range segmented downloads.
- `AstraLauncher.Authentication` — device-code Microsoft OAuth, Xbox Live/XSTS, Minecraft services login, DPAPI token protection and the account service.
- `AstraLauncher.Multiplayer` — reserved for the server list and later signalling/relay abstractions.
- `AstraLauncher.Tests` — xUnit automated tests (266 currently passing).
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
