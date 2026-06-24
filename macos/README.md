# InterlinedList Sync — macOS

A native macOS menu bar application that bidirectionally syncs your
[InterlinedList](https://interlinedlist.com) documents to a local folder.
It runs silently in the background with no Dock icon, surfacing controls through a menu bar extra.

**Minimum requirement:** macOS 13 (Ventura)

**Stack:** Swift 5.9, AppKit (`NSStatusItem`), SwiftUI (preferences window),
FSEvents (file watching), `URLSession` (API), Keychain (token storage),
Swift Package Manager (build)

## Install

> **Note:** Signed, distributable installers are not yet available. The GitHub Releases page will
> list a DMG and PKG when v1.0 ships. Until then, build from source (see below).

When releases are available, download `InterlinedSync.dmg` or `InterlinedSync.pkg` from the
[Releases](https://github.com/Adron/interlinedlist-synchronization/releases) page.

<details>
<summary>Gatekeeper warning for unsigned builds</summary>

If you install an unsigned build (built locally without a Developer ID certificate), macOS will
show "InterlinedSync.app cannot be opened because it is from an unidentified developer."

To open it:

1. In Finder, right-click **InterlinedSync.app** and choose **Open**.
2. Click **Open** in the dialog that appears.

You only need to do this once per install. Subsequent launches proceed without the dialog.

</details>

## Build from source

**Prerequisites:** Xcode 16 (installs Swift and SPM automatically).

```bash
# Clone the repo
git clone https://github.com/Adron/interlinedlist-synchronization.git
cd interlinedlist-synchronization

# Build a release .app bundle
bash macos/scripts/build-app.sh

# Output: macos/build/InterlinedSync.app
```

To also build a `.pkg` installer:

```bash
bash macos/scripts/build-pkg.sh
# Output: macos/build/InterlinedSync-<version>.pkg
```

For details on signed and notarized builds (Developer ID, notarization secrets), see
[PACKAGING.md](PACKAGING.md).

## First run

1. Double-click **InterlinedSync.app** (or the PKG installer will handle this).
2. An onboarding window opens. Enter your InterlinedList email and password and click **Sign In**.
3. Choose a local folder to sync into (default: `~/InterlinedList Sync`).
4. The menu bar icon appears. The initial sync begins automatically.

<details>
<summary>Keychain prompt on first sign-in</summary>

macOS will ask: "InterlinedList Sync wants to use the login keychain." Click **Always Allow**
so the app can retrieve your token on subsequent launches without prompting again.

</details>

<details>
<summary>Login item approval (macOS 13+)</summary>

When you enable **Launch at Login** in Preferences, macOS may show a notification:
"InterlinedList Sync added to Login Items." You can manage this in
**System Settings → General → Login Items & Extensions**.

</details>

## Where files are stored

| What | Location |
|------|----------|
| Auth token | macOS Keychain (login keychain, service `interlinedsync`) |
| Preferences (`UserDefaults`) | `~/Library/Preferences/com.interlinedlist.sync.plist` |
| Security-scoped bookmark for sync folder | Stored in `UserDefaults` under `syncFolderBookmark` |
| Log output | Console.app — filter by subsystem `com.interlinedlist.sync` |

The application does not write config files to `~/Library/Application Support`. Settings are
stored entirely in `UserDefaults` and the Keychain.

## Running tests

```bash
cd macos
swift test
```

Integration tests against the live API require a `.env` file at the repo root — see
[CONTRIBUTING.md](../CONTRIBUTING.md) for setup.

## Troubleshooting

**The menu bar icon does not appear after launch.**
Check Console.app for crash logs from `InterlinedSync`. If you built without signing, check that
Gatekeeper has allowed the app (see "Gatekeeper warning" above).

**"Sign In" fails with an authentication error.**
Verify your email and password work at [interlinedlist.com](https://interlinedlist.com).
The app uses `POST /api/auth/sync-token` — not your web session cookie.

**Documents are not syncing.**
Open the menu bar menu and check the status line. If it shows "Offline", your machine has no
network access. If it shows an error, click **Preferences** to see more detail.
Check Console.app for log messages from the `SyncEngine` subsystem.

**Sync folder path shows as blank in Preferences after a restart.**
The security-scoped bookmark may have become stale. Re-select the folder in Preferences →
**Choose Folder** to re-create the bookmark.

**Conflict copies accumulate in the sync folder.**
Files named `<name>.conflict-<timestamp>.md` were created because both your local copy and the
server copy changed before the next sync cycle. Review them and delete the copies you do not
need.
