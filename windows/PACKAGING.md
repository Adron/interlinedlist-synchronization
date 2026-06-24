# InterlinedList Sync — Windows Packaging

Two distribution channels are produced by `.github/workflows/release.yml`:

| Artifact | Purpose | Install command |
|----------|---------|-----------------|
| `InterlinedSync-Windows-<tag>.zip` | Framework-dependent xcopy build (requires .NET 9 Desktop Runtime pre-installed). | Unzip and run `InterlinedSync.exe`. |
| `InterlinedSync-Windows-MSIX-<tag>.msix` | MSIX package built by `InterlinedSync.Package.wapproj`. Honours the manifest `startupTask` so auto-start is registered via the Task Scheduler bridge when packaged. | `Add-AppxPackage -Path InterlinedSync-Windows-MSIX-<tag>.msix` |

## GitHub Actions secrets

| Secret | Required for | How to obtain |
|--------|--------------|---------------|
| `WINDOWS_CERT_PFX_BASE64` | MSIX code signing in CI | Base64-encode your `.pfx`: `[Convert]::ToBase64String([IO.File]::ReadAllBytes("cert.pfx"))` and paste into the repo secret. |
| `WINDOWS_CERT_PASSWORD` | MSIX code signing in CI | Password protecting the `.pfx`. |

When both secrets are set, the workflow's "Sign MSIX (if cert available)" step runs `signtool sign` with SHA-256 + RFC 3161 timestamp. When either is absent, the step is skipped and an **unsigned** `.msix` is uploaded — useful for internal testing but it will refuse to install on stock Windows unless the host has developer mode enabled or a sideload licence applied.

### Acquiring a code-signing certificate

Production releases need an EV (Extended Validation) code-signing certificate from a public CA (DigiCert, Sectigo, GlobalSign). EV certs:

- Suppress the SmartScreen warning immediately on first install (non-EV needs reputation to accrue first).
- Cost ~US$300–500/year.
- Are usually issued on a hardware token; you'll need to export the `.pfx` (or use Azure Key Vault + `AzureSignTool`) to consume them in CI.

For internal testing only, a self-signed cert created via `New-SelfSignedCertificate` works — but every test machine must trust that cert first (`Import-Certificate -CertStoreLocation Cert:\LocalMachine\TrustedPeople`).

### Updating the manifest after signing

`InterlinedSync.Package/Package.appxmanifest` currently ships with `Publisher="CN=InterlinedSync (UNSIGNED PLACEHOLDER)"`. The `Publisher` attribute **must exactly match** the `Subject` of the signing certificate, otherwise `signtool sign` will refuse to attach the signature to the MSIX. Update the line before the first signed build, e.g.:

```xml
<Identity Name="com.interlinedlist.Sync"
          Publisher="CN=InterlinedList Inc, O=InterlinedList Inc, L=Seattle, S=Washington, C=US"
          Version="1.0.0.0" />
```

## Testing the MSIX locally

```powershell
# Build
cd windows
msbuild InterlinedSync.Package\InterlinedSync.Package.wapproj `
  /p:Configuration=Release /p:Platform=x64 `
  /p:UapAppxPackageBuildMode=SideloadOnly /p:AppxPackageSigningEnabled=false

# The output is at:
#   windows\InterlinedSync.Package\bin\x64\Release\AppPackages\...\*.msix

# Install (requires developer mode or a sideload licence for unsigned packages):
Add-AppxPackage -Path .\InterlinedSync.Package\bin\x64\Release\AppPackages\<...>.msix

# Uninstall:
Get-AppxPackage com.interlinedlist.Sync | Remove-AppxPackage
```

Enabling developer mode: **Settings → Privacy & security → For developers → Developer Mode**. This is required for any unsigned MSIX install on Windows 10/11.

## MSIX requirements and constraints

- `Package.appxmanifest` declares `runFullTrust` (the WPF host needs arbitrary filesystem access for the user-chosen sync folder) and `internetClient` (HTTPS to interlinedlist.com).
- `TargetDeviceFamily` is `Windows.Desktop` with `MinVersion="10.0.19041.0"` (Win10 2004) — matches the .NET TFM.
- `Resources` is `x-generate` — no localization yet; English-only.
- The `startupTask` extension registers the app for auto-start via Task Scheduler when packaged. The unpackaged `.exe` build cannot use this extension and falls back to the HKCU `Run` key (see `RegistryAutoStartManager`).

## Open items before first signed release

- [ ] Replace `Publisher` placeholder with the real cert subject (see above).
- [ ] Bump `Version="0.1.0.0"` to match the git tag.
- [ ] Add Store assets at the correct sizes — currently `Assets\StoreLogo.png` and friends are placeholders; replace with the final artwork (Square44x44, Square150x150, Wide310x150, StoreLogo at 50×50).
- [ ] Decide between sideloaded MSIX (current) and Microsoft Store submission. The latter unlocks Auto-updates via the Store and removes the EV-cert requirement (Store certs are issued for free per submission).
