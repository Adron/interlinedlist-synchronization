# Linux Packaging — InterlinedList Sync

This document covers the two Linux packaging formats produced by CI:
`.deb` (primary) and `.snap` (M7, secondary).

---

## .deb package

Built by `cargo deb -p interlinedlist-sync`.

The package installs:

| Path | Purpose |
|------|---------|
| `/usr/bin/interlinedlist-sync` | Main binary |
| `/usr/lib/systemd/user/interlinedlist-sync.service` | systemd user unit |
| `/etc/apparmor.d/usr.bin.interlinedlist-sync` | AppArmor profile (complain mode) |
| `/usr/share/applications/interlinedlist-sync.desktop` | Autostart / app menu entry |
| `/etc/sysctl.d/60-interlinedlist-sync.conf` | Raises `fs.inotify.max_user_watches` to 524288 |

Maintainer scripts (`postinst` / `prerm`) are in
`packaging/maintainer-scripts/`. They reload sysctl and load/unload the
AppArmor profile on configure / remove.

**Ubuntu 22.04 (Jammy) note:** libadwaita 1.0.x in Jammy lacks
`PreferencesWindow`, `SpinRow`, and `SwitchRow`. The Settings dialog
requires a newer libadwaita. Install via the GNOME PPA before installing
the package:

```sh
sudo add-apt-repository ppa:gnome-team/gnome-next
sudo apt-get update
sudo apt-get install libadwaita-1-0
```

---

## Snap package

### Overview

`snap/snapcraft.yaml` uses `base: core22` and `confinement: classic`.

Classic confinement is required because the sync directory is a
user-chosen folder anywhere under `$HOME` (or on a mounted volume). The
snapd sandbox cannot grant access to arbitrary user-chosen paths via
plugs alone — that would require the `home` plug combined with strict
confinement, which does not cover paths outside the snap's default
home directory slice. Classic confinement sidesteps the sandbox entirely,
equivalent to how the `.deb` binary runs.

**Classic vs strict — the tradeoff:**

| | Classic | Strict |
|-|---------|--------|
| Filesystem access | Full (like a .deb) | Interface-bounded only |
| Snap Store review | Manual review required | Automated |
| Complexity | Low | High (requires AppArmor profile tuning inside the snap) |
| Suitable for this app | Yes — arbitrary sync folder | Only if sync dir is restricted to `$SNAP_USER_DATA` |

For a future strict-confinement variant, the sync directory would need to
be constrained to `$SNAP_USER_COMMON` (persisted across updates) and all
inotify watches would be limited to that prefix.

### Build locally

Requires snapcraft and either LXD/Multipass or a Ubuntu 22.04 host.

```sh
# On Ubuntu 22.04 (or in a 22.04 container):
sudo snap install snapcraft --classic

# Destructive mode — builds directly on the host, no VM:
cd linux-ubuntu
snapcraft --destructive-mode

# LXD mode (recommended for clean builds, requires LXD installed):
snapcraft --use-lxd
```

### Test a locally built snap

```sh
# Install unsigned local snap with classic confinement:
sudo snap install --classic --dangerous interlinedlist-sync_*.snap

# Verify it starts:
interlinedlist-sync --version

# Remove after testing:
sudo snap remove interlinedlist-sync
```

The `--dangerous` flag is required for locally built, unsigned snaps.
Snaps installed from the Snap Store are signed automatically by the Store
and do not need this flag.

### Snap Store publication

Publishing is a separate manual step not automated by CI. It requires
Snap Store credentials tied to the snap name `interlinedlist-sync`.

**One-time setup:**

```sh
# Log in and export credentials for CI use (run on a maintainer machine):
snapcraft login
snapcraft export-login \
  --snaps=interlinedlist-sync \
  --channels=stable \
  snap-store-creds.txt

# Add the contents of snap-store-creds.txt as a GitHub Actions secret
# named SNAPCRAFT_STORE_CREDENTIALS in the repo settings.
# Delete snap-store-creds.txt from the local machine after uploading.
```

**Upload and release:**

```sh
# After downloading the .snap artifact from the draft GitHub Release:
snapcraft upload --release=stable interlinedlist-sync_*.snap
```

Classic confinement requires a one-time manual review by the Snap Store
team before the snap can be published. Submit the review request at
https://forum.snapcraft.io/c/store-requests/. Once approved, subsequent
uploads proceed without review.

### Automating Snap Store upload in CI (future)

When `SNAPCRAFT_STORE_CREDENTIALS` is available as a repository secret,
add this step to `.github/workflows/release.yml` after the snap build:

```yaml
- name: Publish snap to Snap Store
  if: ${{ secrets.SNAPCRAFT_STORE_CREDENTIALS != '' }}
  env:
    SNAPCRAFT_STORE_CREDENTIALS: ${{ secrets.SNAPCRAFT_STORE_CREDENTIALS }}
  working-directory: linux-ubuntu
  run: snapcraft upload --release=stable interlinedlist-sync_*.snap
```

Do not add this step until the snap name is registered and classic
confinement has been approved by the Store.

---

## Release checklist

1. Tag the release: `git tag v0.x.0 && git push origin v0.x.0`
2. CI builds `.deb`, `.tar.gz`, and `.snap`; creates a draft GitHub Release.
3. Download and smoke-test each artifact on a clean Ubuntu 22.04 VM.
4. Snap test: `sudo snap install --classic --dangerous interlinedlist-sync_*.snap`
5. Publish the GitHub Release draft.
6. Upload the snap: `snapcraft upload --release=stable interlinedlist-sync_*.snap`
   (requires Store credentials and prior classic confinement approval)
