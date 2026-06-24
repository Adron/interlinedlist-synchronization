# InterlinedList Sync — Linux (Ubuntu)

A native Linux daemon that bidirectionally syncs your
[InterlinedList](https://interlinedlist.com) documents to a local folder.
It runs as a systemd user service, watches the filesystem with inotify, and surfaces controls
through a GTK4/libadwaita system tray menu (via `libayatana-appindicator3`).

**Minimum requirement:** Ubuntu 22.04 LTS (Jammy) — amd64 or arm64

**Stack:** Rust stable, GTK4 + libadwaita, Ayatana AppIndicator (tray),
inotify via the `notify` crate (file watching), `reqwest` (API),
GNOME Keyring / libsecret (token storage), SQLite via `rusqlite` (sync state),
systemd user service

## Install

> **Note:** Packages are not yet available in a public repository. The GitHub Releases page will
> list a `.deb` and a `.snap` when v1.0 ships.

### .deb package (recommended)

```bash
# Download the .deb from the Releases page, then:
sudo dpkg -i interlinedlist-sync_<version>_amd64.deb

# If dpkg reports missing dependencies, fix them with:
sudo apt-get install -f
```

The package installs the binary, a systemd user unit, an AppArmor profile (complain mode),
a `.desktop` autostart entry, and a sysctl drop-in that raises the inotify watch limit.

After install, enable and start the service:

```bash
systemctl --user enable --now interlinedlist-sync.service
```

### Snap (local unsigned build)

```bash
sudo snap install --classic --dangerous interlinedlist-sync_*.snap
```

The `--dangerous` flag is required for locally built, unsigned snaps. Snaps installed from the
Snap Store do not require it.

> **Note:** Classic confinement is required because the sync directory can be any user-chosen
> path. The snap equivalent of the `.deb` install is functionally identical in what it can access.

## Build from source

**Prerequisites:** Rust stable toolchain (`rustup update stable`) and GTK4/libadwaita dev libs.

```bash
# Install build dependencies
sudo apt-get install \
  libgtk-4-dev libadwaita-1-dev libayatana-appindicator3-dev \
  libdbus-1-dev libsecret-1-dev pkg-config

# Clone the repo
git clone https://github.com/Adron/interlinedlist-synchronization.git
cd interlinedlist-synchronization

# Build (release profile)
cargo build --release --manifest-path linux-ubuntu/Cargo.toml

# Binary is at:
# linux-ubuntu/target/release/interlinedlist-sync
```

> **Note:** Ubuntu 22.04 ships libadwaita 1.0.x, which does not include `PreferencesWindow`,
> `SpinRow`, or `SwitchRow`. Install a newer version before building:
>
> ```bash
> sudo add-apt-repository ppa:gnome-team/gnome-next
> sudo apt-get update && sudo apt-get install libadwaita-1-dev
> ```

For `.deb` packaging:

```bash
cargo install cargo-deb
cd linux-ubuntu
cargo deb -p interlinedlist-sync
# Output: linux-ubuntu/target/debian/interlinedlist-sync_<version>_amd64.deb
```

For Snap packaging, see [PACKAGING.md](PACKAGING.md).

## First run

1. The `.deb` postinstall script enables autostart via the `.desktop` entry.
2. On your next login (or immediately via `systemctl --user start interlinedlist-sync.service`),
   the tray icon appears.
3. Click the tray icon and choose **Settings** to sign in with your InterlinedList email and
   password and choose a local sync folder.

## Where files are stored

| What | Location |
|------|----------|
| Auth token | GNOME Keyring (libsecret, service `interlinedlist-sync`) |
| Configuration | `~/.config/interlinedlist-sync/config.toml` |
| Sync state database | `~/.local/share/interlinedlist-sync/state.db` |
| Offline operation queue | `~/.local/share/interlinedlist-sync/queue.db` |
| Transient cache | `~/.cache/interlinedlist-sync/` |
| PID file / socket | `$XDG_RUNTIME_DIR/interlinedlist-sync/` (cleared on logout) |
| systemd user unit | `/usr/lib/systemd/user/interlinedlist-sync.service` (installed by `.deb`) |
| AppArmor profile | `/etc/apparmor.d/usr.bin.interlinedlist-sync` (complain mode) |

`config.toml` is mode `0600` and readable only by the owning user. The auth token is never
written to disk in plaintext.

## systemd user service management

```bash
# Check status
systemctl --user status interlinedlist-sync.service

# Start / stop / restart
systemctl --user start interlinedlist-sync.service
systemctl --user stop interlinedlist-sync.service
systemctl --user restart interlinedlist-sync.service

# View logs (most recent 50 lines)
journalctl --user -u interlinedlist-sync.service -n 50

# Follow logs live
journalctl --user -u interlinedlist-sync.service -f

# Disable autostart
systemctl --user disable interlinedlist-sync.service
```

The service runs as your user account. No `sudo` is required for any of the above commands.

## Running tests

```bash
cd linux-ubuntu
cargo test --workspace
```

Integration tests against the live API require a `.env` file at the repo root — see
[CONTRIBUTING.md](../CONTRIBUTING.md) for setup.

## Troubleshooting

**The tray icon does not appear on GNOME Wayland.**
GNOME Wayland does not show AppIndicator tray icons by default. Install the
[AppIndicator extension](https://extensions.gnome.org/extension/615/appindicator-support/)
via GNOME Extensions or `sudo apt-get install gnome-shell-extension-appindicator`, then
re-enable the extension in **GNOME Extensions** and log out/in.

**"Failed to unlock keyring" or D-Bus errors at startup.**
The GNOME Keyring D-Bus session service must be running. If you log in via a display manager
(GDM, LightDM) this is automatic. If you start a Wayland/X11 session manually, ensure
`/usr/bin/gnome-keyring-daemon --start --components=secrets` runs before the sync service.
Check `journalctl --user -u interlinedlist-sync.service` for the exact error.

**AppArmor is blocking file access.**
The installed AppArmor profile runs in complain mode (logs but does not block). If you see
denials in `/var/log/syslog` or `journalctl -k`, you can switch to enforce mode after
reviewing them:

```bash
sudo aa-enforce /etc/apparmor.d/usr.bin.interlinedlist-sync
```

**inotify watch limit exceeded.**
The `.deb` postinstall script sets `fs.inotify.max_user_watches=524288` via
`/etc/sysctl.d/60-interlinedlist-sync.conf`. If you built from source without the package,
apply this manually:

```bash
echo "fs.inotify.max_user_watches=524288" | sudo tee /etc/sysctl.d/60-interlinedlist-sync.conf
sudo sysctl --system
```

**Documents are not syncing.**
Click the tray icon and check the status line. Review logs with `journalctl --user -u interlinedlist-sync.service`.
Common causes: auth token expired (sign out and sign back in via Settings), network offline
(the offline queue will drain on reconnect), or the sync folder path changed.

**Conflict copies accumulate in the sync folder.**
Files named `<name>.conflict-<timestamp>.md` were created because both your local copy and the
server copy changed before the next sync cycle. Review them and delete the copies you do not need.
The conflict strategy is configurable in `~/.config/interlinedlist-sync/config.toml` under
`[sync] conflict_resolution` (`remote-wins` or `conflict-copy`).
