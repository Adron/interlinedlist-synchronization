# InterlinedList Sync — Linux/Ubuntu: Implementation Plan

## Overview

A native Linux/Ubuntu system-tray synchronization daemon for the InterlinedList Documents feature. The application runs as a **systemd user service**, watches local directories with **inotify**, syncs with the InterlinedList REST API, and surfaces status and controls through a **GTK4/libadwaita system tray menu**.

"Native" here means: uses the Linux kernel's own file-watching API, integrates with the GNOME/Ubuntu desktop stack, stores credentials in GNOME Keyring, logs to the systemd journal, and ships as a proper `.deb` package with an AppArmor profile.

---

## Recommended Technology Stack

### Language: Rust

Rust is the recommended language for this application.

**Why Rust over the alternatives:**
- **vs. Python:** Rust compiles to a single statically-linked binary — simpler packaging, faster startup, no interpreter version conflicts on different Ubuntu LTS releases, no `venv` management.
- **vs. Go:** GTK4 bindings (`gtk4-rs`) are mature and actively maintained by the GNOME project. Go's GTK bindings are community-maintained and lag behind. Rust also gives finer control over inotify and async I/O.
- **vs. C/C++:** Memory safety without a runtime. No segfaults from buffer handling of file paths or API responses.

Python with PyGObject (`pygobject` / `python3-gi`) is the viable fallback if Rust expertise is not available — it ships pre-installed on Ubuntu, the GTK4 bindings are first-class, and development is faster. The architecture described below applies equally to either language.

### Core Crates / Libraries

| Concern | Rust crate | Python equivalent |
|---|---|---|
| GTK4 UI | `gtk4` + `libadwaita` | `PyGObject` (gi.repository.Gtk, Adw) |
| System tray | `libayatana-appindicator3` (via `ayatana-appindicator` crate) | `gi.repository.AyatanaAppIndicator3` |
| File watching | `notify` (inotify backend on Linux) | `watchdog` |
| HTTP client | `reqwest` (async, TLS via `rustls`) | `httpx` |
| Async runtime | `tokio` | `asyncio` |
| Serialization | `serde` + `serde_json` + `toml` | `tomllib` + `json` (stdlib) |
| SQLite state | `rusqlite` | `sqlite3` (stdlib) |
| Secret storage | `secret-service` crate (libsecret / GNOME Keyring) | `secretstorage` |
| Desktop notifications | `notify-rust` (libnotify backend) | `gi.repository.Notify` |
| D-Bus IPC | `zbus` | `dasbus` |
| systemd notify | `sd-notify` crate | `sdnotify` |

### Build and Packaging

| Format | Tool | Target |
|---|---|---|
| `.deb` | `cargo-deb` (Rust) or `dpkg-deb` | Ubuntu PPA, direct download |
| Snap | `snapcraft` | Ubuntu Snap Store |
| Flatpak | Flatpak manifest | Flathub (broader distro reach) |

The `.deb` is the primary format for Ubuntu users. Snap is the recommended secondary format for staying current on all Ubuntu LTS releases without a PPA.

---

## Architecture

### Process Model

```
┌─────────────────────────────────────────────────────────┐
│  systemd user service (interlinedlist-sync.service)     │
│                                                         │
│  ┌──────────────┐   inotify    ┌─────────────────────┐  │
│  │  FileWatcher │ ──────────▶  │   SyncEngine        │  │
│  │  (inotify)   │              │   (tokio async)     │  │
│  └──────────────┘              │                     │  │
│                                │  ┌───────────────┐  │  │
│  ┌──────────────┐   API calls  │  │  StateStore   │  │  │
│  │  ApiClient   │ ◀─────────── │  │  (SQLite)     │  │  │
│  │  (reqwest)   │              │  └───────────────┘  │  │
│  └──────────────┘              └─────────────────────┘  │
│                                         │               │
│  ┌──────────────────────────────────────▼─────────────┐ │
│  │  TrayApp (GTK4 + libayatana-appindicator3)         │ │
│  │  Menu: Status / Open Web App / Settings / Quit     │ │
│  └───────────────────────────────────────────────────-┘ │
│                                                         │
│  ┌──────────────┐                                       │
│  │  Notifier    │  libnotify (desktop notifications)    │
│  └──────────────┘                                       │
└─────────────────────────────────────────────────────────┘
```

### Component Responsibilities (SRP)

Each component has exactly one responsibility:

- **FileWatcher** — wrap inotify, debounce rapid events (e.g., editor save storms), emit a `FileChanged(path)` event. Nothing else.
- **ApiClient** — make authenticated HTTP calls to the InterlinedList API. Owns TLS, retries with exponential backoff, and token refresh. Nothing else.
- **StateStore** — SQLite database tracking sync state: local path → server ID, last-synced hash, pending operations. Nothing else.
- **SyncEngine** — orchestrate: receive events from FileWatcher, compute diffs, call ApiClient, update StateStore, emit results. No UI, no file I/O beyond what is needed to read document content.
- **TrayApp** — GTK4 main loop, system tray icon, menu, settings dialog. Receives status updates from SyncEngine via a channel. No sync logic.
- **Notifier** — send desktop notifications via libnotify. Receives notification requests from SyncEngine and TrayApp.
- **ConfigStore** — read/write `~/.config/interlinedlist-sync/config.toml`. Validates on load. Emits a `ConfigChanged` event on SIGHUP.
- **SecretStore** — read/write API credentials from GNOME Keyring via libsecret. Abstracted behind a trait/interface so a file-based fallback can substitute (LSP).

### Dependency Flow (DIP)

```
TrayApp          ──depends on──▶  SyncEngine (trait/interface)
SyncEngine       ──depends on──▶  FileWatcher (trait)
                                  ApiClient (trait)
                                  StateStore (trait)
                                  Notifier (trait)
ConfigStore      ──injected into──▶ SyncEngine, TrayApp
SecretStore      ──injected into──▶ ApiClient
```

No component imports a concrete sibling. All cross-component dependencies are injected via constructors and satisfied by trait objects (Rust) or abstract base classes / protocols (Python).

---

## File and Directory Layout

### Runtime paths (XDG)

| Path | Purpose |
|---|---|
| `~/.config/interlinedlist-sync/config.toml` | User configuration |
| `~/.local/share/interlinedlist-sync/state.db` | SQLite sync state |
| `~/.local/share/interlinedlist-sync/queue.db` | Offline operation queue |
| `~/.cache/interlinedlist-sync/` | Transient cache (thumbnail previews, etc.) |
| `$XDG_RUNTIME_DIR/interlinedlist-sync/` | PID file, socket (cleared on logout) |

GNOME Keyring (libsecret) stores the API token — never written to disk in plaintext.

### Installed paths (FHS / .deb)

```
/usr/bin/interlinedlist-sync          # main binary
/usr/share/interlinedlist-sync/       # read-only app data (icons, default config)
/usr/lib/systemd/user/
  interlinedlist-sync.service         # systemd user unit
/etc/apparmor.d/usr.bin.interlinedlist-sync
/usr/share/applications/
  interlinedlist-sync.desktop         # XDG desktop entry (autostart)
/usr/share/doc/interlinedlist-sync/
```

---

## systemd User Service

The application runs as a **user** service (`systemctl --user`), not a system service. This means:

- No root/sudo required to install or start.
- The service starts when the user logs in (via `WantedBy=default.target`).
- Credentials remain scoped to the user's GNOME Keyring session.

```ini
# /usr/lib/systemd/user/interlinedlist-sync.service
[Unit]
Description=InterlinedList Document Synchronization
Documentation=https://interlinedlist.com/help/documents
After=graphical-session.target network-online.target
Wants=network-online.target

[Service]
Type=notify
ExecStart=/usr/bin/interlinedlist-sync --daemon
ExecReload=/bin/kill -HUP $MAINPID
Restart=on-failure
RestartSec=10s
TimeoutStopSec=30s

# XDG state dirs created automatically by systemd
StateDirectory=%h/.local/share/interlinedlist-sync
CacheDirectory=%h/.cache/interlinedlist-sync
ConfigurationDirectory=%h/.config/interlinedlist-sync

# Hardening
NoNewPrivileges=yes
PrivateTmp=yes

[Install]
WantedBy=default.target
```

---

## File Watching — inotify

The FileWatcher subscribes to inotify events on configured watched directories:

- `IN_CLOSE_WRITE` — file written and closed (the canonical "save" event)
- `IN_MOVED_TO` — file moved into the watched directory
- `IN_DELETE` — file removed (triggers remote delete or archive)
- `IN_CREATE` + `IN_ISDIR` — new subdirectory (add watch recursively)

**Debouncing:** a 500ms quiet window after the last event for a given path before emitting to the SyncEngine. Prevents redundant uploads during rapid editor saves.

**Watch limit:** `/proc/sys/fs/inotify/max_user_watches` defaults to 8192 on Ubuntu. The `.deb` `postinst` script sets `fs.inotify.max_user_watches=524288` via `/etc/sysctl.d/60-interlinedlist-sync.conf`.

---

## Sync Engine — Core Logic

### Sync Cycle

1. On `FileChanged(path)` from FileWatcher:
   a. Compute SHA-256 of the local file.
   b. Look up last-synced hash in StateStore.
   c. If hash matches → skip (no-op).
   d. If hash differs → enqueue `Upload(path)` operation.

2. On periodic poll (configurable, default 5 minutes):
   a. Fetch document list from API.
   b. For each remote document, check StateStore for last-synced version.
   c. If remote is newer → enqueue `Download(doc_id)` operation.
   d. If local is newer and no pending upload → enqueue `Upload(path)`.

3. On `Upload(path)`:
   a. Read file content.
   b. `POST /api/documents` or `PUT /api/documents/{id}`.
   c. On success: update StateStore hash and server ID.
   d. On failure: add to offline queue; schedule retry.

4. On `Download(doc_id)`:
   a. `GET /api/documents/{id}`.
   b. Write to local path (atomic write: write to `.tmp`, then `rename(2)`).
   c. Update StateStore.

### Conflict Resolution

When both local and remote versions have changed since the last sync:

- Default strategy: **remote wins** (server is the authoritative source).
- Alternative: **create a conflict copy** (`filename.conflict-YYYYMMDD-HHMMSS.ext`) and notify the user.
- The strategy is configurable in `config.toml`.

### Offline Queue

Operations that fail due to network unavailability are written to `queue.db`. The SyncEngine subscribes to NetworkManager D-Bus signals (`org.freedesktop.NetworkManager.StateChanged`) and drains the queue when connectivity is restored.

---

## Authentication and Security

- OAuth2 or API key (to be confirmed — see open questions).
- Token stored in GNOME Keyring via `libsecret` — never in `config.toml` or on disk in plaintext.
- All API calls use TLS (rustls, no system OpenSSL dependency for the Snap/Flatpak builds).
- AppArmor profile ships with the `.deb`; enforced on install.
- The binary runs as the logged-in user; no `setuid`, no capabilities needed.
- `config.toml` is `0600` — readable only by the owner.

---

## System Tray and UI

### Tray Menu (GTK4 + libayatana-appindicator3)

```
[●] InterlinedList Sync           ← icon changes: idle/syncing/error
  ─────────────────────
  Last synced: 2 minutes ago
  ─────────────────────
  Sync Now
  Pause Sync
  ─────────────────────
  Open Web App
  ─────────────────────
  Settings...
  View Logs
  ─────────────────────
  Quit
```

Tray icon states:
- **Idle (green)** — synced and watching
- **Syncing (animated)** — upload or download in progress
- **Paused (grey)** — user-paused or offline
- **Error (red)** — last sync failed; click for details

### Settings Dialog (GTK4 + libadwaita)

A native Adwaita preferences window with:
- **Watched folders** — add/remove directory paths (file chooser dialog)
- **Sync interval** — slider: 1 min to 60 min (plus "real-time only")
- **Conflict resolution** — radio: Remote wins / Create conflict copy
- **On startup** — toggle: Start with session
- **Notifications** — toggle: Show sync notifications, toggle: Show error notifications
- **Account** — display current account email; Sign Out button

### Notifications (libnotify)

| Event | Urgency | Example |
|---|---|---|
| Sync completed (N files) | Low | "Synced 3 documents" |
| Conflict detected | Normal | "Conflict in report.md — conflict copy created" |
| Auth token expired | Critical | "Sign in to InterlinedList to resume sync" |
| Network restored, queue draining | Low | "Back online — syncing 7 queued changes" |
| Error after retries exhausted | Critical | "Sync error: check Settings > View Logs" |

---

## Configuration (`config.toml`)

```toml
[sync]
watched_dirs = [
  "~/Documents/InterlinedList",
]
interval_seconds = 300          # 5-minute poll; 0 = inotify-only
conflict_resolution = "remote-wins"   # or "conflict-copy"
pause_on_battery = false

[notifications]
show_success = true
show_conflicts = true
show_errors = true

[network]
api_base_url = "https://interlinedlist.com/api"
timeout_seconds = 30
max_retries = 5
retry_backoff_base_seconds = 2

[ui]
autostart = true
```

---

## Packaging Plan

### Phase 1 — `.deb` (Ubuntu PPA)
- Build with `cargo-deb` (Rust) or `dpkg-deb`.
- Ship systemd user unit, AppArmor profile, `.desktop` autostart entry, sysctl drop-in.
- Target: Ubuntu 22.04 LTS and 24.04 LTS (amd64, arm64).
- Lint with `lintian` before release.
- Host on a Launchpad PPA for `apt` installation.

### Phase 2 — Snap
- `snapcraft.yaml` with `confinement: strict`.
- Plugs: `network`, `home`, `secret-service`, `unity7` (tray), `desktop-notifications`.
- Enables staying current on all Ubuntu LTS releases via automatic updates.

### Phase 3 — Flatpak
- Broader distro reach (Fedora, Arch, etc. with GNOME).
- Publish to Flathub.

---

## Development Milestones

| Milestone | Deliverables |
|---|---|
| **M1 — Core sync** | ApiClient, StateStore, SyncEngine (no UI). Tested with a headless binary that logs to stdout. |
| **M2 — File watching** | FileWatcher with inotify, debouncer, integration with SyncEngine. |
| **M3 — Tray + notifications** | GTK4 main loop, tray icon, basic menu, libnotify integration. |
| **M4 — Settings dialog** | Adwaita preferences window, config read/write, GNOME Keyring integration. |
| **M5 — Offline queue** | NetworkManager D-Bus subscription, queue drain on reconnect. |
| **M6 — Packaging** | `.deb` with AppArmor, systemd user unit, sysctl drop-in, autostart entry. |
| **M7 — Snap** | `snapcraft.yaml`, store submission. |
| **M8 — Hardening** | AppArmor enforce mode, security audit, performance profiling. |

---

## Open Questions

These need answers before implementation begins. See the questions section below.

1. **Authentication method** — OAuth2 (authorization code flow) or API key? If OAuth2, what is the redirect URI scheme for a desktop app (loopback or custom scheme)?
2. **Document types** — What file formats does the API accept? Plain text, Markdown, PDF, proprietary JSON? Does the sync treat files as opaque blobs or does it parse content?
3. **API pagination** — Does the document list API paginate? What are the rate limits?
4. **Conflict resolution default** — Should the default be "remote wins" (safe, no data loss on server) or "create conflict copy" (preserves local work)?
5. **Offline behavior** — Should local edits be queued and replayed when connectivity returns, or should offline edits be discarded?
6. **Multiple accounts** — Is support for signing into more than one InterlinedList account in scope?
7. **Ubuntu LTS targets** — Which Ubuntu releases must be supported at launch? 22.04 (Jammy)? 24.04 (Noble)? Both?
8. **Desktop environment scope** — GNOME/Ubuntu only, or also KDE Plasma, XFCE, Cinnamon? (Affects tray implementation choice — `libayatana-appindicator3` works on all, but KDE has its own StatusNotifier protocol.)
9. **Wayland vs. X11** — AppIndicator tray icons on GNOME Wayland require the `gnome-shell-extension-appindicator` extension. Is this an acceptable requirement, or should there be a fallback (e.g., a floating window or GNOME extension bundled in the `.deb`)?
10. **Distribution priority** — Is the `.deb` / PPA the primary shipping vehicle, or is Snap preferred (simpler updates, wider reach)?
11. **Language preference** — Is there a team preference for Rust vs. Python vs. Go? Rust is recommended for the reasons above, but if the team is more comfortable in Python or Go, the architecture translates.
12. **Sync direction at first run** — On first install, if both local and server have documents, which direction wins? Full server-to-local download, or merge?
