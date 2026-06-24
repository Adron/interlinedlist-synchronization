# Next Steps — Phase 6 (Notifications + Error Handling)

Phase 5 (Conflict Resolution) shipped: remote-wins + conflict-copy strategy,
per-document concurrency via `ConcurrentDictionary<string, SemaphoreSlim>`, and
pre-PATCH `GET /api/documents/[id]` to detect server-side divergence. The
engine now writes `<name>.conflict-yyyyMMddTHHmmss.md` on conflict and emits
a `conflict.detected` row in `sync_log` (the log row is the hand-off point
the notification layer will pick up in Phase 6).

## Phase 5 — done

- `Sync/IConflictResolver.cs` + `Sync/ConflictResolver.cs` — pure `Decide`
  function covers all 5 permutations + `ResolveConflictAsync` writes the
  conflict copy.
- `SyncEngine` — per-doc `SemaphoreSlim` map (pull uses a dedicated key,
  push keys on document id once resolved, otherwise on the file path).
  Conflict branch fetches the live `updatedAt` before PATCH and routes to
  `ConflictResolver` when remote is newer than `SyncStateRecord.ServerUpdatedAt`.
- `SyncStateRecord.LastConflictAt` (nullable) + `documents.last_conflict_at`
  column with an idempotent `ALTER TABLE` migration in `SyncStateRepository`.
- `FileMapper.GetConflictPath(originalPath, conflictAt)` — UTC-normalized
  `yyyyMMddTHHmmss` timestamp.
- `Program.cs` — registered `IConflictResolver` as singleton.
- Tests: 14 new unit tests (108 → 122 green) covering all 5 conflict
  permutations, conflict-copy write, parallel-different-docs concurrency,
  and `GetConflictPath` formatting.

## Phase 6 goals

1. **Toast pipeline.** Drain `conflict.detected` (and other interesting
   `sync_log` rows) into `Microsoft.Windows.AppNotifications` toasts:
   "Conflict on <name> — local copy preserved at <conflict path>".
2. **Error funnel.** A typed `SyncErrorBus` so transient API/file errors
   surface as a single throttled tray-icon state change instead of one
   toast per retry.
3. **Tray badge.** Set tray icon overlay (red dot) on `SyncState.Error`,
   spinner on `Syncing`, clear on `Idle`.
4. **Retry policy.** `Polly`-style exponential backoff around `PushOnceAsync`
   for `5xx` and `HttpRequestException`, capped at 5 attempts; expose the
   final failure as a toast.

## Open questions for Phase 6

- Should `conflict.detected` toasts coalesce per poll cycle (one toast for
  N conflicts) or stay one-per-event?
- Should the tray icon offer a "View conflicts" menu item that opens the
  sync folder filtered to `*.conflict-*.md`?
- Where does the retry budget reset — per file, per session, or per poll cycle?
