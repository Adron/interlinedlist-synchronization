# Next Steps — Post-Phase 8

Phases 1–8 have shipped. The Windows app now has:

- A working sync engine with bidirectional push/pull, conflict copies,
  rate-limit backoff, network-aware pause/resume, and auth-expired pause.
- 165 unit tests + 5 (skipped, live) integration tests, all green.
- A four-tab Settings window (General, Account, Notifications, Advanced)
  backed by `SettingsViewModel`, an HKCU-Run `RegistryAutoStartManager`, a
  JSON-backed `AccountStore` for the signed-in email, and a state-reset
  affordance plumbed through `ISyncStateRepository.ResetAsync`.
- An MSIX packaging project (`InterlinedSync.Package.wapproj`) that produces
  a `.msix` from CI, with conditional `signtool` signing wired behind two
  GitHub Actions secrets.

## Phase 7 — done

- `Storage/IAutoStartManager` + `RegistryAutoStartManager` (HKCU Run, no UAC)
  + `InMemoryAutoStartManager` (non-Windows / test). The manager is decoupled
  from the registry via `IAutoStartRegistryGateway` so the unit tests in
  `AutoStartManagerTests` never touch HKCU.
- `Storage/IAccountStore` + `AccountStore` — JSON file under
  `%APPDATA%\interlinedlist-sync\account.json` for the signed-in email.
- `Sync/ISyncStateRepository.ResetAsync` + matching SQL.
- `UI/ViewModels/SettingsViewModel` rebuilt around the four tabs: General
  (folder browse + AutoStart + interval slider), Account (signed-in email +
  Sign Out), Notifications (master + three sub-toggles wired into existing
  `NotifyOn*` prefs), Advanced (log/sync/prefs-folder buttons + Reset state
  + version display).
- `UI/Views/SettingsWindow.xaml` rewritten as a `TabControl` (480×360,
  resizable). Folder browse uses `Microsoft.Win32.OpenFolderDialog`
  (.NET 8+). Reset state shows a confirmation `MessageBox`.
- `UI/Views/OnboardingWindow.xaml.cs` now prompts for the sync folder on
  first sign-in via the same `OpenFolderDialog` and persists it through
  `IPreferencesStore`.
- `Program.cs` registers `IAccountStore` always; `IAutoStartManager` is
  `RegistryAutoStartManager` on Windows, `InMemoryAutoStartManager`
  otherwise.
- `SyncEngine.CurrentPollInterval` exposed so the hot-reload behaviour
  (existing `IOptionsMonitor<SyncPreferences>` plumbing) is testable.
- 19 new unit tests across `SettingsViewModelTests`, `OnboardingViewModelTests`,
  `AutoStartManagerTests`, `AccountStoreTests`, `SyncEngineTests`,
  `SyncStateRepositoryTests` (146 → 165 green).

## Phase 8 — done (modulo signing cert)

- `InterlinedSync.Package/InterlinedSync.Package.wapproj` scaffolded as a
  Desktop Bridge / MSIX project referencing the WPF host with a `win-x64`
  publish profile.
- `Package.appxmanifest` updated with a placeholder publisher and an
  explanatory comment; manifest must be updated to match the signing cert's
  subject before the first signed release.
- `.github/workflows/release.yml` Windows job extended to:
  - Set up MSBuild.
  - Build the `.wapproj` (`UapAppxPackageBuildMode=SideloadOnly`,
    signing disabled at MSBuild level — we sign as a separate step).
  - Locate the produced `.msix` and copy it as
    `InterlinedSync-Windows-MSIX-<tag>.msix`.
  - Run `signtool sign` against it conditionally on
    `secrets.WINDOWS_CERT_PFX_BASE64` / `secrets.WINDOWS_CERT_PASSWORD`.
  - Upload both the zip and the MSIX under the `windows-release` artifact.
- `windows/PACKAGING.md` documents the secrets, the local test workflow
  (`Add-AppxPackage`), and the manifest/publisher constraints.

## v1.0 readiness gates

1. **Code-signing certificate.** Provision an EV (or at minimum OV) cert,
   populate `WINDOWS_CERT_PFX_BASE64` + `WINDOWS_CERT_PASSWORD` in repo
   secrets, then update the `Publisher="..."` in
   `InterlinedSync.Package/Package.appxmanifest` to exactly match the cert
   `Subject`. Until both are in place the CI MSIX is unsigned and will
   refuse to install without developer mode.
2. **Final API endpoints.** Confirm `/api/auth/sync-token` (or the
   replacement login endpoint) and the documents / delta paths with the
   server team; flip the 5 skipped integration tests on against a sandbox
   host by setting `INTERLINEDSYNC_API_BASE`, `_USERNAME`, `_PASSWORD`.
3. **Toast notifications.** The Windows App SDK
   `Microsoft.Windows.AppNotifications` integration is still stubbed by
   `LoggingNotificationDispatcher`. Once MSIX is the primary distribution
   channel, swap in the real dispatcher and wire
   `AppNotificationManager.Default.NotificationInvoked` to the existing
   `ITrayCommandHandler` (Open Folder / Sign In / Open Log).
4. **Store assets.** Replace the placeholder PNGs under
   `InterlinedSync.Package/Assets/` (Square44, Square150, Wide310, Store
   logo) with final artwork before the first public install.
5. **Smoke test on real Windows hardware** — UI tests are still WinAppDriver
   territory (Phase 9 candidate). Manually verify: settings tabs render,
   Browse… opens the folder picker, Sign Out returns to OnboardingWindow,
   Reset state empties the SQLite DB, and the MSIX-installed build picks up
   the `startupTask` extension instead of writing to HKCU.

## Phase 9 candidates (out of v1.0)

- WinAppDriver-based UI smoke tests (`InterlinedSync.UITests`).
- Microsoft Store submission pipeline (replaces EV cert with Store-issued
  signing).
- Localization scaffolding (move strings into `.resw`).
- Telemetry pipeline (optional, opt-in only — never on by default).
- Optional offline-aware backoff on the push channel so the queue does not
  grow unbounded while paused.
