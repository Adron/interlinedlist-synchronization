using FluentAssertions;
using InterlinedSync.Configuration;
using InterlinedSync.UI;
using Xunit;

namespace InterlinedSync.Tests.UI;

/// <summary>
/// Pure-string tests for the startup-failure message helper. These exercise
/// the formatting contract so the dialog content stays stable for support
/// staff who triage user reports against it.
/// </summary>
public class StartupFailureReporterTests
{
    private const string LogDir = @"C:\Users\Test\AppData\Local\interlinedlist-sync\logs";

    [Fact]
    public void FormatMessage_IncludesStageInOpeningSentence()
    {
        var actual = StartupFailureReporter.FormatMessage(
            stage: "building host",
            exception: new InvalidOperationException("boom"),
            logDirectory: LogDir);

        actual.Should().Contain("(building host)");
    }

    [Fact]
    public void FormatMessage_IncludesExceptionTypeAndMessage()
    {
        var ex = new InvalidOperationException("the database is locked");

        var actual = StartupFailureReporter.FormatMessage("starting background services", ex, LogDir);

        actual.Should().Contain("System.InvalidOperationException");
        actual.Should().Contain("the database is locked");
    }

    [Fact]
    public void FormatMessage_WalksOneLevelOfInnerException()
    {
        var inner = new IOException("disk full");
        var outer = new InvalidOperationException("could not write state", inner);

        var actual = StartupFailureReporter.FormatMessage("starting background services", outer, LogDir);

        actual.Should().Contain("InvalidOperationException");
        actual.Should().Contain("could not write state");
        actual.Should().Contain("Caused by:");
        actual.Should().Contain("IOException");
        actual.Should().Contain("disk full");
    }

    [Fact]
    public void FormatMessage_IncludesLogDirectoryPath()
    {
        var actual = StartupFailureReporter.FormatMessage(
            stage: "Dispatcher",
            exception: new Exception("oops"),
            logDirectory: LogDir);

        actual.Should().Contain("Log files:");
        actual.Should().Contain(LogDir);
    }

    [Fact]
    public void FormatMessage_NullException_ProducesGenericMessage()
    {
        var actual = StartupFailureReporter.FormatMessage(
            stage: "building host",
            exception: null,
            logDirectory: LogDir);

        actual.Should().Contain("(building host)");
        actual.Should().Contain("unknown error");
        actual.Should().Contain(LogDir);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FormatMessage_RejectsBlankStage(string? stage)
    {
        var act = () => StartupFailureReporter.FormatMessage(stage!, new Exception(), LogDir);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FormatMessage_RejectsBlankLogDirectory(string? logDir)
    {
        var act = () => StartupFailureReporter.FormatMessage("Dispatcher", new Exception(), logDir!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GetDefaultLogDirectory_PointsAtLocalAppDataInterlinedSyncLogs()
    {
        var actual = StartupFailureReporter.GetDefaultLogDirectory();

        actual.Should().EndWith(
            Path.Combine(AppConstants.AppDataFolderName, AppConstants.LogsFolderName));
        actual.Should().Contain(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    [Fact]
    public void DialogTitle_IsHumanReadableAndMentionsApp()
    {
        // Catches accidental drift to a developer-y string in the future.
        StartupFailureReporter.DialogTitle.Should().Contain("InterlinedList Sync");
    }
}
