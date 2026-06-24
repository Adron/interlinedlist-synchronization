# Next Steps — Linux/Ubuntu Implementation

## Current State (as of 2026-06-22)

The Cargo workspace and all crates are implemented through **M1 + M2 + M3 + M4 (complete) + M5 (complete) + M6 (complete) + M7 (complete)**.

### What is complete

| Crate | Status |
|---|---|
| `config-store` | Config TOML read/write with validation, defaults, round-trip tests; `ConflictResolution::LocalWins` variant added |
| `state-store` | SQLite schema + CRUD; `pending_ops` table with `enqueue_op` / `peek_pending_ops` / `mark_op_done` / `mark_op_failed` |
| `api-client` | Async HTTP — login, list/get/create/update/delete document, delta, retry with exponential backoff; mock + integration tests |
| `api-client/mock` | `MockApiClient` for unit tests (in-memory store + `fail_once` injection) |
| `file-watcher` | inotify via `notify` crate, 500ms debounce, recursive watch, emits `FileChanged/Created/Deleted` |
| `sync-engine` | Full sync cycle: upload on FileChanged, hash dedup, periodic remote poll, conflict resolution (remote-wins, conflict-copy, **local-wins**); offline queue drain on startup / poll cycle / network reconnect; `SyncEngine::with_network_monitor` constructor |
| `sync-engine` (M5) | `NetworkMonitor` trait + `StubNetworkMonitor` (testable via `Arc<Notify>`); `NetworkManagerMonitor` (Linux D-Bus, `#[cfg(target_os="linux", feature="network-monitor")]`) |
| `secret-store` | `SecretStore` trait + `FileSecretStore` + `KeyringSecretStore` (GNOME Keyring via `secret-service`, D-Bus) |
| `notifier` | `Notifier` trait + `StubNotifier` + `LibnotifyNotifier` (notify-rust, libnotify backend) |
| `tray-app` | `ksni`-based StatusNotifierItem tray; Settings dialog refactored to window-level Apply/Revert; folder picker result persisted on Apply/close; `LocalWins` radio button added |
| `interlinedlist-sync` (bin) | `--login` / `--daemon` / headless modes; `sync_now_rx` plumbed from tray to engine; `config_path` forwarded to tray for settings dialog |
| Packaging | systemd user unit, AppArmor profile (starter), `.desktop` autostart entry, sysctl drop-in, `postinst`/`prerm` maintainer scripts |
| Unit tests | 59 unit tests across all crates; 5 live-API integration tests |

### M4 — Completed items

1. **Folder picker persistence** — `build_folders_page` returns a collector closure; `window.connect_close_request` and the Apply button both call all four collectors and write config atomically once.
2. **`LocalWins` conflict resolution** — added to `ConflictResolution` enum, handler arm in `write_document_atomic` returns early (keeps local file), radio button in settings Sync panel, round-trip serialisation test.
3. **Single Apply/Revert at window level** — per-panel Save rows removed; `do_save` closure shared between Apply button and `connect_close_request`; Revert closes window and reopens with disk config.

### M5 — Completed items

1. **`pending_ops` table** — added to `StateStore::migrate()`; `OpKind` enum (`Upload/Delete/Rename`), `QueuedOp` struct, `enqueue_op` / `peek_pending_ops` / `mark_op_done` / `mark_op_failed` methods.
2. **`SyncEngine` integration** — `drain_queue()` called on startup, on every poll tick, and on manual sync; `upload_if_changed` failure path calls `enqueue_op` in addition to `set_pending_op`.
3. **`NetworkMonitor` trait** — `pub trait NetworkMonitor: Send + Sync { async fn wait_for_reconnect(&self); }`; `StubNetworkMonitor` (testable, `Arc<Notify>`); `NetworkManagerMonitor` stub behind `#[cfg(target_os="linux", feature="network-monitor")]` in `crates/sync-engine/src/nm_monitor.rs`; `select!` arm in `run()` triggers `drain_queue()` on reconnect.

### What is missing / TODOs in code

1. **`cargo check` requires Linux host** — inotify, ksni, and (with `--features gtk`) GTK4/libadwaita dev headers are Linux-only. Run on Ubuntu 22.04/24.04 or Docker.

2. **`NetworkManagerMonitor` zbus signal stream** — `nm_monitor.rs` uses `receive_state_changed()` / `stream.next()` which requires `zbus` and `futures` in scope. Wire in `zbus` as an optional workspace dep when the `network-monitor` feature is active; confirm the exact signal proxy macro syntax under zbus 4.x vs 5.x. The D-Bus path for the `StateChanged` signal is `/org/freedesktop/NetworkManager` with interface `org.freedesktop.NetworkManager`.

3. **M4 — `libadwaita` version on Ubuntu 22.04** — libadwaita 1.0.x in Jammy does not have `PreferencesWindow`, `SpinRow`, or `SwitchRow`. The PPA requirement (`ppa:gnome-team/gnome-next`) must be documented in the `.deb` README and `postinst`.

4. **M6 — `.deb` packaging** — COMPLETE.
   - `packaging/maintainer-scripts/postinst`: reloads sysctl (`sysctl --system`) and loads AppArmor profile (`apparmor_parser -r`) on configure.
   - `packaging/maintainer-scripts/prerm`: unloads AppArmor profile (`apparmor_parser -R`) on remove/upgrade/deconfigure.
   - `maintainer-scripts = "../../packaging/maintainer-scripts"` added to `[package.metadata.deb]` in `crates/interlinedlist-sync/Cargo.toml`.
   - `cargo deb --no-build` (with stub binary) confirmed both scripts land in `control.tar.xz` at mode 755.
   - `lintian` requires a Linux host; verify in CI (`Build .deb` step already present).

5. **M7 — Snap** — COMPLETE.
   - `snap/snapcraft.yaml` created with `base: core22`, `confinement: classic`, `plugin: rust`.
   - CI `build-linux` job updated: installs snapcraft, runs `snapcraft --destructive-mode`, uploads `.snap` in `linux-release` artifact.
   - Release notes table updated with snap install instruction.
   - `PACKAGING.md` documents local test install, Store credential setup, classic vs strict confinement tradeoff, and release checklist.

6. **Test coverage gaps**:
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
cargo test --workspace --exclude tray-app

# Build release binary:
cargo build --release

# Build .deb:
cargo deb
lintian target/debian/*.deb
```

### Next tasks in order (all platform packaging complete)

1. Wire `zbus` optional dep for `network-monitor` feature; verify `NetworkManagerMonitor` compiles on Ubuntu 22.04 with zbus 4.x
2. Add file-watcher event emission test (Linux-only / `#[ignore]`)
3. Register `interlinedlist-sync` snap name at https://snapcraft.io/snaps
4. Request classic confinement approval at https://forum.snapcraft.io/c/store-requests/
5. Set `SNAPCRAFT_STORE_CREDENTIALS` repo secret; enable Store upload step in `release.yml`

---

## Open Questions

1. **API auth endpoint** — confirmed `POST /auth/login` in code. Verify against live server before first run.
2. **Document API shape** — does `GET /documents` return `sha256` in the summary? If not, remote-poll conflict detection falls back to timestamp comparison only.
3. **Wayland tray** — `ksni` (StatusNotifierItem) works on GNOME Wayland with the AppIndicator extension enabled. Confirm this is acceptable.
4. **Ubuntu 22.04 libadwaita** — PPA requirement for `PreferencesWindow`. Decide: ship PPA instruction in postinst, or gate the settings dialog behind a runtime version check that falls back to a simpler dialog.
5. **zbus version** — `nm_monitor.rs` uses the `#[proxy]` macro. Confirm zbus 4.x vs 5.x API compatibility; the `receive_state_changed()` method name is generated by the macro and differs between major versions.
