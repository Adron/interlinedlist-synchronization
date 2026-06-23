# Next Steps — Linux/Ubuntu Implementation

## Current State (as of 2026-06-22)

The Cargo workspace and all crates are implemented through **M1 + M2 + M3 + M4 (partial)**.

### What is complete

| Crate | Status |
|---|---|
| `config-store` | Config TOML read/write with validation, defaults, and round-trip tests |
| `state-store` | SQLite schema + CRUD (insert, lookup by path, lookup by server ID, upsert, delete, set pending op) |
| `api-client` | Async HTTP — `login()`, `list_documents()`, `get_document()`, `create_document()`, `update_document()`, `delete_document()`, retry with exponential backoff; mockito-based integration tests |
| `api-client/mock` | `MockApiClient` for unit tests (in-memory store + `fail_once` injection) |
| `file-watcher` | inotify via `notify` crate, 500ms debounce, recursive watch, emits `FileChanged/Created/Deleted` |
| `sync-engine` | Full sync cycle: upload on FileChanged, hash dedup, periodic remote poll, conflict resolution (remote-wins + conflict-copy), pending op on failure, atomic write; `sync_now_rx` wired into `run()` loop |
| `secret-store` | `SecretStore` trait + `FileSecretStore` (token in `~/.config/…/.token-<account>`, 0600 perms) + `KeyringSecretStore` (GNOME Keyring via `secret-service` crate, D-Bus, with in-memory mock tests + `#[ignore]`'d real-keyring round-trip) |
| `notifier` | `Notifier` trait + `StubNotifier` + `LibnotifyNotifier` (notify-rust, libnotify backend, spawn_blocking) |
| `tray-app` | `ksni`-based StatusNotifierItem tray (X11 + Wayland); menu: Status / Sync Now / Pause / Settings… / Open Web App / Quit; Settings menu item wires to `open_settings_window` |
| `tray-app` (M4) | Settings dialog: Adwaita `PreferencesWindow` with Watched Folders, Sync, Notifications, Account panels; gated behind `--features gtk`; GTK4 runs in a dedicated thread with GLib main loop |
| `interlinedlist-sync` (bin) | `--login` / `--daemon` / headless modes; `sync_now_rx` plumbed from tray to engine; Linux: `LibnotifyNotifier` + `KeyringSecretStore` (with file store fallback); `config_path` forwarded to tray for settings dialog |
| Packaging | systemd user unit, AppArmor profile (starter), `.desktop` autostart entry, sysctl drop-in |
| Unit tests | `sync-engine`: upload on change, no-op on hash match, conflict copy, remote-wins, pending op on failure, **`sync_now` channel triggers remote poll**; `secret-store`: in-memory mock store CRUD + ignored keyring round-trip; `tray-app`: settings config round-trip |

### What is missing / TODOs in code

1. **`cargo check` requires Linux host** — inotify, ksni, and (with `--features gtk`) GTK4/libadwaita dev headers are Linux-only. Run on Ubuntu 22.04/24.04 or Docker. On macOS dev machines the workspace compiles to stubs without those features.

2. **M4 — Settings dialog save wiring is partial** — the Sync and Notifications pages have a "Save" row that writes back to `ConfigStore`. The Watched Folders and Account pages currently only display values; path picker result is shown in the UI but not persisted on close. Wire up a window `close-request` handler that collects all field values and saves the config atomically.

3. **M4 — Settings dialog: `SwitchRow` save** — `SwitchRow` toggling is shown but not saved on panel switch. A "Save" button is provided per panel; consider switching to a single "Apply" / "Revert" pattern at the window level for consistency.

4. **M4 — `libadwaita` version on Ubuntu 22.04** — libadwaita 1.0.x in Jammy does not have `PreferencesWindow`, `SpinRow`, or `SwitchRow`. Document the PPA requirement (`ppa:gnome-team/gnome-next`) prominently in the `.deb` README and `postinst`. Consider a `PreferencesPage`-free fallback for 22.04 minimal installs if PPA adoption is low.

5. **M5 — Offline queue** — `queue.db` path is defined but the queue table is not created and `SyncEngine::run()` does not drain it. Needs:
   - `StateStore` or separate `QueueStore` with insert/drain operations
   - NetworkManager D-Bus subscription via `zbus` to trigger drain on reconnect

6. **M6 — `.deb` packaging** — `packaging/` files exist and `[package.metadata.deb]` is in `Cargo.toml`. Still needed:
   - `maintainer-scripts/postinst` to install sysctl drop-in (`sysctl --system`) and AppArmor profile (`apparmor_parser`)
   - `maintainer-scripts/prerm` to stop the user service
   - Verify `lintian` passes on a built `.deb`

7. **M7 — Snap** — `snapcraft.yaml` not started.

8. **Test coverage gaps**:
   - `state-store`: unit tests for all CRUD operations (insert, lookup, upsert, delete, set_pending_op)
   - `file-watcher`: event emission test using `tempfile` + `tokio` (Linux-only, `#[ignore]` on macOS)
   - `tray-app` (settings): `#[ignore]`'d smoke test that opens the window on a Linux display

---

## How to Resume

```bash
# On Ubuntu 22.04 / 24.04 (or Docker):
cd linux-ubuntu
cargo check --workspace

# With GTK4/libadwaita (install dev headers first):
sudo apt install libgtk-4-dev libadwaita-1-dev  # or use PPA for 22.04
cargo check --workspace --features tray-app/gtk

# Run all cross-platform tests:
cargo test --workspace

# Build release binary:
cargo build --release

# Build .deb:
cargo deb
lintian target/debian/*.deb
```

### Next tasks in order

1. Finish settings dialog save wiring — window `close-request` → collect all fields → save config
2. Add state-store unit tests
3. Add file-watcher event emission test (Linux-only / `#[ignore]`)
4. Implement offline queue + NetworkManager D-Bus drain (M5)
5. Write `postinst`/`prerm` maintainer scripts and verify `lintian` (M6)
6. Write `snapcraft.yaml` (M7)

---

## Open Questions

1. **API auth endpoint** — confirmed `POST /auth/login` in code. Verify against live server before first run.
2. **Document API shape** — does `GET /documents` return `sha256` in the summary? If not, remote-poll conflict detection falls back to timestamp comparison only.
3. **Wayland tray** — `ksni` (StatusNotifierItem) works on GNOME Wayland with the AppIndicator extension enabled. Confirm this is acceptable.
4. **Ubuntu 22.04 libadwaita** — PPA requirement for `PreferencesWindow`. Decide: ship PPA instruction in postinst, or gate the settings dialog behind a runtime version check that falls back to a simpler dialog.
