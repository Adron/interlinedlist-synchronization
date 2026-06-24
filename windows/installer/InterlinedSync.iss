; ============================================================================
; InterlinedList Sync — Inno Setup script
; ----------------------------------------------------------------------------
; Builds InterlinedListSync-Setup-<version>.exe from the framework-dependent
; publish output produced by:
;
;   dotnet publish windows/InterlinedSync/InterlinedSync.csproj \
;     --configuration Release --runtime win-x64 --self-contained false \
;     --output windows/publish
;
; The release pipeline already produces that publish/ directory upstream of
; the installer step; locally, run the dotnet publish command above before
; invoking iscc.
;
; Build command (Windows host with Inno Setup installed):
;
;   iscc /Qp windows/installer/InterlinedSync.iss /DAppVersion=0.2.0
;
; Output:
;
;   windows/installer/Output/InterlinedListSync-Setup-0.2.0.exe
; ============================================================================

; ---------------------------------------------------------------------------
; AppVersion — supplied by CI via /DAppVersion=<tag>. Falls back to a clearly
; fake placeholder so local-dev builds without /D still produce SOMETHING.
; ---------------------------------------------------------------------------
#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif

#define AppName        "InterlinedList Sync"
#define AppPublisher   "InterlinedList"
#define AppURL         "https://interlinedlist.com"
#define AppExeName     "InterlinedSync.exe"
; Fixed AppId — DO NOT regenerate, this identifies upgrades in place.
#define AppId          "{{B7D4E9F8-3C2A-4F1D-9A6E-1B5C8D0F2E7A}"

; ---------------------------------------------------------------------------
; PublishDir — where dotnet publish puts the framework-dependent output. CI
; passes this via /DPublishDir=path; locally, default to ..\publish so the
; standard `dotnet publish --output windows/publish` lands the right files.
; ---------------------------------------------------------------------------
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\InterlinedList Sync
DefaultGroupName=InterlinedList Sync
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=InterlinedListSync-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; LICENSE lives at the repo root; the script is at windows/installer so
; ..\..\LICENSE points at it. Wizard shows the standard "I accept" page.
LicenseFile=..\..\LICENSE
; Only 64-bit Windows is supported; .NET 9 desktop runtime requires x64/arm64
; and the framework-dependent publish targets win-x64.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Per-user install would let us avoid UAC but Start Menu shortcuts and Program
; Files placement match user expectations for a "system tray app". A standard
; UAC prompt at install time is fine.
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
; Embed publisher info into the installer EXE itself for SmartScreen reputation
; once the build is signed.
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

; ---------------------------------------------------------------------------
; Optional code-signing.
;
; Inno Setup invokes `signtool` via a NAMED SignTool directive. CI is expected
; to register that named tool BEFORE invoking iscc, e.g.:
;
;   iscc /Qp /Ssigntool=$qC:\Tools\signtool.exe$q sign /fd sha256 /tr ...
;           /td sha256 /f cert.pfx /p $p $f windows/installer/InterlinedSync.iss
;
; (or via the `Inno-Setup-Tool` registry under Tools/Configure Sign Tools in
; the IDE). The SignTool= line below activates signing IFF a tool named
; "signtool" has been registered for this iscc invocation. When absent — i.e.
; in local dev or unsigned CI builds — Inno emits a warning and ships an
; unsigned installer rather than failing the build.
;
; To sign both the embedded EXE and the installer itself add:
;   SignedUninstaller=yes
; and ensure the runner has the signtool registered. We leave SignedUninstaller
; off by default so the local-dev path works without configuration.
; ---------------------------------------------------------------------------
#ifdef SignToolConfigured
SignTool=signtool
SignedUninstaller=yes
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; ---------------------------------------------------------------------------
; Wizard checkboxes. Order in the [Tasks] section is the order shown to the
; user. All three default to checked per the workstream spec.
; ---------------------------------------------------------------------------
Name: "startmenu";  Description: "Create Start Menu shortcut";                Flags: checkablealone
Name: "autostart";  Description: "Start automatically when Windows starts";   Flags: checkablealone

[Files]
; Copy every file under the published output. `recursesubdirs` + the wildcard
; brings the entire tree (DLLs, runtimes, assets) into {app}.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Start Menu shortcut — only when the user keeps the matching task checked.
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: startmenu
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"; Tasks: startmenu

[Registry]
; ---------------------------------------------------------------------------
; Auto-start registration. Conditional on the `autostart` task. We write to
; HKCU rather than HKLM so the entry applies to the user who ran the
; installer; `uninsdeletevalue` removes ONLY our named value on uninstall
; (other apps' Run entries are untouched). `uninsdeletekeyifempty` cleans up
; the InterlinedSync preferences key if the suppressed-flag is the only
; child left.
; ---------------------------------------------------------------------------
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "InterlinedSync"; \
  ValueData: """{app}\{#AppExeName}"""; \
  Tasks: autostart; Flags: uninsdeletevalue

; Preferences key — created empty here so the uninstaller knows to clean it
; up if empty after our values are removed. The app's
; StartupPromptSuppressed flag (when set) lives under this key.
Root: HKCU; Subkey: "Software\InterlinedSync"; Flags: uninsdeletekeyifempty

[Run]
; ---------------------------------------------------------------------------
; Finish-page "Launch InterlinedList Sync" checkbox. Checked by default per
; spec. The `--from-installer` argument tells the app to skip its one-time
; startup prompt on this very first launch (the wizard already covered that
; question).
; ---------------------------------------------------------------------------
Filename: "{app}\{#AppExeName}"; \
  Parameters: "--from-installer"; \
  Description: "Launch {#AppName}"; \
  Flags: nowait postinstall skipifsilent

[UninstallDelete]
; ---------------------------------------------------------------------------
; USER DATA IS PRESERVED ON UNINSTALL.
;
; The uninstaller intentionally does NOT touch:
;   * the user's chosen sync folder (default %USERPROFILE%\Documents\InterlinedList)
;   * Windows Credential Manager entries (PasswordVault, resource
;     "com.interlinedlist.sync")
;   * the JSON preferences at %APPDATA%\interlinedlist-sync\appsettings.json
;   * Serilog log files at %LOCALAPPDATA%\interlinedlist-sync\logs\
;
; A reinstall picks all of this back up automatically. A user who actually
; wants to wipe state can do so manually; making it the uninstaller's job is
; surprising and destructive.
;
; Only the application install directory is touched on uninstall (handled
; automatically by Inno from the [Files] section).
; ---------------------------------------------------------------------------
