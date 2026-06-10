# Next Steps — InterlinedList Sync macOS

## Current State

Phase 2 (API Client + Document Fetch) is complete. `swift test` passes: 29/29.

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

### Phase 3 — Local File Watching + Upload

Files to implement (currently stubs):

1. **`FileSystem/FSEventsWatcher.swift`** — wrap `FSEventStreamCreate`; emit
   `AsyncStream<[URL]>` of changed paths; handle start/stop lifecycle.
2. **`Sync/ChangeSet.swift`** — populate from diffing `DocumentMapper.localDocuments()`
   against the remote list returned by `InterlinedListClient.fetchDocuments()`.
3. **`Sync/SyncEngine.swift`** — actor driving the full poll/push cycle:
   - Pull: fetch remote docs → compute `ChangeSet.remoteChanges` → write via `DocumentMapper`
   - Push: consume `FSEventsWatcher` events → compute `ChangeSet.localChanges` →
     upload/delete via `InterlinedListClient`
   - Conflict detection via `ConflictResolver`
4. **`Sync/SyncState.swift`** — drive `status` and `lastSyncedAt` from `SyncEngine` results
5. **Tests** — `SyncEngineTests`, `FSEventsWatcherTests` (or integration test with a temp directory)

### Phase 4 — Menu Bar UI + Notifications (after Phase 3)

- `StatusItemController` — connect to live `SyncState` (currently hardcoded)
- `SyncState.@Published` properties driving menu item labels and icon badge
- `UNUserNotificationCenter` for sync completion and error events

### Phases 5–7 (in order)

1. `PreferencesView` + `SMAppService` login item
2. `ConflictResolver` — chosen default strategy + retry/backoff
3. Code signing, entitlements, notarization pipeline
