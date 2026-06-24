using System.IO;
using FluentAssertions;
using InterlinedSync.FileSystem;
using InterlinedSync.Sync;
using Moq;
using Xunit;

namespace InterlinedSync.Tests.FileSystem;

public sealed class FileMapperTests
{
    private static FileMapper Build(Mock<ISyncStateRepository>? repo = null)
    {
        repo ??= new Mock<ISyncStateRepository>();
        return new FileMapper(repo.Object);
    }

    [Theory]
    [InlineData("Simple Title", "Simple Title")]
    [InlineData("With/Slash", "With_Slash")]
    [InlineData("Has:Colon", "Has_Colon")]
    [InlineData("Many<>:\"/\\|?*Chars", "Many_________Chars")]
    [InlineData("Trailing dots...", "Trailing dots")]
    [InlineData("   spaced   ", "spaced")]
    [InlineData("", "untitled")]
    [InlineData("   ", "untitled")]
    public void SanitizeTitle_ReplacesIllegalChars(string input, string expected)
    {
        Build().SanitizeTitle(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("PRN")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    public void SanitizeTitle_AvoidsWindowsReservedNames(string reserved)
    {
        var sanitized = Build().SanitizeTitle(reserved);
        sanitized.Should().StartWith("_");
    }

    [Fact]
    public void SanitizeTitle_TruncatesVeryLongInput()
    {
        var huge = new string('a', 500);
        var sanitized = Build().SanitizeTitle(huge);
        sanitized.Length.Should().BeLessOrEqualTo(120);
    }

    [Fact]
    public void SanitizeTitle_StripsControlCharacters()
    {
        // Build a string with explicit control codes that survive source roundtripping.
        var input = "hithere";
        var sanitized = Build().SanitizeTitle(input);
        sanitized.Should().Be("hi_the_re_");
    }

    [Fact]
    public void GetLocalPath_AppendsMdExtension()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sync-root");
        var path = Build().GetLocalPath(folder, "My Document");

        path.Should().Be(Path.Combine(folder, "My Document.md"));
    }

    [Fact]
    public void GetLocalPath_RejectsEmptySyncFolder()
    {
        var act = () => Build().GetLocalPath(string.Empty, "anything");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task GetDocumentIdForPathAsync_DelegatesToRepository()
    {
        var repo = new Mock<ISyncStateRepository>();
        repo.Setup(r => r.GetByPathAsync("/p", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncStateRecord("doc-7", "/p", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "sha"));

        var id = await Build(repo).GetDocumentIdForPathAsync("/p");

        id.Should().Be("doc-7");
    }

    [Fact]
    public void GetConflictPath_AppendsTimestampedConflictSuffix()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sync-root");
        var original = Path.Combine(folder, "My Document.md");
        var conflictAt = new DateTimeOffset(2026, 6, 22, 13, 45, 7, TimeSpan.Zero);

        var path = Build().GetConflictPath(original, conflictAt);

        path.Should().Be(Path.Combine(folder, "My Document.conflict-20260622T134507.md"));
    }

    [Fact]
    public void GetConflictPath_NormalizesToUtc()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sync-root");
        var original = Path.Combine(folder, "Note.md");
        var conflictAt = new DateTimeOffset(2026, 6, 22, 9, 0, 0, TimeSpan.FromHours(-4));

        var path = Build().GetConflictPath(original, conflictAt);

        path.Should().EndWith("Note.conflict-20260622T130000.md");
    }

    [Fact]
    public void GetConflictPath_RejectsEmptyPath()
    {
        var act = () => Build().GetConflictPath(string.Empty, DateTimeOffset.UtcNow);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task GetPathForDocumentIdAsync_ReturnsNull_WhenMissing()
    {
        var repo = new Mock<ISyncStateRepository>();
        repo.Setup(r => r.GetByIdAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncStateRecord?)null);

        var path = await Build(repo).GetPathForDocumentIdAsync("missing");

        path.Should().BeNull();
    }
}
