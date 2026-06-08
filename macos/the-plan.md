# InterlinedList Sync — macOS: Engineering Plan

## What the README Establishes

The app is a **native macOS menu bar daemon** that keeps a local folder in sync with the InterlinedList Documents feature (`interlinedlist.com/help/documents`). It runs continuously in the background, presents no Dock icon, and exposes controls through a menu bar extra.

---

## Technology Stack

| Concern | Technology | Rationale |
|---|---|---|
| Language | Swift 5.9+ | Native, fully async/await, first-class Apple framework access |
| UI — menu bar | AppKit `NSStatusItem` + `NSMenu` | Required for menu bar extras; no SwiftUI equivalent |
| UI — windows | SwiftUI (macOS 14 SDK) | Preferences, onboarding, status views |
| Lifecycle | `LSUIElement = YES` in `Info.plist` | Hides from Dock and App Switcher |
| Concurrency | `async/await`, `actor`, `@MainActor` | Structured concurrency throughout; no `DispatchQueue` |
| HTTP / API | `URLSession` (async) | No third-party HTTP libs; wraps the interlinedlist.com REST API |
| Auth | `ASWebAuthenticationSession` (OAuth 2.0 PKCE) | Standard Mac browser-based OAuth; no embedded webview |
| Token storage | Keychain (`SecItemAdd` / `SecItemCopyMatching`) | Credentials never touch `UserDefaults` or disk |
| Preferences | `UserDefaults` / `@AppStorage` | Non-sensitive settings (folder path, interval, etc.) |
| Local file watching | `FSEventStreamCreate` | Recursive, low-latency directory watching |
| Remote change detection | Polling (REST) or webhook (if API supports it) | To be confirmed — see open questions |
| Notifications | `UserNotifications` framework | System notifications for sync events and errors |
| File I/O | `FileManager` + `NSFileCoordinator` | Safe concurrent file access, iCloud-aware |
| Build system | Swift Package Manager | No Xcode project file required for CLI/CI; Xcode opens `.package` natively |
| Testing | XCTest, `MockURLProtocol` | Unit + integration tests; no network in CI |
| Signing / distribution | Developer ID + Notarisation **or** Mac App Store (TBC) | Affects sandbox entitlements — see open questions |

---

## Proposed Architecture

```
InterlinedSyncApp (SwiftUI App)
├── App/
│   ├── InterlinedSyncApp.swift       — @main, wires dependencies
│   └── AppDelegate.swift             — NSApplicationDelegate, lifecycle
│
├── MenuBar/
│   └── StatusItemController.swift    — NSStatusItem, NSMenu, window management
│
├── Sync/
│   ├── SyncEngine.swift              — actor; orchestrates poll/push cycle
│   ├── SyncState.swift               — @Observable state surfaced to UI
│   ├── ConflictResolver.swift        — conflict policy (TBD strategy)
│   └── ChangeSet.swift               — value type describing a batch of diffs
│
├── API/
│   ├── InterlinedListClient.swift    — actor; URLSession wrapper for REST API
│   └── Models.swift                  — Codable DTOs matching API schema
│
├── Auth/
│   └── AuthManager.swift             — OAuth 2.0 PKCE via ASWebAuthenticationSession
│
├── Storage/
│   ├── KeychainManager.swift         — token persistence
│   ├── PreferencesManager.swift      — folder path, interval, enabled flag
│   └── LaunchAgentManager.swift      — optional: install/remove Login Item
│
├── FileSystem/
│   ├── FSEventsWatcher.swift         — FSEventStreamCreate wrapper
│   └── DocumentMapper.swift          — maps API doc schema ↔ local file paths
│
└── UI/
    ├── OnboardingView.swift          — first-run auth + folder selection
    └── PreferencesView.swift         — SwiftUI settings window
```

### Data Flow

```
FSEventsWatcher ──local change──▶ SyncEngine ──upload──▶ InterlinedListClient
                                       │
                         timer/webhook─┤
                                       ▼
                              InterlinedListClient ──fetch──▶ DocumentMapper ──▶ FileManager
                                       │
                              ConflictResolver (on overlap)
                                       │
                              StatusItemController ◀── SyncState (via @Observable)
                                       │
                              UserNotifications
```

---

## Feature Breakdown

| Feature | Approach |
|---|---|
| Menu bar extra | `NSStatusItem` with template SF Symbol; `NSMenu` with status, last-sync time, pause/resume, preferences, quit |
| Background sync | `SyncEngine` actor driven by `FSEventsWatcher` (local) + timer poll (remote) |
| Folder selection | Open panel via `NSOpenPanel`; store security-scoped bookmark in `UserDefaults` |
| Sync interval config | Preference (default: 5 min); drives `AsyncTimerSequence` or `Task.sleep` loop |
| Notifications | `UNUserNotificationCenter`; one permission request at first launch |
| Login item | `SMAppService.mainApp` (macOS 13+) — one-line enable/disable from Preferences |
| Error surfacing | Menu bar icon badge + notification + error detail in status popover |
| Security | HTTPS only; token in Keychain; App Sandbox if App Store; no logging of token values |

---

## SOLID Principles Applied

### Single Responsibility
- `SyncEngine` orchestrates the sync cycle only — no file I/O, no network calls directly.
- `InterlinedListClient` owns the HTTP transport layer only.
- `StatusItemController` owns menu bar presentation only; it reads `SyncState`, never writes it.
- `AppDelegate` wires dependencies at startup; no business logic lives there.

### Open/Closed
- `ConflictResolver` is defined as a protocol; swap strategies (last-write-wins, remote-wins, manual) without touching `SyncEngine`.
- `TokenStorage` is a protocol; `KeychainManager` is one implementation — tests inject a mock.

### Liskov Substitution
- Any mock injected in tests must honour the full contract of the protocol it replaces.
- `MockURLProtocol` satisfies the same `URLSession` request/response contract as the real stack.

### Interface Segregation
- Callers of `InterlinedListClient` that only read documents depend on `DocumentFetching`, not the full client type.
- `SyncEngine` depends on `FileWriting` and `FileReading` role protocols, not `FileManager` directly.

### Dependency Inversion
- All dependencies (network client, keychain, preferences, file watcher) are injected at construction time.
- No bare singletons in business logic; `@EnvironmentObject` / `@Environment` at the SwiftUI view layer only.

---

## Open Questions

Answers to these questions are required before implementation begins.

**1. API capabilities**
Does the API support webhooks or WebSockets for push notification of remote document changes, or is polling the only option? Does it expose a delta/diff endpoint (changes since timestamp X), or does the client always fetch the full document list? Are there rate limits to respect?

**2. Sync direction**
Is sync fully bidirectional (edits flow both ways), or is one direction primary? If bidirectional, how should conflicts be handled when a document is edited both locally and remotely before the next sync cycle? Options: last-write-wins, remote-wins, local-wins, or flag for manual resolution.

**3. Document format on disk**
What file format should documents be written as locally — Markdown (`.md`), plain text (`.txt`), JSON, or something else? Does the API return raw content or structured data that needs transformation before writing?

**4. Folder / file structure**
Does each InterlinedList "document" map to exactly one file? Can documents have attachments or sub-items that would make a folder-per-document layout preferable?

**5. Distribution channel**
Mac App Store or direct download (notarised DMG)? This is load-bearing — App Store requires full App Sandbox, which restricts file access to user-chosen paths via `NSOpenPanel` bookmarks. Direct distribution allows a more relaxed entitlement set.

**6. Minimum macOS version**
Is macOS 14 (Sonoma) the floor, or should this support macOS 13 (Ventura) or older? `SMAppService` (Login Items), `@Observable`, and SwiftData are all 14+.

**7. Authentication flow**
Does `interlinedlist.com` expose an OAuth 2.0 PKCE endpoint? If so, what are the client ID, redirect URI scheme, and required scopes? Or is this a username/password flow requiring a different approach?

**8. Existing code**
Is there any existing Swift code in `macos/` to build on, or is this greenfield from an empty Swift Package?

**9. Multiple accounts**
Should the app support signing in to multiple InterlinedList accounts, or is one account per installation sufficient?

**10. Offline behaviour**
When the machine has no network connection, should the app queue local changes and replay them when connectivity returns, or simply skip the sync cycle and retry on the next interval?

---

## Implementation Phases (draft — subject to answers above)

### Phase 1 — Scaffold + Auth
- Swift Package structure with all layers as empty files
- `AuthManager` with OAuth 2.0 PKCE via `ASWebAuthenticationSession`
- `KeychainManager` storing and retrieving the access token
- `OnboardingView` (sign-in + folder picker)
- Manual smoke test: sign in, token persists across restarts

### Phase 2 — API Client + Document Fetch
- `InterlinedListClient` with fetch-all-documents endpoint
- `Models.swift` Codable DTOs
- `DocumentMapper` writing fetched documents to the local folder
- Unit tests with `MockURLProtocol`

### Phase 3 — Local File Watching + Upload
- `FSEventsWatcher` wrapping `FSEventStreamCreate`
- `SyncEngine` detecting local changes and uploading via `InterlinedListClient`
- `ChangeSet` value type diffing local vs remote state

### Phase 4 — Menu Bar UI + Notifications
- `StatusItemController` with full `NSMenu`
- `SyncState` `@Observable` driving menu item labels and icon badge
- `UserNotifications` for sync completion and errors

### Phase 5 — Preferences + Login Item
- `PreferencesView` (folder, interval, launch-at-login toggle)
- `SMAppService.mainApp` for Login Item registration
- `PreferencesManager` persisting all settings

### Phase 6 — Conflict Resolution + Error Handling
- `ConflictResolver` protocol + chosen default strategy
- Comprehensive error mapping to `SyncError` enum
- Retry logic with exponential backoff for transient network failures

### Phase 7 — Packaging + Distribution
- Code signing, entitlements, privacy manifest (`PrivacyInfo.xcprivacy`)
- Notarisation pipeline (Developer ID) **or** App Store submission workflow
- TestFlight beta distribution before public release
