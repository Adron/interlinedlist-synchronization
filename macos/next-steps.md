# Next Steps — InterlinedList Sync macOS

## Current State

Phase 4 (Menu Bar UI + Notifications) is complete. `swift test` passes: 70/70.

### What Phase 4 added

- `Sync/SyncState.swift` — added `SyncOutcome` value type and a `resumed()` transition
- `Sync/SyncEngine.swift` — `runCycle()` now returns a `SyncOutcome`; `syncNow()` reports
  success/failure to an injected `NotificationManager`; added `pause()` / `resume()`
- `Notifications/UserNotificationScheduling.swift` — protocol seam over `UNUserNotificationCenter`
- `Notifications/NotificationManager.swift` — actor; just-in-time authorization (HIG),
  gated by `PreferencesManager.notificationsEnabled`; posts completion (only when files
  changed), error, and conflict-copy notifications
- `MenuBar/SyncCoordinating.swift` — control-surface protocol the menu drives (`SyncEngine` conforms)
- `MenuBar/StatusItemController.swift` — observes live `SyncState` via Combine; menu reflects
  status, relative `lastSyncedAt` (`RelativeDateTimeFormatter`), Sync Now, Pause/Resume; template
  SF Symbol icon badges per status
- `Storage/PreferencesManager.swift` — added `notificationsEnabled` (default `true`)
- `App/AppDelegate.swift` — wires `SyncState`, `NotificationManager`, `SyncEngine`, and
  `StatusItemController` together; starts/pauses sync based on onboarding + preferences
- Tests: `NotificationManagerTests` (10), `StatusItemControllerTests` (8),
  `SyncEngineTests` notification cases (3), `MockNotificationCenter`

### Earlier state

Phase 2 (API Client + Document Fetch) was complete at 29/29; Phase 3 (file watching +
bidirectional sync) landed the `SyncEngine` actor, `ChangeSet`, `ConflictResolver`,
`FSEventsWatcher`, and their tests.

### What exists

- `Package.swift` — SPM package, macOS 13 minimum, no third-party deps
- Full folder structure scaffolded (all 7 phases represented)
- **Fully implemented**: `AuthManager`, `KeychainManager`, `PreferencesManager`, `AppDelegate`,
  `InterlinedSyncApp`, `StatusItemController`, `OnboardingView`,
  `InterlinedListClient`, `Models`, `DocumentMapper`
- **Stubs only**: `SyncEngine`, `SyncState`, `ConflictResolver`, `ChangeSet`,
  `FSEventsWatcher`, `LaunchAgentManager`, `PreferencesView`
- **Tests**: `AuthManagerTests` (3), `KeychainManagerTests` (2),
  `InterlinedListClientTests` (10), `DocumentMapperTests` (14) — all 29 pass

### Confirmed design decisions

- **Auth**: `POST /api/auth/login` with `{"email":"...","password":"..."}` → `{"token":"..."}`
- **File format**: Markdown (`.md`) — body is written as-is to disk
- **Filename**: sanitized `title.md`; document ID stored as extended attribute `com.interlinedlist.sync.documentID`
- **Sync**: bidirectional (push + pull); conflict resolution required
- **Minimum macOS**: 13 — uses `ObservableObject`+`@Published`, not `@Observable`

### API assumptions to verify before Phase 3

- List endpoint shape: assumed `{"documents": [...]}`; verify against live API
- Field names: assumed camelCase (`updatedAt` etc.); if snake_case, uncomment
  `keyDecodingStrategy = .convertFromSnakeCase` in `JSONDecoder.interlinedList()`
- Endpoint paths: `GET /api/documents`, `POST /api/documents`, `PUT /api/documents/{id}`,
  `DELETE /api/documents/{id}` — confirm these are correct

---

## Next Phase to Implement

### Phase 5 — Preferences + Login Item

Files to implement (currently stubs / partial):

1. **`UI/PreferencesView.swift`** — full settings form bound to `PreferencesManager`:
   - Sync folder (re-open `NSOpenPanel` via `selectSyncFolder()`), shown as a path
   - Sync interval (minutes; drives `pollIntervalSeconds`)
   - Launch at login toggle (see below)
   - **Notifications toggle** — bind to the new `PreferencesManager.notificationsEnabled`
     (added in Phase 4 but not yet surfaced in the UI)
2. **`Storage/LaunchAgentManager.swift`** — wrap `SMAppService.mainApp` (macOS 13+) for a
   one-line register/unregister Login Item; reflect `status` in the toggle
3. **Wire-up** — changing the interval should reach the running `SyncEngine`. Phase 4 reads
   `pollIntervalSeconds` once at construction; either rebuild the engine on change or add an
   engine method to update its poll cadence live.
4. **Tests** — `PreferencesManagerTests` (interval flooring, notifications default),
   `LaunchAgentManagerTests` (mock `SMAppService` behind a protocol seam)

### Phases 6–7 (in order)

1. `ConflictResolver` — user-selectable strategy (remote-wins is the current default) +
   retry/backoff for transient network failures; comprehensive `SyncError` mapping
2. Code signing, entitlements, `PrivacyInfo.xcprivacy`, notarization pipeline

### Carried-over API assumptions to verify before shipping

- List endpoint shape: assumed `{"documents": [...]}`; verify against live API
- Field names: assumed camelCase (`updatedAt` etc.); if snake_case, uncomment
  `keyDecodingStrategy = .convertFromSnakeCase` in `JSONDecoder.interlinedList()`
- Endpoint paths: `GET /api/documents`, `POST /api/documents`, `PUT /api/documents/{id}`,
  `DELETE /api/documents/{id}` — confirm these are correct
