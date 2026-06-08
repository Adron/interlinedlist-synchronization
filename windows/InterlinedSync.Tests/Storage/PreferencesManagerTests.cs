using System.IO.Abstractions.TestingHelpers;
using FluentAssertions;
using InterlinedSync.Configuration;
using InterlinedSync.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InterlinedSync.Tests.Storage;

public class PreferencesManagerTests
{
    private const string PrefsPath = @"C:\app\appsettings.json";

    private static PreferencesManager Build(MockFileSystem fs)
        => new(fs, NullLogger<PreferencesManager>.Instance, PrefsPath);

    [Fact]
    public async Task LoadAsync_ReturnsDefaults_WhenFileMissing()
    {
        var fs = new MockFileSystem();
        var manager = Build(fs);

        var prefs = await manager.LoadAsync();

        prefs.PollIntervalSeconds.Should().Be(AppConstants.DefaultPollIntervalSeconds);
        prefs.AutoStart.Should().BeFalse();
        prefs.SyncFolder.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SaveAsync_PersistsAndIsReadBack()
    {
        var fs = new MockFileSystem();
        var manager = Build(fs);
        var prefs = new SyncPreferences
        {
            SyncFolder = @"C:\sync",
            PollIntervalSeconds = 45,
            AutoStart = true,
        };

        await manager.SaveAsync(prefs);
        var loaded = await manager.LoadAsync();

        loaded.SyncFolder.Should().Be(@"C:\sync");
        loaded.PollIntervalSeconds.Should().Be(45);
        loaded.AutoStart.Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_CreatesParentDirectory()
    {
        var fs = new MockFileSystem();
        var manager = Build(fs);

        await manager.SaveAsync(new SyncPreferences { SyncFolder = @"C:\sync" });

        fs.Directory.Exists(@"C:\app").Should().BeTrue();
        fs.File.Exists(PrefsPath).Should().BeTrue();
    }

    [Fact]
    public async Task LoadAsync_ReturnsDefaults_WhenFileIsCorrupt()
    {
        var fs = new MockFileSystem();
        fs.AddFile(PrefsPath, new MockFileData("{ this is not json"));

        var manager = Build(fs);
        var prefs = await manager.LoadAsync();

        prefs.PollIntervalSeconds.Should().Be(AppConstants.DefaultPollIntervalSeconds);
    }

    [Fact]
    public void PreferencesFilePath_ReturnsConfiguredPath()
    {
        var fs = new MockFileSystem();
        var manager = Build(fs);
        manager.PreferencesFilePath.Should().Be(PrefsPath);
    }

    [Fact]
    public async Task SaveAsync_RejectsNull()
    {
        var fs = new MockFileSystem();
        var manager = Build(fs);
        var act = async () => await manager.SaveAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
