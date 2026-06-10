using System.Net;
using FluentAssertions;
using InterlinedSync.API;
using InterlinedSync.Auth;
using Moq;
using Xunit;

namespace InterlinedSync.Tests.API;

public class BearerTokenHandlerTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task SendAsync_AttachesBearerHeader_WhenTokenAvailable()
    {
        var auth = new Mock<IAuthProvider>();
        auth.Setup(a => a.GetTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("token-xyz");

        var capturing = new CapturingHandler();
        var bearer = new BearerTokenHandler(auth.Object) { InnerHandler = capturing };
        var invoker = new HttpMessageInvoker(bearer);

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://example.com/foo"), CancellationToken.None);

        capturing.LastRequest!.Headers.Authorization.Should().NotBeNull();
        capturing.LastRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        capturing.LastRequest.Headers.Authorization.Parameter.Should().Be("token-xyz");
    }

    [Fact]
    public async Task SendAsync_OmitsBearerHeader_WhenTokenNull()
    {
        var auth = new Mock<IAuthProvider>();
        auth.Setup(a => a.GetTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        var capturing = new CapturingHandler();
        var bearer = new BearerTokenHandler(auth.Object) { InnerHandler = capturing };
        var invoker = new HttpMessageInvoker(bearer);

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://example.com/foo"), CancellationToken.None);

        capturing.LastRequest!.Headers.Authorization.Should().BeNull();
    }
}
