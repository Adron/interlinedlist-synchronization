# InterlinedList Sync — Windows Packaging

The primary Windows distribution channel is now an **Inno Setup installer**
(`InterlinedListSync-Setup-<tag>.exe`). MSIX remains supported in the
project files but is deferred from CI until a runner with the matching UAP
SDK is available — see "MSIX status" near the bottom.

## Artifacts produced by `.github/workflows/release.yml`

| Artifact | Purpose | Install command |
|----------|---------|-----------------|
| `InterlinedListSync-Setup-<tag>.exe` | **Primary.** Inno Setup installer. Adds Start Menu shortcut, optional auto-start via HKCU Run, finish-page launch checkbox, registered uninstaller. Self-contained — bundles the .NET 9 runtime, no separate install required. | Double-click and follow the wizard. |
| `InterlinedSync-Windows-<tag>.zip` | **Secondary.** Self-contained xcopy build for users who prefer no installer. | Unzip and run `InterlinedSync.exe`. |

Both artifacts are **self-contained** (`dotnet publish --self-contained
true`): the entire .NET 9 runtime ships inside the publish directory so the
app launches on machines that do not have the .NET Desktop Runtime
installed. This trades a substantially larger artifact (~100–150 MB) for
a true double-click-to-run experience. Earlier framework-dependent builds
silently no-op'd for users who never installed the runtime separately —
the self-contained build closes that hole.

## Building locally

See `windows/installer/README.md` for the full local build flow. The short
version:

```powershell
# From the repository root.
dotnet publish windows/InterlinedSync/InterlinedSync.csproj `
  -c Release -r win-x64 --self-contained true -o windows/publish
iscc /Qp windows/installer/InterlinedSync.iss /DAppVersion=0.2.0
```

The output is at `windows/installer/Output/InterlinedListSync-Setup-0.2.0.exe`.

## Installer behavior summary

| Page | Default | Effect |
|------|---------|--------|
| Tasks: Create Start Menu shortcut | checked | Adds `Start Menu → InterlinedList Sync` shortcut. |
| Tasks: Start automatically when Windows starts | checked | Writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\InterlinedSync` pointing at the installed exe. |
| Finish: Launch InterlinedList Sync | checked | Launches the app with `--from-installer` so the app's one-time autostart prompt is skipped on this very first run. |

The app's own one-time startup prompt (`StartupPromptDialog`) appears on
*subsequent* launches when the autostart task was unchecked, asking the user
if they'd like to opt in after all. Picking "Don't ask again" writes
`HKCU\Software\InterlinedSync\StartupPromptSuppressed = 1` so the prompt
stays hidden.

## Uninstall

- Removes the application files.
- Removes the Start Menu shortcut.
- Removes ONLY the installer's `HKCU\...\Run\InterlinedSync` value (other
  apps' Run entries are not touched).
- **PRESERVES** the user's sync folder, Windows Credential Manager entries,
  `appsettings.json`, and log files. Reinstalling picks them back up.

## Code-signing (CI)

Same secrets as the MSIX scaffold:

| Secret | Required for | How to obtain |
|--------|--------------|---------------|
| `WINDOWS_CERT_PFX_BASE64` | Signing the installer EXE in CI. | Base64-encode your `.pfx`: `[Convert]::ToBase64String([IO.File]::ReadAllBytes("cert.pfx"))`. |
| `WINDOWS_CERT_PASSWORD` | Password for the `.pfx`. | — |

When both secrets are present, the release workflow's "Sign installer (if
cert available)" step runs `signtool sign` with SHA-256 + RFC 3161
timestamp on the installer EXE. When either is absent, the step is skipped
and an **unsigned** installer ships — fine for internal testing but
SmartScreen will warn on first install until the cert is in place.

### Acquiring a code-signing certificate

Production releases need an EV (Extended Validation) code-signing
certificate from a public CA (DigiCert, Sectigo, GlobalSign). EV certs:

- Suppress the SmartScreen warning immediately on first install (non-EV
  needs reputation to accrue first).
- Cost ~US$300–500/year.
- Are usually issued on a hardware token; you'll need to export the `.pfx`
  (or use Azure Key Vault + `AzureSignTool`) to consume them in CI.

For internal testing only, a self-signed cert created via
`New-SelfSignedCertificate` works — but every test machine must trust that
cert first (`Import-Certificate -CertStoreLocation Cert:\LocalMachine\TrustedPeople`).

## MSIX status (deferred)

`InterlinedSync.Package/` still holds the MSIX wapproj for a future
revival. It is not built in CI today because the `windows-latest` runner's
Windows SDK does not include UAP.props for our target version (APPX3217
across multiple version values).

Revive it by either:

- Standardizing on a self-hosted runner with the matching SDK installed, or
- Switching to a Microsoft Store submission so the runtime is provisioned
  via the Store (which sidesteps the EV-cert requirement too).

When that happens, both the `.exe` installer and the `.msix` package can
ship side-by-side; users on Windows 10 1903+ can pick whichever they
prefer.

### Manifest publisher (for the eventual MSIX revival)

`InterlinedSync.Package/Package.appxmanifest` currently ships with
`Publisher="CN=InterlinedSync (UNSIGNED PLACEHOLDER)"`. The `Publisher`
attribute **must exactly match** the `Subject` of the signing certificate.
Update the line before the first signed MSIX build, e.g.:

```xml
<Identity Name="com.interlinedlist.Sync"
          Publisher="CN=InterlinedList Inc, O=InterlinedList Inc, L=Seattle, S=Washington, C=US"
          Version="1.0.0.0" />
```

## Open items before first signed release

- [ ] Procure the EV code-signing certificate and load it into
  `WINDOWS_CERT_PFX_BASE64` / `WINDOWS_CERT_PASSWORD` secrets.
- [ ] Run through the manual smoke-test checklist in
  `windows/installer/README.md`.
- [ ] (Deferred) Replace MSIX `Publisher` placeholder + bump the package
  version + ship Store-quality assets.
