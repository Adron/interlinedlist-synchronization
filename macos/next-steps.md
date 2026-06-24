# Next Steps — InterlinedList Sync macOS

## Current State

Phase 6 (Conflict Resolution + Error Handling) is complete. `swift test` passes: 149 tests
(144 unit + 5 live-API integration, which skip without credentials).

### What Phase 6 added

- `Sync/ConflictResolver.swift` — added a pure `ConflictResolver.decide(localChanged:remoteChanged:
  localExists:remoteExists:) -> ConflictDecision` decision table (`noOp`/`push`/`pull`/`conflictCopy`),
  separated from the side-effecting `RemoteWinsConflictResolver` (remote-wins + timestamped
  `<name>.conflict-YYYYMMDDTHHmmss.md` copy, matching the Windows convention)
- `Sync/DocumentGate.swift` — actor that serializes work per document ID via a per-ID tail-`Task`
  chain while letting different documents run in parallel; self-prunes idle chains
- `Sync/SyncEngine.swift` — applies (conflicts/remote/local) now fan out per-document through the
  gate via `withThrowingTaskGroup` (parallel across docs, serial per doc); conflict detection on
  push (`remoteIfNewerThanLedger`) reroutes a clobbering `PATCH` to the conflict resolver; offline
  pause/resume driven by an injected `NetworkMonitoring`; `.authExpired` / `.offline` /
  `.rateLimited` error routing with `Retry-After`-honoring backoff (exponential + jitter fallback,
  capped at 300 s)
- `Network/NetworkMonitor.swift` — `NetworkMonitoring` protocol, `NWPathMonitor`-backed production
  `NetworkMonitor`, and `StubNetworkMonitor` for deterministic tests
- `Sync/SyncState.swift` — `SyncError` gains `.authExpired` / `.offline` / `.rateLimited(retryAfter:)`;
  `SyncStatus` gains `.offline` / `.authExpired`; new `authExpired()` / `wentOffline()` /
  `cameOnline()` transitions
- `API/InterlinedListClient.swift` — 401 → `.authExpired`, 403 → `.notAuthenticated`,
  429 → `.rateLimited` with a `Retry-After` parser (integer seconds or HTTP-date)
- `Notifications/NotificationManager.swift` — `.auth` category + `notifyAuthExpired()`
- `MenuBar/StatusItemController.swift` — `.offline` (`wifi.slash`) and `.authExpired`
  (`exclamationmark.triangle.fill`) labels/icons; Sync Now disabled while offline
- `App/AppDelegate.swift` — wires the production `NetworkMonitor` into the engine; `.auth`
  notifications always allowed
- Tests: `ConflictResolverTests` (11), `NetworkMonitorTests` (5), `DocumentGateTests` (4),
  `SyncEngineTests` (+8: per-doc parallel, conflict-on-push, authExpired→state+notify, 429 retry,
  offline pause/resume), `InterlinedListClientTests` (+6: 401/403/429 + Retry-After parsing),
  `StatusItemControllerTests` (+2), `NotificationManagerTests` (+2); `FakeServer` gained 401/429
  fault injection

### Earlier state

Phase 5 (Preferences + Login Item) passed at 112 tests (107 unit + 5 live-API integration).

### What Phase 5 added

- `UI/PreferencesView.swift` — real settings window as a `TabView` (480×360, non-resizable):
  - **General**: sync folder display + "Choose…" (`NSOpenPanel`, security-scoped bookmark via
    `PreferencesManager.selectSyncFolder()`); "Launch at Login" toggle; sync-interval `Slider`
    (5–300 s, integer steps) bound to `pollIntervalSeconds`
  - **Account**: "Signed in as …" (from `accountEmail`) + "Sign Out"
  - **Notifications**: master "Enable notifications" toggle + per-category sub-toggles
    (completion / errors / conflict copies)
  - **Advanced**: log-file path + "Reveal in Finder", "Reset State" with confirmation alert,
    version/build from `Bundle.main`
- `UI/PreferencesViewModel.swift` — `@MainActor ObservableObject` driving the panels; owns the
  side-effecting account actions (sign out, reset state) as injected closures
- `Storage/PreferencesManager.swift` — added `PreferenceStoring` protocol seam; `pollIntervalSeconds`
  is now a stored, clamped (5–300 s), persisted, `@Published` value with a Combine publisher; added
  `launchAtLogin`, `notifyOnSyncCompletion/Errors/ConflictCopies`, `accountEmail`
- `Storage/LaunchAgentManager.swift` — `LoginItemManaging` protocol seam +
  `SMAppServiceLoginItemManager` (production) wrapping `SMAppService.mainApp`
- `Sync/SyncEngine.swift` — `bindPollInterval(to:)` Combine subscription + `updatePollInterval(_:)`
  reschedule the live poll timer; `resetLedger()` clears the in-memory ledger and sync marker
- `Sync/SyncState.swift` — added `resetSyncMarker()`
- `Notifications/NotificationManager.swift` — added per-`Category` gating predicate
- `MenuBar/StatusItemController.swift` — Preferences menu item opens (and re-activates) a single
  `NSHostingController`-backed window built from the injected `PreferencesViewModel`
- `App/AppDelegate.swift` — wires the login-item manager, builds the `PreferencesViewModel`, binds
  the engine's poll interval, routes notification categories, and implements the sign-out flow
  (clears Keychain token + ledger, returns to onboarding)
- `UI/OnboardingView.swift` — persists `accountEmail` on successful sign-in
- Tests: `PreferencesViewTests` (10), `PreferencesManagerTests` (6), `LaunchAgentManagerTests` (3),
  `SyncEngineTests.testSyncEngine_pickUpNewIntervalLive`, two notification-category cases;
  `MockLoginItemManager`, `MockPreferencesManager`

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

### Phase 7 — Packaging & Distribution

- Code signing, entitlements (the security-scoped bookmark + login item need the App Sandbox
  entitlements once sandboxed), `PrivacyInfo.xcprivacy`, notarization pipeline

### Open follow-ups from Phase 6

- **User-selectable conflict strategy** — only remote-wins ships today. Local-wins / newest-wins
  could be surfaced in Preferences and persisted via `PreferencesManager`; the `ConflictResolving`
  seam is already in place.
- **Conflict edge cases not yet covered**: a local *rename* during a conflict produces a conflict
  copy under the old filename (the canonical file then moves to the new remote title); simultaneous
  local-and-remote *delete* is treated as `noOp` by the decision table but the two-tombstone path
  isn't exercised end-to-end. Both warrant follow-up tests before shipping.
- **Push-side conflict detection cost**: `remoteIfNewerThanLedger` re-fetches the full document list
  per pushed edit. Once the API exposes a cheap per-document `HEAD`/`updatedAt` probe, swap it in.
- **Rate-limit backoff** resets only on the next successful cycle; there is no surfaced countdown in
  the menu beyond the status string.

### Open follow-ups from Phase 5

- The "Reveal in Finder" log path is a placeholder location; wire it to the real logging
  destination once a logger lands.
- `SMAppServiceLoginItemManager` can surface a System Settings approval prompt the first time it
  registers; the toggle reverts and shows guidance on failure but cannot detect "pending approval"
  state distinctly from "enabled" via `SMAppService.status` alone.
- Security-scoped bookmarks only matter under the App Sandbox (Phase 7); until then folder access
  works without `startAccessingSecurityScopedResource()`.

### Carried-over API assumptions to verify before shipping

- List endpoint shape: assumed `{"documents": [...]}`; verify against live API
- Field names: assumed camelCase (`updatedAt` etc.); if snake_case, uncomment
  `keyDecodingStrategy = .convertFromSnakeCase` in `JSONDecoder.interlinedList()`
- Endpoint paths: `GET /api/documents`, `POST /api/documents`, `PUT /api/documents/{id}`,
  `DELETE /api/documents/{id}` — confirm these are correct
