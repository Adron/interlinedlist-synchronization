using System.IO;
using System.IO.Abstractions;
using InterlinedSync.API.Models;
using InterlinedSync.FileSystem;
using Microsoft.Extensions.Logging;

namespace InterlinedSync.Sync;

public sealed class ConflictResolver : IConflictResolver
{
    private readonly IFileSystem _fileSystem;
    private readonly IFileMapper _fileMapper;
    private readonly ISyncStateRepository _repository;
    private readonly ILogger<ConflictResolver> _logger;

    public ConflictResolver(
        IFileSystem fileSystem,
        IFileMapper fileMapper,
        ISyncStateRepository repository,
        ILogger<ConflictResolver> logger)
    {
        _fileSystem = fileSystem;
        _fileMapper = fileMapper;
        _repository = repository;
        _logger = logger;
    }

    public ConflictDecision Decide(
        SyncStateRecord? record,
        string? localSha,
        DateTimeOffset? localModifiedAt,
        Document remote)
    {
        ArgumentNullException.ThrowIfNull(remote);

        if (record is null)
        {
            return localSha is null
                ? new ConflictDecision(ConflictAction.Pull, Reason: "new remote document")
                : new ConflictDecision(ConflictAction.Push, Reason: "new local document");
        }

        var localChanged = localSha is null
            ? true
            : !string.Equals(localSha, record.Sha256, StringComparison.Ordinal);
        var remoteChanged = remote.UpdatedAt > record.ServerUpdatedAt;

        return (localChanged, remoteChanged) switch
        {
            (false, false) => new ConflictDecision(ConflictAction.NoOp, Reason: "in sync"),
            (true, false) => new ConflictDecision(ConflictAction.Push, Reason: "local changed"),
            (false, true) => new ConflictDecision(ConflictAction.Pull, Reason: "remote changed"),
            (true, true) => new ConflictDecision(ConflictAction.ConflictCopyAndPull, Reason: "both diverged"),
        };
    }

    public async Task<ConflictDecision> ResolveConflictAsync(
        SyncStateRecord record,
        byte[]? localBytes,
        Document remote,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(remote);

        var conflictAt = DateTimeOffset.UtcNow;
        var conflictPath = _fileMapper.GetConflictPath(record.LocalPath, conflictAt);

        if (localBytes is { Length: > 0 })
        {
            try
            {
                var directory = _fileSystem.Path.GetDirectoryName(conflictPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    _fileSystem.Directory.CreateDirectory(directory);
                }
                await _fileSystem.File.WriteAllBytesAsync(conflictPath, localBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not write conflict copy at {Path}.", conflictPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Access denied writing conflict copy at {Path}.", conflictPath);
            }
        }
        else
        {
            _logger.LogInformation(
                "Conflict on {DocumentId}: no local bytes preserved (file already deleted locally).",
                record.Id);
        }

        await _repository.AppendLogAsync(
            "conflict.detected",
            $"id={record.Id}, path={record.LocalPath}, conflict_copy={conflictPath}",
            cancellationToken).ConfigureAwait(false);

        _logger.LogWarning(
            "Conflict detected for {DocumentId}: remote {RemoteUpdatedAt:O} diverged from local; saved conflict copy at {ConflictPath}.",
            record.Id,
            remote.UpdatedAt,
            conflictPath);

        return new ConflictDecision(
            ConflictAction.ConflictCopyAndPull,
            ConflictCopyPath: conflictPath,
            Reason: "both diverged");
    }
}
