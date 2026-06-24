using InterlinedSync.API.Models;

namespace InterlinedSync.Sync;

public enum ConflictAction
{
    NoOp,
    Push,
    Pull,
    ConflictCopyAndPull,
}

public sealed record ConflictDecision(
    ConflictAction Action,
    string? ConflictCopyPath = null,
    string? Reason = null);

public interface IConflictResolver
{
    ConflictDecision Decide(
        SyncStateRecord? record,
        string? localSha,
        DateTimeOffset? localModifiedAt,
        Document remote);

    Task<ConflictDecision> ResolveConflictAsync(
        SyncStateRecord record,
        byte[]? localBytes,
        Document remote,
        CancellationToken cancellationToken);
}
