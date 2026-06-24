using System.IO.Abstractions.TestingHelpers;
using FluentAssertions;
using InterlinedSync.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InterlinedSync.Tests.Storage;

public class AccountStoreTests
{
    private const string Path = @"C:\app\account.json";

    private static AccountStore Build(MockFileSystem fs)
        => new(fs, NullLogger<AccountStore>.Instance, Path);

    [Fact]
    public async Task GetSignedInEmailAsync_ReturnsNull_WhenFileMissing()
    {
        var fs = new MockFileSystem();
        var store = Build(fs);

        var email = await store.GetSignedInEmailAsync();

        email.Should().BeNull();
    }

    [Fact]
    public async Task SetSignedInEmailAsync_PersistsAndRoundTrips()
    {
        var fs = new MockFileSystem();
        var store = Build(fs);

        await store.SetSignedInEmailAsync("alice@example.com");
        var roundTripped = await store.GetSignedInEmailAsync();

        roundTripped.Should().Be("alice@example.com");
        fs.File.Exists(Path).Should().BeTrue();
    }

    [Fact]
    public async Task ClearAsync_DeletesFile()
    {
        var fs = new MockFileSystem();
        var store = Build(fs);
        await store.SetSignedInEmailAsync("alice@example.com");

        await store.ClearAsync();

        fs.File.Exists(Path).Should().BeFalse();
        (await store.GetSignedInEmailAsync()).Should().BeNull();
    }

    [Fact]
    public async Task GetSignedInEmailAsync_ReturnsNull_WhenFileCorrupt()
    {
        var fs = new MockFileSystem();
        fs.AddFile(Path, new MockFileData("{ this is not valid json"));
        var store = Build(fs);

        var email = await store.GetSignedInEmailAsync();

        email.Should().BeNull();
    }
}
