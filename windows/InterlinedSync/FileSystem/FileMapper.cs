using System.Globalization;
using System.IO;
using System.Text;
using InterlinedSync.Sync;

namespace InterlinedSync.FileSystem;

/// <summary>
/// Default <see cref="IFileMapper"/>. Sanitization rules are deliberately
/// conservative — they target the intersection of NTFS and exFAT rules so the
/// sync folder works on USB drives and network shares too.
/// </summary>
public sealed class FileMapper : IFileMapper
{
    /// <summary>File extension used for every synced document.</summary>
    public const string DocumentExtension = ".md";

    private const int MaxBaseNameLength = 120;
    private const char ReplacementChar = '_';

    // Characters that NTFS / exFAT reject in any filename — same on every host
    // OS so the mapper produces stable names regardless of where tests run.
    private static readonly HashSet<char> InvalidNameChars =
    [
        '<', '>', ':', '"', '/', '\\', '|', '?', '*',
    ];

    // Windows-reserved device names — disallowed regardless of extension.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly ISyncStateRepository _repository;

    public FileMapper(ISyncStateRepository repository)
    {
        _repository = repository;
    }

    public string GetLocalPath(string syncFolder, string title)
    {
        ArgumentException.ThrowIfNullOrEmpty(syncFolder);
        var safeName = SanitizeTitle(title);
        return Path.Combine(syncFolder, safeName + DocumentExtension);
    }

    public async Task<string?> GetDocumentIdForPathAsync(string localPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(localPath);
        var record = await _repository.GetByPathAsync(localPath, cancellationToken).ConfigureAwait(false);
        return record?.Id;
    }

    public async Task<string?> GetPathForDocumentIdAsync(string documentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentId);
        var record = await _repository.GetByIdAsync(documentId, cancellationToken).ConfigureAwait(false);
        return record?.LocalPath;
    }

    public string GetConflictPath(string originalPath, DateTimeOffset conflictAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(originalPath);

        var directory = Path.GetDirectoryName(originalPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(originalPath);
        if (string.IsNullOrEmpty(stem))
        {
            stem = "untitled";
        }

        var timestamp = conflictAt.ToUniversalTime().ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture);
        var conflictName = $"{stem}.conflict-{timestamp}{DocumentExtension}";
        return string.IsNullOrEmpty(directory) ? conflictName : Path.Combine(directory, conflictName);
    }

    public string SanitizeTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "untitled";
        }

        var builder = new StringBuilder(title.Length);
        foreach (var ch in title)
        {
            if (char.IsControl(ch) || InvalidNameChars.Contains(ch))
            {
                builder.Append(ReplacementChar);
            }
            else
            {
                builder.Append(ch);
            }
        }

        // Trim trailing dots and spaces — illegal in Windows filenames.
        var trimmed = builder.ToString().Trim().TrimEnd('.', ' ');
        if (trimmed.Length == 0)
        {
            return "untitled";
        }

        if (trimmed.Length > MaxBaseNameLength)
        {
            trimmed = trimmed[..MaxBaseNameLength];
        }

        if (ReservedNames.Contains(trimmed))
        {
            trimmed = ReplacementChar + trimmed;
        }

        return trimmed;
    }
}
