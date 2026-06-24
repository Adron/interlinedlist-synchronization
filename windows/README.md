# InterlinedList Sync — Windows

A native Windows notification-area (system tray) application that bidirectionally syncs your
[InterlinedList](https://interlinedlist.com) documents to a local folder.
It runs as a background process, surfaces controls through a tray context menu, and sends
toast notifications for sync events.

**Minimum requirement:** Windows 10 build 19041 (version 2004)

**Stack:** C# 13 / .NET 9, WPF, `Hardcodet.Wpf.TaskbarNotification` (tray icon),
Windows App SDK (toasts), `System.IO.FileSystemWatcher` (file watching),
`System.Net.Http.HttpClient` (API), Windows Credential Manager (token storage),
SQLite (sync state)

## Install

> **Note:** Signed, distributable installers are not yet available. The GitHub Releases page will
> list an MSIX package and a framework-dependent ZIP when v1.0 ships.

When releases are available, download `InterlinedSync-Windows-MSIX-<tag>.msix` from the
[Releases](https://github.com/Adron/interlinedlist-synchronization/releases) page and run:

```powershell
Add-AppxPackage -Path .\InterlinedSync-Windows-MSIX-<tag>.msix
```

A framework-dependent ZIP (`InterlinedSync-Windows-<tag>.zip`) is also available for users
who prefer xcopy deployment (requires the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)
pre-installed).

<details>
<summary>SmartScreen warning for unsigned builds</summary>

Unsigned MSIX packages trigger a SmartScreen warning. To install an unsigned package, enable
**Developer Mode** in **Settings → Privacy & security → For developers → Developer Mode**,
then re-run the `Add-AppxPackage` command.

Signed packages from the Releases page with an EV code-signing certificate do not show this
warning.

</details>

<details>
<summary>.NET 9 Desktop Runtime requirement (ZIP build only)</summary>

The ZIP (framework-dependent) build does not bundle the .NET runtime.
Download and install the
[.NET 9 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)
before launching `InterlinedSync.exe`. The MSIX build is self-contained and does not have
this requirement.

</details>

## Build from source

**Prerequisites:** .NET 9 SDK, Visual Studio 2022 with the **.NET desktop development** workload
(or JetBrains Rider).

```bash
# Clone the repo
git clone https://github.com/Adron/interlinedlist-synchronization.git
cd interlinedlist-synchronization

# Build (Release configuration)
dotnet build windows/InterlinedSync.sln -c Release

# Run
dotnet run --project windows/InterlinedSync -c Release
```

To build the MSIX package locally, see [PACKAGING.md](PACKAGING.md).

## First run

1. Launch **InterlinedSync.exe** (or install via MSIX).
2. An onboarding window opens. Enter your InterlinedList email and password and click **Sign In**.
3. Choose a local folder to sync into (default: `%USERPROFILE%\InterlinedList Sync`).
4. The tray icon appears in the notification area. The initial sync begins automatically.

## Where files are stored

| What | Location |
|------|----------|
| Auth token | Windows Credential Manager (`PasswordVault`, target `InterlinedSync`) |
| Preferences (`appsettings.json`) | `%APPDATA%\interlinedlist-sync\appsettings.json` |
| Sync state database | `%APPDATA%\interlinedlist-sync\state.db` (SQLite) |
| Application logs | `%LOCALAPPDATA%\interlinedlist-sync\logs\` (rolling Serilog files) |

## Running tests

```bash
cd windows
dotnet test InterlinedSync.sln
```

Integration tests against the live API require a `.env` file at the repo root — see
[CONTRIBUTING.md](../CONTRIBUTING.md) for setup. Run the integration project directly:

```bash
dotnet test windows/InterlinedSync.IntegrationTests
```

## Troubleshooting

**The tray icon does not appear after launch.**
Check `%LOCALAPPDATA%\interlinedlist-sync\logs\` for error entries. If the process started but the
icon is hidden, click the **Show hidden icons** chevron in the notification area.

**"Sign In" fails with an authentication error.**
Verify your email and password work at [interlinedlist.com](https://interlinedlist.com).
The app uses `POST /api/auth/sync-token` — not your browser session.

**MSIX will not install — "The app you're trying to install isn't a Microsoft-verified app."**
The package is unsigned. Enable Developer Mode (see "SmartScreen warning" above) or wait for
a signed release.

**Documents are not syncing.**
Right-click the tray icon and check the status tooltip. If it shows an error, open **Settings**
for detail. Review the log files in `%LOCALAPPDATA%\interlinedlist-sync\logs\`.

**Conflict copies accumulate in the sync folder.**
Files named `<name>.conflict-<timestamp>.md` were created because both your local copy and the
server copy changed before the next sync cycle. Review them and delete the copies you do not need.

**Auto-start does not work after an xcopy (ZIP) install.**
The MSIX `startupTask` extension (which registers auto-start via Task Scheduler) is only available
in the packaged MSIX build. The ZIP build falls back to writing a `HKCU\Software\Microsoft\Windows\
CurrentVersion\Run` registry value. If that toggle is not available in the ZIP build's Settings
window, auto-start is not supported in your current install format — use the MSIX.
