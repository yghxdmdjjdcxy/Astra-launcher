# Astra Launcher

Windows Minecraft Java Edition launcher, built in verified stages. This repository currently completes **Stage 0 through Stage 2**; it does not download, authenticate, validate ownership, or launch Minecraft yet.

## Requirements

- Windows 10 or later
- .NET SDK 10.0.100 or a compatible .NET 10 SDK

## Build and run

```powershell
dotnet restore AstraLauncher.sln
dotnet build AstraLauncher.sln --configuration Release --no-restore
dotnet run --project .\AstraLauncher.App\AstraLauncher.App.csproj
dotnet test .\AstraLauncher.Tests\AstraLauncher.Tests.csproj --configuration Release
```

## Project layout

- `AstraLauncher.App` — WPF entry point, dependency injection composition root, MVVM shell/navigation, exception handling.
- `AstraLauncher.Core` — dependency-free domain records and service contracts.
- `AstraLauncher.Infrastructure` — application directories and file logging; later stages add network, SQLite, and secure storage implementations.
- `AstraLauncher.Game` — version metadata parsing/inheritance, Java discovery, and safe local instance management. Library downloading and process launching remain later stages.
- `AstraLauncher.Downloader` — reserved for the real asynchronous download engine (Stage 3).
- `AstraLauncher.Authentication` — reserved for official Microsoft/Xbox/Minecraft authentication and offline profiles (Stage 4).
- `AstraLauncher.Multiplayer` — reserved for servers and later signalling/relay abstractions (Stage 7).
- `AstraLauncher.Tests` — xUnit automated tests.

Runtime log files are written beneath `%LOCALAPPDATA%\AstraLauncher\logs`. The launcher intentionally has no Microsoft account form and makes no claim of Minecraft authorization at this stage.
