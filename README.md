# InterlinedList Synchronization

[![macOS CI](https://github.com/Adron/interlinedlist-synchronization/actions/workflows/macos-ci.yml/badge.svg)](https://github.com/Adron/interlinedlist-synchronization/actions/workflows/macos-ci.yml)
[![Windows CI](https://github.com/Adron/interlinedlist-synchronization/actions/workflows/windows-ci.yml/badge.svg)](https://github.com/Adron/interlinedlist-synchronization/actions/workflows/windows-ci.yml)
[![Linux CI](https://github.com/Adron/interlinedlist-synchronization/actions/workflows/linux-ubuntu-ci.yml/badge.svg)](https://github.com/Adron/interlinedlist-synchronization/actions/workflows/linux-ubuntu-ci.yml)
[![Release](https://github.com/Adron/interlinedlist-synchronization/actions/workflows/release.yml/badge.svg)](https://github.com/Adron/interlinedlist-synchronization/actions/workflows/release.yml)

A monorepo containing three native desktop sync clients — one per platform — for the [InterlinedList](https://interlinedlist.com) web application.

## What This Is

[InterlinedList](https://interlinedlist.com) is a web application with a Documents feature that lets users create and manage documents online. This repository holds the tooling to bring those documents to the local machine: a background sync agent that watches a local folder, detects changes, and keeps the local filesystem and the web app in agreement through the [InterlinedList API](https://interlinedlist.com/help/api).

The sync client is built three times — once per operating system — as a fully native application. Each version lives in its own subdirectory and is developed with the idioms, toolchain, and UI conventions of its target platform rather than as a cross-platform wrapper. The result is a tight system-tray experience that feels at home on each OS.

## Platform Projects

| Platform | Directory | Status |
|----------|-----------|--------|
| macOS | [`macos/`](macos/) | In development |
| Linux (Ubuntu) | [`linux-ubuntu/`](linux-ubuntu/) | In development |
| Windows | [`windows/`](windows/) | In development |

## What Each Client Does

All three clients share the same functional scope:

- **System tray integration** — The application lives in the OS system tray, out of the way until needed.
- **Background synchronization** — A background process continuously watches the local document folder and pushes or pulls changes via the API as they occur.
- **Configuration** — Users choose which local folder to sync and how frequently the client polls for remote changes.
- **Notifications** — The OS notification system surfaces successful sync events and any errors without requiring the user to open a window.
- **Error handling** — Conflicts, network failures, and API errors are caught and reported clearly so the user can act on them.
- **Security** — Credentials are stored using each platform's native secret store (Keychain, Secret Service, Windows Credential Manager). All communication with the API is over HTTPS.
- **Performance** — The sync engine is designed to be lightweight: minimal CPU and memory use, and incremental transfers so only changed content moves over the wire.

## References

- Web app: https://interlinedlist.com
- Documents feature documentation: https://interlinedlist.com/help/documents
- Sync API reference: https://interlinedlist.com/help/api
