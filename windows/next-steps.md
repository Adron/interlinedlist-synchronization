# Next Steps — Phase 5 (Conflict Resolution)

Phase 4 (push) landed bidirectional sync with serialized pull/push pipelines, but
the engine still relies on simple "newest server wins" + "any local change wins"
heuristics. Phase 5 closes that gap.

## Goals

1. **Detect true conflicts.** A conflict is: the local file has changed since the
   last recorded SHA-256 (in `SyncStateRecord.Sha256`) **and** the server
   `UpdatedAt` is newer than the recorded `ServerUpdatedAt`. Both sides have
   diverged from the last known synced state.
2. **Default strategy: backup + last-writer-wins.** Match the macOS client:
   - Rename the local file to `<name>.conflict-<yyyyMMddHHmmss>.md`.
   - Write the server version to the canonical path.
   - Append a `conflict.detected` row to `sync_log`.
   - Toast notification: "Conflict on <name> — local copy preserved."
3. **`IConflictStrategy` extension point.** Future strategies (server-wins,
   local-wins, manual-merge) plug in without touching `SyncEngine`.

## Suggested shape

```csharp
public interface IConflictStrategy
{
    Task<ConflictResolution> ResolveAsync(
        Document remote,
        SyncStateRecord localRecord,
        byte[] localBytes,
        CancellationToken cancellationToken);
}

public sealed record ConflictResolution(
    string CanonicalPath,
    byte[] CanonicalBytes,
    string? BackupPath,
    byte[]? BackupBytes);
```

`SyncEngine.ReconcileAsync` calls the strategy when it detects the divergence
described above, then writes both the canonical and (optional) backup files via
the existing `WriteDocumentAsync` path.

## Test plan

- Local SHA differs from record AND server `UpdatedAt` newer than record → backup
  file is created at `<name>.conflict-<ts>.md`, canonical holds the server body.
- Local SHA differs but server `UpdatedAt` unchanged → push (already covered).
- Local SHA matches record but server `UpdatedAt` newer → pull overwrite (already
  covered).
- `IConflictStrategy` injected as `BackupAndServerWinsStrategy`; a substitute
  `ServerAlwaysWinsStrategy` validates the extension point.

## Open questions

- Is the conflict filename format `<name>.conflict-<ts>.md` final, or should it
  match a different macOS pattern (`<name> (conflict <ts>).md`)?
- Should the conflict toast be best-effort (current notification service), or
  should it block until acknowledged?
- Should the engine maintain a per-document conflict counter to suppress
  repeated toasts within a poll cycle?
