# InterlinedList Sync — Installer (Inno Setup)

This folder holds `InterlinedSync.iss`, the [Inno Setup][inno] script that
produces `InterlinedListSync-Setup-<version>.exe`. The installer is the
preferred distribution channel for Windows; the older `.zip` artifact remains
available as an alternative for users who want to xcopy-deploy.

[inno]: https://jrsoftware.org/isinfo.php

---

## Prerequisites

| Tool | Why | How to install |
|------|-----|----------------|
| .NET 9 SDK | To run `dotnet publish` and produce the framework-dependent build the installer copies. | <https://dotnet.microsoft.com/download/dotnet/9.0> |
| Inno Setup 6.x | Provides the `iscc.exe` compiler. | `choco install innosetup -y` (recommended), or download the installer from <https://jrsoftware.org/isdl.php>. |
| Optional: `signtool.exe` | For code-signing the installer. Ships with the Windows SDK. | <https://learn.microsoft.com/windows/win32/seccrypto/signtool> |

> The installer can only be built on a Windows host — Inno Setup does not
> have a macOS or Linux compiler. On non-Windows hosts the rest of this
> repository builds fine; only `iscc` is gated.

---

## Build steps (local)

From the repository root:

```powershell
# 1. Produce the framework-dependent publish output.
dotnet publish windows/InterlinedSync/InterlinedSync.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained false `
  --output windows/publish

# 2. Compile the installer.
iscc /Qp windows/installer/InterlinedSync.iss /DAppVersion=0.2.0

# 3. The signed (or unsigned) installer lands here:
ls windows/installer/Output/InterlinedListSync-Setup-0.2.0.exe
```

`/DAppVersion` is required for a release-quality build — the script defaults
to `0.0.0-dev` when omitted, which is fine for sanity checks but should never
hit a public release tag.

To embed the publish output from a non-default location, pass
`/DPublishDir=path` — the path is resolved relative to `InterlinedSync.iss`.

---

## Code-signing

Local-dev builds ship **unsigned**. To sign in CI:

1. Register a named SignTool with `iscc` BEFORE invoking the compile, e.g.
   `iscc /Ssigntool="signtool.exe sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 /f cert.pfx /p $p $f" ...`
2. Pass `/DSignToolConfigured=1` so the script's conditional `SignTool=`
   directive activates.

When `SignToolConfigured` is not defined the script emits an unsigned
installer rather than failing the build. The release workflow follows the
same pattern as the (deferred) MSIX scaffold — only signs when the
`WINDOWS_CERT_PFX_BASE64` + `WINDOWS_CERT_PASSWORD` secrets exist on the
runner. See `windows/PACKAGING.md` for cert-acquisition guidance.

---

## What the installer does

| Phase | Action |
|-------|--------|
| Welcome page | Standard Inno wizard introduction. |
| License page | Shows the repo-root `LICENSE`. |
| Install location | Default `{autopf}\InterlinedList Sync` (i.e. `%ProgramFiles%\InterlinedList Sync`). User can override. |
| Tasks page | Three checkboxes, all checked by default: `Create Start Menu shortcut`, `Start automatically when Windows starts`, `Launch InterlinedList Sync after installation` (the latter is the finish-page run entry, shown on the wizard's last page). |
| Install | Copies the published `.exe` + dependencies into the install dir; creates Start Menu shortcuts when the matching task is selected; writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\InterlinedSync` when the autostart task is selected. |
| Finish page | Optional "Launch InterlinedList Sync" run — passes `--from-installer` so the app skips its one-time autostart prompt this session. |

---

## What the uninstaller does — and DOES NOT do

Uninstall (via Add/Remove Programs or `unins000.exe` in the install
directory) removes:

- The application files in `{app}`.
- The Start Menu shortcut.
- The `HKCU\...\Run\InterlinedSync` registry value (the value only — other
  programs' Run entries are untouched).
- The empty `HKCU\Software\InterlinedSync` key, IF the
  StartupPromptSuppressed flag is its only child.

The uninstaller **does not** touch any of:

- The user's sync folder (default `%USERPROFILE%\Documents\InterlinedList`).
- Windows Credential Manager entries (`com.interlinedlist.sync`).
- The JSON preferences at `%APPDATA%\interlinedlist-sync\appsettings.json`.
- Serilog log files at `%LOCALAPPDATA%\interlinedlist-sync\logs\`.

Reinstalling picks all of that back up automatically. Users who want a true
clean slate can delete those directories manually after uninstalling.

---

## Manual smoke-test checklist

After every meaningful change to `InterlinedSync.iss` or to the app's
startup-prompt logic, run through the following on a clean Windows 10 or 11
VM:

1. **Fresh install — defaults.**
   - Double-click `InterlinedListSync-Setup-<v>.exe`.
   - Accept defaults through every page; leave all three Task checkboxes
     checked.
   - Confirm: `%ProgramFiles%\InterlinedList Sync\InterlinedSync.exe` exists;
     Start Menu has an `InterlinedList Sync` entry; HKCU Run key has an
     `InterlinedSync` value pointing at the installed exe; the app launched
     automatically and lives in the tray.
   - Sign out of Windows / sign back in. Confirm the app auto-starts.
2. **Fresh install — autostart unchecked.**
   - Repeat #1 but uncheck "Start automatically when Windows starts".
   - Confirm: HKCU Run key has NO `InterlinedSync` value.
   - Confirm: launching the app from the Start Menu shows the one-time
     "Run InterlinedList Sync automatically?" prompt.
   - Pick "Yes". Confirm: HKCU Run key now has the value.
   - Sign out / sign in. Confirm autostart works.
3. **Fresh install — "Don't ask again".**
   - Same as #2 but pick "Don't ask again" on the prompt.
   - Confirm: HKCU Run key has NO value; `HKCU\Software\InterlinedSync\StartupPromptSuppressed`
     is `1`.
   - Relaunch the app from the Start Menu. Confirm the prompt does NOT
     reappear.
4. **Upgrade install.**
   - With the app installed at version N, build version N+1 and run the new
     setup.
   - Confirm: install proceeds without uninstalling the old version (Inno
     handles in-place upgrade via `AppId`); credentials and the sync folder
     are preserved; the app launches with the new exe.
5. **Uninstall.**
   - Settings → Apps → InterlinedList Sync → Uninstall.
   - Confirm: install dir is gone; Start Menu shortcut is gone; HKCU Run
     value is gone; sync folder, Credential Manager entry, preferences JSON,
     and log files are still present.
6. **Code-signed installer (when cert is available).**
   - Confirm: SmartScreen does NOT show a yellow warning on first run.
   - Right-click the installer → Properties → Digital Signatures. Confirm
     the publisher matches the cert subject.
