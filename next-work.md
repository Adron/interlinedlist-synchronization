# Next Work — Post-v0.1.1-alpha Capabilities

Three workstreams identified after the alpha shipped. Each is parallel-friendly via per-platform engineering sub-agents.

**Created:** 2026-06-24 (after v0.1.1-alpha shipped)
**Target release:** v0.2.0

---

## Workstream A — Linux `.deb` silent-failure diagnostics + logging

### Problem
Installing the `.deb` succeeds but the app then "silently fails" — no clear log output, no actionable error, no obvious place to look. Diagnosing is currently hard because:

- The systemd user unit may not enable/start correctly post-install
- Daemon likely exits early when no `config.toml` exists, with no log capture
- `tracing` output goes nowhere by default (no `tracing-subscriber` log file)
- `postinst` doesn't tell the user what to do next or where logs live
- No `interlinedlist-sync --status` style command for self-diagnosis

### Proposed solution

1. **File-based logging via `tracing-subscriber` rolling appender**
   - Log to `~/.local/share/interlinedlist-sync/logs/interlinedlist-sync.log` with daily rotation (keep 7 days)
   - Log level controlled by `RUST_LOG` env var (default `info`)
   - Format: structured JSON or compact text — choose compact for human grep-ability
   - Initialize the logger BEFORE config loading so config-load failures are captured
   - Print log path to stderr on startup so users can find it

2. **`--status` subcommand**
   - `interlinedlist-sync --status` prints: config path + whether it exists, last sync time from state DB, log file path + last 10 lines, secret-store backend in use + whether a token is present (boolean, never the value), systemd unit state if running under systemd
   - Useful for the user, useful for bug reports
   - Exit code 0 if everything's fine, 1 if anything's missing

3. **systemd user unit improvements**
   - `StandardOutput=journal` and `StandardError=journal` so `journalctl --user -u interlinedlist-sync` works
   - `Restart=on-failure` with `RestartSec=10s` and `StartLimitBurst=3` so transient failures don't loop
   - `After=network-online.target` so the daemon doesn't start before networking is up
   - Add a `Documentation=` line pointing to the local README and the GitHub URL

4. **`postinst` script — make next steps obvious**
   - After install, print to stdout:
     ```
     InterlinedList Sync installed.
     
     Next steps:
       1) Configure the sync folder + API base URL:
            $EDITOR ~/.config/interlinedlist-sync/config.toml
            (the daemon will create a default file on first run)
       2) Store your account credentials:
            interlinedlist-sync --login
       3) Enable + start the user service:
            systemctl --user enable --now interlinedlist-sync.service
     
     Status + logs:
       interlinedlist-sync --status
       journalctl --user -u interlinedlist-sync
       tail -F ~/.local/share/interlinedlist-sync/logs/interlinedlist-sync.log
     ```
   - Do NOT auto-enable the systemd unit in `postinst` (requires `loginctl enable-linger` or per-user context which isn't safe for system-level package management)

5. **Early-exit clarity**
   - When the daemon exits because no config file / no credentials / etc., log a clear ERROR-level message saying *why* and *what to do*, then exit with a non-zero code
   - Currently the daemon may silently sleep or exit 0 — change to be explicit

### Files to touch
- `linux-ubuntu/crates/interlinedlist-sync/src/main.rs` — wire `tracing-subscriber` file appender; add `--status` subcommand; clear early-exit messages
- `linux-ubuntu/crates/interlinedlist-sync/Cargo.toml` — add `tracing-appender` if not already a dep
- `linux-ubuntu/packaging/systemd/interlinedlist-sync.service` — journal output, Restart, After=network-online
- `linux-ubuntu/packaging/maintainer-scripts/postinst` — print next-steps message
- New: `linux-ubuntu/crates/interlinedlist-sync/src/status.rs` — `--status` command implementation
- Tests: unit tests for status output formatting; integration test for log file creation

### Acceptance criteria
- After `sudo dpkg -i interlinedlist-sync_*.deb`, the user sees a clear next-steps message
- `interlinedlist-sync --status` produces useful, sanitized output even before any sync has happened
- Daemon early-exit produces an ERROR log line explaining the cause
- Log file appears at the documented path on first run
- `journalctl --user -u interlinedlist-sync` shows the same output as the log file

### Sub-agent
`engineering-linux-ubuntu-agent` — single workstream, can be done in one session.

---

## Workstream B — Windows installer + Start Menu + startup integration

### Problem
Today's Windows artifact is an unsigned `.exe`-in-zip. The user must extract it, run it manually, and there's no installer, no Start Menu entry, no startup registration, no uninstaller. Earlier MSIX attempts hit `APPX3217` UAP-SDK-mismatch on the GitHub-hosted runner across three different version values, so MSIX is deferred.

### Proposed solution — Inno Setup-based installer

**Why Inno Setup over MSIX/MSI:**
- Free, mature, widely used for desktop Windows apps
- No special SDK requirement on the build runner (CLI compiler `iscc.exe` is small and installable via Chocolatey or direct download)
- First-class support for: Start Menu shortcuts, registry-based startup, optional install tasks, code-sign hook
- Produces a single `InterlinedListSync-Setup-v*.exe` installer — familiar UX to Windows users
- WiX/MSI is "more correct" but heavier; Inno Setup matches our actual needs

**Installer behavior:**

1. **Welcome / License / Install Location** pages — standard Inno wizard
2. **Tasks page** — checkboxes:
   - [x] Create Start Menu shortcut *(checked by default)*
   - [x] Start automatically when Windows starts *(checked by default)*
   - [ ] Launch InterlinedList Sync after installation *(checked by default)*
3. **Install** — copies binaries to `%ProgramFiles%\InterlinedList Sync\`, writes Start Menu shortcut, writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` entry conditional on task selection
4. **Finish** — optional launch checkbox

**App-side behavior on launch from Start Menu:**

1. On every launch, check if `HKCU\...\Run\InterlinedSync` registry value exists and points to the current EXE
2. If not present:
   - Show a one-time non-modal toast or dialog: *"Run InterlinedList Sync automatically when you sign in? [Yes / No / Don't ask again]"*
   - "Yes" → write the registry value
   - "No" → don't write, but ask again next launch
   - "Don't ask again" → write a `HKCU\Software\InterlinedSync\StartupPromptSuppressed = 1` flag so the prompt stays hidden
3. App then minimizes to the tray and starts sync as today

**Uninstaller:**
- Standard Inno-generated uninstall — removes app files, removes Start Menu shortcut, removes `Run` registry entry
- Does NOT remove user data (sync folder, credentials in Credential Manager, settings JSON) — preserve user data on uninstall, document this behavior

**Optional code-signing:**
- Inno's `[Setup]` section accepts a `SignTool` directive — wire to a conditional `signtool sign` step when `WINDOWS_CERT_PFX_BASE64` is present (same secret already documented)
- Sign both the installer `.exe` and the embedded `InterlinedSync.exe`

### Files to add/touch

- New: `windows/installer/InterlinedSync.iss` — Inno Setup script
- New: `windows/installer/README.md` — how to build the installer locally
- `.github/workflows/release.yml` (Windows job) — add steps:
  - Install Inno Setup via `choco install innosetup -y`
  - Run `iscc /Qp windows/installer/InterlinedSync.iss /DAppVersion=${{ github.ref_name }}` to compile
  - Conditional signtool step (same pattern as the MSIX scaffold we wrote)
  - Upload `InterlinedListSync-Setup-${{ github.ref_name }}.exe` as a release artifact (alongside the existing `.zip` for users who prefer no installer)
- `windows/InterlinedSync/Storage/IAutoStartManager.cs` + `RegistryAutoStartManager.cs` — already exist from Phase 7; add `IsManagedByInstaller()` to distinguish installer-set vs user-set, and a "don't ask again" preference
- New: `windows/InterlinedSync/UI/Views/StartupPromptDialog.xaml` — one-time prompt on launch from Start Menu
- Modified: `windows/InterlinedSync/App.xaml.cs` — startup-prompt check at launch
- Tests: unit tests for the prompt logic with `MockAutoStartManager`; manual smoke-test checklist for the installer
- Update `windows/PACKAGING.md` to document the Inno-based flow (MSIX remains a future option)

### Acceptance criteria
- Downloading `InterlinedListSync-Setup-v0.2.0.exe`, double-clicking, accepting defaults: app is installed, Start Menu shortcut created, autostart registry entry written, app launches and lives in the tray
- Unchecking "Start automatically" in the wizard: no registry entry written; app launches once if "Launch after installation" was checked, but doesn't restart on next sign-in
- Launching from Start Menu when no registry entry exists: one-time prompt appears with Yes/No/Don't-ask-again
- Uninstall via Add/Remove Programs: removes app + shortcuts + registry entry; preserves sync folder + credentials
- Installer can be signed when cert secrets are present

### Sub-agent
`engineering-windows-agent` — Inno script + app-side prompt + workflow integration. Single session.

---

## Workstream C — Cross-platform first-run account setup UX

### Problem
The app today assumes credentials are already in the platform credential store. If a user installs fresh and runs the app, there's no clear path to enter their email + password. The tray icon may appear but the app sits in an "AuthExpired" or similar state with no obvious next step. Linux is CLI-only for sign-in (`interlinedlist-sync --login` prompt).

The desired behavior across all three platforms:

- **No credentials → tray icon shows a distinct "Sign in needed" state** (warning badge or muted color)
- **Click tray icon** → menu includes a prominent "Sign in…" item at the top
- **Sign in dialog** — email + password fields, "Sign in" button, error display on failed auth
- **Validate via `POST /api/auth/sync-token` BEFORE saving** — never write garbage tokens to the credential store
- **Success** → store token in platform credential store, dismiss dialog, kick off first sync

### Per-platform proposal

#### macOS
- **Status:** `OnboardingView` already exists and handles sign-in. Phase 5 added Preferences. Just need to verify the no-credentials → onboarding flow surfaces correctly.
- **Tasks:**
  - On launch, if `KeychainManager.loadToken()` returns nil, automatically present `OnboardingView` as a modal (window, not sheet — app has no main window)
  - Tray icon shows `exclamationmark.triangle` with "Sign in needed" tooltip when no token
  - "Sign in…" menu item at top of `StatusItemController`'s menu when no token; greyed out when signed in
  - Existing Phase 5 work likely covers most of this — verify and patch gaps
- **Acceptance:** Fresh install → click tray → "Sign in…" → enter creds → app moves to idle state and starts syncing

#### Windows
- **Status:** `OnboardingWindow` exists from Phase 7. Tray icon exists.
- **Tasks:**
  - On launch, if no token in Credential Manager, automatically show `OnboardingWindow` (already done per Phase 7 report — verify)
  - Tray icon: `tray-error.ico` (already a state) when no token
  - Tray right-click menu: top item "Sign in…" when no token; otherwise greyed out
  - "Sign in…" reuses `OnboardingViewModel` — opens the same window as first-run
- **Acceptance:** Fresh install → app launches → onboarding window appears → enter creds → minimize to tray and sync starts

#### Linux
- **Status:** **Biggest gap.** Sign-in is CLI-only (`interlinedlist-sync --login`). The GTK tray app launches the daemon and shows a Settings dialog, but the daemon exits immediately when no token is present, and there's no GUI to enter credentials.
- **Tasks:**
  - **New GTK sign-in dialog** at `linux-ubuntu/crates/tray-app/src/signin.rs`: Adwaita `Window` with email Entry + password Entry (`visibility = false`) + "Sign in" button + error label
  - Wired to `api_client::ApiClient::login(email, password)` directly; on success, write to `SecretStore` and emit a signal that the daemon can subscribe to
  - **Daemon needs to wait for credentials instead of exiting** — if no token at startup, the daemon enters a "waiting for sign-in" state (still running), polls the secret store every 5s, and starts the sync engine once a token appears
  - Tray icon: distinct state when no token (use the `ksni` warning icon or stamp a badge)
  - Tray menu: "Sign in…" item at top when no token; opens the GTK sign-in dialog (gated behind `#[cfg(feature = "gtk")]`)
  - For CLI-only environments (servers, no display), the existing `interlinedlist-sync --login` flow stays as the fallback
- **Acceptance:** Fresh `dpkg -i`, no credentials → tray icon appears with "Sign in" state → click → enter creds → daemon picks up the new token and starts syncing without restart

### Cross-platform sub-tasks
- **Validate credentials BEFORE persisting** — don't write a token if `POST /api/auth/sync-token` returns 401. Surface the error in the dialog.
- **Sign-out path** — Preferences (macOS/Windows) and tray menu (Linux) already have or need a "Sign out" item that clears the credential store and returns the app to the no-credentials state, prompting for sign-in next launch.
- **No password logging** — credentials never appear in any log line. Sign-in HTTP requests don't dump request body to logs.
- **HIG / platform conventions** — sign-in dialogs follow native UX (macOS sheet/window, Windows modal, Linux Adwaita dialog).

### Files to add/touch (per platform)

**macOS:**
- Verify `App/AppDelegate.swift` shows `OnboardingView` when no token at launch (likely already does)
- Update `MenuBar/StatusItemController.swift` to show "Sign in…" item when in unsigned state
- Tests: `OnboardingFlowTests` covering no-token launch path

**Windows:**
- Verify `App.xaml.cs` shows `OnboardingWindow` when no token
- Update `SystemTray/TrayMenuBuilder.cs` (or wherever the tray menu is built) to surface "Sign in…" when in unsigned state
- Tests: extend `OnboardingViewModelTests` for unsigned-state launch

**Linux:**
- New: `linux-ubuntu/crates/tray-app/src/signin.rs` — GTK sign-in dialog
- Modified: `linux-ubuntu/crates/tray-app/src/linux.rs` — "Sign in…" tray menu item; tray state when no token
- Modified: `linux-ubuntu/crates/interlinedlist-sync/src/main.rs` — wait-for-credentials state, poll secret-store, hot-start engine on token appearance
- Modified: `linux-ubuntu/crates/sync-engine/src/lib.rs` — accept "start without engine running" mode that can be triggered later
- Tests: integration test for wait-for-credentials → token-appears → engine-starts flow

### Sub-agents
- macOS: `swift-engineer` (small — verify + patch gaps)
- Windows: `engineering-windows-agent` (small — verify + patch + tray menu)
- Linux: `engineering-linux-ubuntu-agent` (largest — new dialog + daemon state machine)

All three can run in parallel.

---

## Suggested execution order

1. **Workstream A** (Linux logging) — smallest, unlocks user-facing diagnosis immediately
2. **Workstream C** (account setup UX) — three parallel agents, blocks usability of any installer
3. **Workstream B** (Windows installer) — biggest single workstream, depends on Workstream C being done so the installer's "launch after install" actually presents a sign-in dialog
4. **Cut v0.2.0** — same release flow as v0.1.1-alpha; bump tag to `v0.2.0` (or `v0.2.0-beta` if you want another prerelease)

ETA per workstream: 1 session each (3 sessions total if sequential, ~1.5 if parallel where possible).

---

## What's NOT in this plan (still deferred)

- MSIX packaging — defer until we can pin a Windows SDK in CI or move to a self-hosted runner
- Snap packaging — defer until we have snapcraft remote-build credentials or move to a self-hosted runner
- Apple Developer ID signing / notarization — defer until creds available
- Windows code-signing cert — defer until cert procured
- App Store / Snap Store publication — separate manual processes
