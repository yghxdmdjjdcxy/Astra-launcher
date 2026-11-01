# Astra Launcher

Windows Minecraft Java Edition launcher (WPF, hand-written MVVM) built in verified stages.

**Current status:** the launcher can download and install vanilla Minecraft versions (official source with BMCLAPI mirror and automatic fallback, per-file parallel segmented downloads), manage instances, detect locally installed versions (including PCL/other-launcher profiles), install mod loaders (Fabric/Quilt via Meta profiles, Forge/NeoForge via their official installers), select Java runtimes, launch the game offline, keep game logs, persist settings, and manage accounts (offline profiles plus the complete official Microsoft → Xbox Live → XSTS → Minecraft authentication chain with DPAPI-protected tokens). Real end-to-end verification passed with **Minecraft 1.21.1** (download + offline launch into the main menu) and with an offline account selected, and with **Fabric, Quilt and Forge** 1.21.1 profiles installed through the launcher and launched to the main menu. NeoForge installation works, but its 1.21.1 launch currently stops inside the NeoForge BootstrapLauncher (known limitation, see below). A real Microsoft sign-in additionally needs an Azure public-client id (see below). The server list with one-click `--server/--port` join is implemented as well, and a **Mods page** manages local jars and installs compatible mods from Modrinth. A **crash analyzer** inspects `latest.log`/`crash-reports` after a failed run and offers PCL-style explanations plus reversible auto-repair or manual handling. No installer/auto-updater is included by design.

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

The WPF shell uses a clear navigation sidebar, a header with the current account and a footer with the recommended start flow. Every page follows the same card layout with Chinese labels and short explanations: the welcome page guides through the three steps (download → create instance → launch) and summarizes the Minecraft directory, account, Java and version status; instances, downloads, accounts, servers and settings are grouped into clearly labelled sections. The servers page stores a reusable multiplayer list and launches the selected instance with `--server/--port`. Screenshots are kept under `docs/evidence/ui-redesign/`, `docs/evidence/m5/` and `docs/evidence/m6/`.

## Beta build

- A modal beta notice is shown on startup (version, risks, backup advice, links). A checkbox suppresses it for future launches, and the same toggle is available in Settings → 更新.
- The window title, header pill and footer show `内测版本`.

## One-click Java

Settings → Java 运行时 → 一键安装 Java supports Java 8/11/17/21/25/26/27 (LTS and current EA):

- Resolves the newest Windows x64 JDK zip from the TUNA Adoptium mirror (USTC fallback), then falls back to the Adoptium API for majors not yet mirrored (e.g. JDK 26/27).
- Downloads it through the segmented `DownloadQueue` (parallel HTTP Range requests).
- Extracts to `%LOCALAPPDATA%\AstraLauncher\runtime\jdk-<major>-<timestamp>` and points the launcher at the resulting `javaw.exe`.

## Cloud drive (Cloudreve)

The 云盘 page connects to a Cloudreve instance over WebDAV (`http://127.0.0.1:5212/dav/` by default):

- Login uses a Cloudreve **WebDAV account** (create one in Cloudreve: 左侧 WebDAV → 创建新账号). The account name is the Cloudreve email and the password is the generated WebDAV password; it is not the Cloudreve login password. The password stays in memory and is never written to disk.
- Four fixed slots (`astra-slot1.zip` … `astra-slot4.zip`) map to the four game-version slots per user.
- Upload builds a zip of `mods`, `config`, `saves`, `resourcepacks`, `shaderpacks`, `scripts`, `defaultconfigs`, `datapacks`, `options.txt`, `servers.dat` plus an `astra-instance.json` manifest, then PUTs it over WebDAV.
- Download fetches the zip, extracts it under the instances directory, reads the manifest and registers the restored folder as a local instance.

## Segmented downloads

The install pipeline, Java installer and Modrinth mod downloads all run through `DownloadQueue` with HTTP Range segmentation, resume and SHA-1 verification (Modrinth sends the expected SHA-1 and size).

## Importing other launchers

The instances page can adopt data created by PCL / HMCL / the official launcher:

- **扫描其他启动器** scans `versions/*` under the current Minecraft directory and `%APPDATA%\.minecraft`, lists version-isolation directories with their mod/save counts, and registers them as instances (path-only import, no files moved).
- **导入全局存档** copies worlds from the shared `.minecraft\saves` folder into the selected instance's `saves` directory (originals are kept; duplicate names receive an `-imported` suffix).
- Imported versions launch through the normal install pipeline, so missing libraries/assets are completed automatically.

## Mod loaders

The Downloads page has a loader card below the version list: choose a Minecraft version, then a loader (Fabric, Quilt, Forge, NeoForge), refresh its version list (with a recommended build preselected) and click **安装加载器**. Installed loader profiles appear in the instance version picker together with locally installed versions from `.minecraft/versions` (for example PCL profiles).

- **Fabric/Quilt** — the loader profile JSON is fetched from Fabric Meta / Quilt Meta, written as `versions/<loader>-<version>/…json`, and missing libraries/asset files are completed by the regular install pipeline. Libraries whose metadata only names a Maven repository URL are resolved through the repository fallback.
- **Forge** — the official installer jar is downloaded, run with a Java 21/17 runtime (`--installClient`), then the generated profile is verified/completed by the install pipeline. A minimal `launcher_profiles.json` is created first because the installer refuses to run without one.
- **NeoForge** — the installer jar and Maven artifacts are mirrored through BMCLAPI when `maven.neoforged.net` is unreachable; the embedded version profile is installed and its libraries are completed through the mirror-aware downloader. **Known limitation:** launching a NeoForge 1.21.1 profile currently fails inside NeoForge's BootstrapLauncher (`NoSuchElementException` while building its service module layer), while installation itself succeeds. Fabric/Quilt/Forge launching is fully verified.

Loader metadata is merged with the vanilla parent profile; library deduplication keeps classifier (natives) variants, which is required for inherited loader launches (a regression test covers this).

## Mods

The Mods page manages the selected instance's `mods` directory:

- **Local jars** — import one or more `.jar` files (file picker), list installed mods with name/version/platform/id, enable or disable them (renames between `.jar` and `.jar.disabled`), delete them, or open the folder. Fabric, Quilt, Forge and NeoForge metadata inside the jar (`fabric.mod.json`, `quilt.mod.json`, `META-INF/mods.toml`) is parsed, including declared dependencies.
- **Modrinth** — search the public Modrinth API with the instance's Minecraft version and loader automatically detected from its version profile (inheritance chains are followed), pick a compatible version and install its primary file into the instance's mods directory. Downloads are written to a temporary `.part` file and verified against the expected SHA-1 before being moved into place.
- Verified end-to-end on a real Fabric 1.21.1 instance: local import/enable/disable, live Modrinth search (48 results for "fabric api"), and installation of a small mod (`thegodgenutility-0.1.jar`, 66,230 bytes) into the instance mods folder.

## Servers

The Servers page keeps a saved multiplayer server list (`servers.json` in the launcher-data directory, separate from the Minecraft files). A server has a name, host and optional port; the address field also accepts `host:port`, pasted URLs and bracketed IPv6 addresses. **启动并加入** launches the instance currently selected on the Instances page and appends the vanilla quick-join arguments `--server <host>` and, when a port is set, `--port <port>`, so the game connects directly and skips the multiplayer menu. The list can be refreshed, added to, edited and deleted from the same page; the join progress and game log are shown on the Instances page. A read-only `servers.dat` import is a possible future addition and is not implemented yet. This flow was verified end-to-end on a real 1.21.1 instance in `F:\MC`: the launcher saved a server, launched 1.21.1 with `--server/--port`, and the game opened and exited with code 0.

## Crash analysis

After a game process exits (including exit code 0 when the game showed a crash screen), the launcher analyzes the instance's `logs/latest.log` and newest `crash-reports/*.txt`:

- **Detected causes** — missing mandatory dependencies (Forge/NeoForge `Mod ID: 'x', Requested by: 'y', Actual version: '[MISSING]'` and Fabric missing-mod lines), duplicate mods, mods listed under `Suspected Mods`, Java version mismatches, `OutOfMemoryError`, graphics/OpenGL initialization failures and loader/Mixin errors.
- **Dialog** — shows a PCL-style summary with per-issue details and suggestions, plus buttons **自动修复 / 手动处理 / 查看日志/崩溃报告**. Nothing is modified until the user chooses automatic repair.
- **Reversible auto-repair** — installs a missing dependency from Modrinth (when a compatible project/version exists), disables suspected or duplicate mods by renaming them to `.jar.disabled`, and raises the configured memory by 1 GiB for out-of-memory crashes. Each action is listed as success/failure; disabled mods can be re-enabled, downloaded mods deleted, and memory changed back in Settings. Java/driver problems are reported as manual suggestions only.
- Verified against a real crashed Forge 1.20.1 modpack (`changed&机械动力`): the analyzer identified the missing `create` dependency required by `create_dragons_plus` and `create_hypertube`, showed the dialog, and the Manual choice preserved the files.

## Auto-update

- **Startup check** — when `AutoCheckUpdates` is enabled (default), the launcher queries GitHub Releases (`/releases/latest`) in the background after the window opens. Drafts and pre-releases are ignored.
- **Manual check** — Settings → 更新 → 立即检查更新.
- **Prompt** — a newer tag shows a confirmation dialog; accepting opens a topmost progress window while the release asset downloads.
- **Verification** — the download must match the asset size and, when GitHub supplies a `sha256:` digest, its SHA-256 before installation.
- **Self-replace and restart** — a detached PowerShell script waits for the launcher process to exit, stages the new executable, swaps it with `.old` backup, starts the new build and removes the backup. In-flight games are independent processes and are not affected.
- **Bootstrap note** — only builds that already contain this updater can auto-update. The first release containing it must be installed manually once; every later release can then be installed in-place.

## Project layout

- `AstraLauncher.App` — WPF entry point, DI composition root, MVVM shell/navigation, settings and instance/download pages.
- `AstraLauncher.Core` — dependency-free domain records and service contracts.
- `AstraLauncher.Infrastructure` — launcher paths, JSON settings, file logging, version manifest and download source resolution.
- `AstraLauncher.Game` — version metadata parsing/inheritance, install planning, safe local instance management, Java discovery, command construction, process launching and offline profiles.
- `AstraLauncher.Downloader` — asynchronous download queue with resume, retries, integrity checks and per-file HTTP Range segmented downloads.
- `AstraLauncher.Authentication` — device-code Microsoft OAuth, Xbox Live/XSTS, Minecraft services login, DPAPI token protection and the account service.
- `AstraLauncher.Multiplayer` — reserved for later signalling/relay abstractions.
- `AstraLauncher.Tests` — xUnit automated tests (295 currently passing).
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
