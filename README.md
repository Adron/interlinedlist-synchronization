# InterlinedList Synchronization

[![macOS CI](https://github.com/Adron/interlinedlist-synchronization/actions/workflows/macos-ci.yml/badge.svg)](https://github.com/Adron/interlinedlist-synchronization/actions/workflows/macos-ci.yml)
[![Windows CI](https://github.com/Adron/interlinedlist-synchronization/actions/workflows/windows-ci.yml/badge.svg)](https://github.com/Adron/interlinedlist-synchronization/actions/workflows/windows-ci.yml)
[![Linux CI](https://github.com/Adron/interlinedlist-synchronization/actions/workflows/linux-ubuntu-ci.yml/badge.svg)](https://github.com/Adron/interlinedlist-synchronization/actions/workflows/linux-ubuntu-ci.yml)
[![Release](https://github.com/Adron/interlinedlist-synchronization/actions/workflows/release.yml/badge.svg)](https://github.com/Adron/interlinedlist-synchronization/actions/workflows/release.yml)

A Dropbox/OneDrive-style sync client for [InterlinedList](https://interlinedlist.com) documents.
The application runs in the background, watches a local folder, and bidirectionally syncs your
InterlinedList `.md` documents and folders between your machine and the web app — without any
manual export or import step.

## Platform support

| Platform | Minimum version | Install method | Stack |
|----------|----------------|----------------|-------|
| **macOS** | macOS 13 (Ventura) | DMG / PKG from Releases (upcoming) | Swift 5.9 + AppKit/SwiftUI |
| **Windows** | Windows 10 build 19041 (2004) | MSIX from Releases (upcoming) | C# 13 / .NET 9 + WPF |
| **Linux** | Ubuntu 22.04 LTS | `.deb` or Snap (upcoming) | Rust stable + GTK4/libadwaita |

> **Note:** v1.0 has not yet been released. The Releases page will be updated when signed,
> distributable installers are available.

## Quick start

Each platform has its own packaging guide with install and build-from-source instructions:

- macOS: [macos/PACKAGING.md](macos/PACKAGING.md)
- Windows: [windows/PACKAGING.md](windows/PACKAGING.md)
- Linux: [linux-ubuntu/PACKAGING.md](linux-ubuntu/PACKAGING.md)

For end-user install and troubleshooting detail, see the per-platform READMEs:

- [macos/README.md](macos/README.md)
- [windows/README.md](windows/README.md)
- [linux-ubuntu/README.md](linux-ubuntu/README.md)

## Architecture

There is no shared runtime code between platforms. Each directory — `macos/`, `windows/`,
`linux-ubuntu/` — is an entirely self-contained native application written in the idiomatic stack
for its operating system. The macOS client is a Swift/AppKit menu bar daemon; the Windows client
is a C#/.NET 9 WPF tray application; the Linux client is a Rust daemon with a GTK4/libadwaita
tray menu and systemd user service.

This approach was chosen deliberately. A cross-platform framework (Electron, Tauri, Qt) would
allow code sharing but would sacrifice the native secret store, tray API, file-system notification
API, notification framework, and OS packaging format on each platform. Because the application
surface area is small — a tray icon, a preferences window, a background sync loop — duplicating
the logic three times in native code costs less than the ongoing maintenance of cross-platform
abstractions that leak on every major OS release.

All three implementations hit the same InterlinedList REST API (`https://interlinedlist.com/api`),
use the same Bearer auth token format (`il_tok_...` from `POST /api/auth/sync-token`), and follow
the same conflict resolution policy: remote wins, with the conflicting local copy renamed to
`<name>.conflict-<timestamp>.md`.

## Feature matrix

| Feature | macOS | Windows | Linux |
|---------|-------|---------|-------|
| Bidirectional sync (push + pull) | Yes | Yes | Yes |
| File watcher (push on save) | FSEvents | FileSystemWatcher | inotify |
| Delta pull (`/api/documents/sync`) | Yes | Yes | Yes |
| Conflict resolution: remote-wins + conflict copy | Yes | Yes | Yes |
| Offline queue (changes queued when offline) | No (v1) | No (v1) | Yes |
| Per-document concurrency (no global lock) | Yes (actor isolation) | Yes (per-doc semaphore) | Yes (tokio tasks) |
| System tray / menu bar UX | Menu bar extra | Notification area icon | Ayatana AppIndicator |
| Native OS notifications | UserNotifications | Windows App SDK toasts | libnotify |
| Credentials in native secret store | Keychain | Windows Credential Manager | GNOME Keyring |
| Launch at login / startup | SMAppService | MSIX startupTask | systemd user service |
| Preferences window | SwiftUI | WPF | libadwaita |

## Build from source

Each platform requires its own toolchain. One-liner builds below; see the per-platform README for
full prerequisites.

**macOS** — requires Xcode 16 and Swift Package Manager:

```bash
bash macos/scripts/build-app.sh
```

**Windows** — requires .NET 9 SDK:

```bash
dotnet build windows/InterlinedSync.sln -c Release
```

**Linux** — requires Rust stable toolchain and GTK4/libadwaita development libraries:

```bash
cargo build --release --manifest-path linux-ubuntu/Cargo.toml
```

## Test counts (as of v1.0-RC)

| Platform | Unit tests | Integration tests |
|----------|-----------|-------------------|
| macOS | 144 | 5 |
| Windows | 165 | 5 |
| Linux | 58 | 5 |

Integration tests require live API credentials; see [CONTRIBUTING.md](CONTRIBUTING.md) for the
`.env` setup.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for toolchain setup, test conventions, branch policy, and
commit message style.

## API contract

A snapshot of the InterlinedList REST API as validated against the live service is in
[API_CONTRACT.md](API_CONTRACT.md). Reference this document when making changes to any platform's
API client to avoid silent regressions against live API drift.

## License

MIT — see [LICENSE](LICENSE).
