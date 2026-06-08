# Next Steps — Linux/Ubuntu Implementation

## Current State (as of 2026-06-05)

The Cargo workspace and all crates have been scaffolded. Implementation reached **M1 + M2 + M3** before the session was paused.

### What is complete

| Crate | Status |
|---|---|
| `config-store` | Config TOML read/write with validation and defaults |
| `state-store` | SQLite schema + CRUD (insert, lookup by path, lookup by server ID, upsert, delete, set pending op) |
| `api-client` | Async HTTP — `login()`, `list_documents()`, `get_document()`, `create_document()`, `update_document()`, `delete_document()`, retry with exponential backoff |
| `api-client/mock` | `MockApiClient` for unit tests (in-memory store + `fail_once` injection) |
| `file-watcher` | inotify via `notify` crate, 500ms debounce, recursive watch, emits `FileChanged/Created/Deleted` |
| `sync-engine` | Full sync cycle: upload on FileChanged, hash dedup, periodic remote poll, conflict resolution (remote-wins + conflict-copy), pending op on failure, atomic write |
| `secret-store` | `SecretStore` trait + `FileSecretStore` fallback (token in `~/.config/interlinedlist-sync/.token`, 0600 perms) |
| `notifier` | `Notifier` trait + `StubNotifier` (no-op) + `LibnotifyNotifier` stub |
| `tray-app` | `ksni`-based StatusNotifierItem tray (pure Rust, works on X11 + Wayland); menu: Status / Sync Now / Pause / Open Web App / Quit |
| `interlinedlist-sync` (bin) | `--login` / `--daemon` / headless modes wired together |
| Packaging | systemd user unit, AppArmor profile (starter), `.desktop` autostart entry, sysctl drop-in |
| Unit tests | `sync-engine` tests: upload on change, no-op on hash match, conflict copy, remote-wins, pending op on failure |

### What is missing / TODOs in code

1. **`cargo check` not yet run** — the workspace compiles on Linux only (inotify, ksni). Verify on a Linux host or in a Docker container before assuming clean compilation. Known issue: `tray-app/Cargo.toml` may need `ksni` version pinned and `tokio` sync feature explicitly listed.

2. **`notifier` — libnotify wiring incomplete** — `LibnotifyNotifier` is scaffolded but the `notify-rust` crate integration may need finishing. `main.rs` currently uses `StubNotifier`. Switch to `LibnotifyNotifier` when running on Linux.

3. **M4 — Settings dialog** — not started. Requires:
   - Adwaita `PreferencesWindow` with panels: Watched Folders, Sync Interval, Conflict Resolution, Notifications, Account
   - Wire `Settings...` menu item in tray to open this window (currently the menu item is absent from `linux.rs`)
   - GTK4 / libadwaita as optional feature flag (`#[cfg(feature = "gtk")]`) since GTK dev libs are not available on the macOS dev machine

4. **M4 — GNOME Keyring** — `secret-store` has a `#[cfg(feature = "gnome-keyring")]` stub for `KeyringSecretStore` but the implementation body is empty. Fill in with the `secret-service` crate.

5. **M5 — Offline queue** — `queue.db` path is defined but the queue table is not created and `SyncEngine::run()` does not drain it. Needs:
   - `StateStore` or separate `QueueStore` with insert/drain operations
   - NetworkManager D-Bus subscription via `zbus` to trigger drain on reconnect

6. **M5 — `sync_now_rx` wiring** — `main.rs` creates `(sync_now_tx, _sync_now_rx)` but `_sync_now_rx` is discarded. Wire it into `SyncEngine::run()` so the tray "Sync Now" button triggers an immediate poll.

7. **M6 — `.deb` packaging** — `packaging/` files exist but `Cargo.toml` does not yet have `[package.metadata.deb]` for `cargo-deb`. Add:
   - `assets` mapping for binary, systemd unit, AppArmor profile, desktop entry, sysctl file
   - `maintainer-scripts/postinst` to install sysctl drop-in (`sysctl --system`) and AppArmor profile (`apparmor_parser`)
   - `maintainer-scripts/prerm` to remove them

8. **M7 — Snap** — `snapcraft.yaml` not started.

9. **Test coverage gaps**:
   - `config-store`: round-trip serialize/deserialize test
   - `state-store`: unit tests for all CRUD operations
   - `api-client`: mock-server tests (add `mockito` or `wiremock` to dev-dependencies)
   - `file-watcher`: event emission test using `tempfile`

---

## How to Resume

### Immediate next action

```bash
# On a Linux host or Docker (Ubuntu 22.04 / 24.04):
cd linux-ubuntu
cargo check --workspace
```

Fix any compilation errors before continuing. The most likely issues are:
- Missing `tokio` features in individual `Cargo.toml` files
- `ksni` API mismatch (check `ksni = "0.2"` docs for `TrayService::spawn` signature)
- `notify-debouncer-full` debounce API (verify `new_debouncer` call in `file-watcher/src/lib.rs`)

### Then continue in order

1. Fix `cargo check` errors
2. Add missing tests (`config-store`, `state-store`, `api-client`, `file-watcher`)
3. Wire `sync_now_rx` into `SyncEngine::run()` (M5 partial)
4. Finish `LibnotifyNotifier` and switch `main.rs` from `StubNotifier`
5. Implement `KeyringSecretStore` with `secret-service` crate (M4)
6. Add Settings dialog (M4) — GTK4 + libadwaita, gated behind `#[cfg(feature = "gtk")]`
7. Implement offline queue + NetworkManager D-Bus drain (M5)
8. Add `[package.metadata.deb]` to `Cargo.toml` and `postinst`/`prerm` scripts (M6)
9. Write `snapcraft.yaml` (M7)

---

## Open Questions (from the-plan.md) — answers needed before M4

1. **API auth** — confirmed username/password → bearer token. Is the endpoint `POST /api/auth/login`? Verify against https://interlinedlist.com/help/api.
2. **Document API shape** — does `GET /api/documents` return `sha256` in the summary? If not, the remote-poll conflict detection needs adjustment.
3. **Wayland tray requirement** — `ksni` (StatusNotifierItem) works on GNOME Wayland with the AppIndicator extension enabled. Confirm this is acceptable or if a fallback window is needed.
4. **Ubuntu LTS targets** — 22.04 and 24.04 confirmed? Affects GTK4 / libadwaita version pinning in `.deb` deps.
