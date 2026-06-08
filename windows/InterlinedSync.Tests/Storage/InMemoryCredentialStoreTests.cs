using FluentAssertions;
using InterlinedSync.Storage;
using Xunit;

namespace InterlinedSync.Tests.Storage;

public class InMemoryCredentialStoreTests
{
    [Fact]
    public async Task LoadTokenAsync_ReturnsNull_WhenNothingStored()
    {
        var store = new InMemoryCredentialStore();
        var token = await store.LoadTokenAsync();
        token.Should().BeNull();
    }

    [Fact]
    public async Task SaveTokenAsync_RoundTripsTheValue()
    {
        var store = new InMemoryCredentialStore();
        await store.SaveTokenAsync("abc123");
        var token = await store.LoadTokenAsync();
        token.Should().Be("abc123");
    }

    [Fact]
    public async Task SaveTokenAsync_OverwritesPreviousValue()
    {
        var store = new InMemoryCredentialStore();
        await store.SaveTokenAsync("first");
        await store.SaveTokenAsync("second");
        var token = await store.LoadTokenAsync();
        token.Should().Be("second");
    }

    [Fact]
    public async Task DeleteTokenAsync_ClearsTheValue()
    {
        var store = new InMemoryCredentialStore();
        await store.SaveTokenAsync("abc");
        await store.DeleteTokenAsync();
        var token = await store.LoadTokenAsync();
        token.Should().BeNull();
    }

    [Fact]
    public async Task DeleteTokenAsync_IsSafeWhenEmpty()
    {
        var store = new InMemoryCredentialStore();
        var act = async () => await store.DeleteTokenAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SaveTokenAsync_RejectsEmptyToken()
    {
        var store = new InMemoryCredentialStore();
        var act = async () => await store.SaveTokenAsync("");
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
