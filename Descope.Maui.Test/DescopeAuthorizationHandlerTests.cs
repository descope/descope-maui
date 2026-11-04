using System.Net;
using Descope.Maui.Internal;
using Microsoft.Extensions.Time.Testing;

namespace Descope.Maui.Test;

public class DescopeAuthorizationHandlerTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Dictionary<string, string?> Authorization { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization[request.RequestUri!.GetLeftPart(UriPartial.Authority)] = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task SendsTheSessionJwtOnlyToTheGivenHostsOverHttps()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var manager = new DescopeSessionManager(new MemoryStorage(), (_, _) => Task.FromResult(new TokenResponse(null, null, null)), time, new FakeAppLifecycle());
        var session = new DescopeSession(Jwt.Session(time.GetUtcNow(), TimeSpan.FromMinutes(10)), Jwt.Refresh(time.GetUtcNow()));
        await manager.ManageSessionAsync(session);
        var inner = new RecordingHandler();
        var client = new HttpClient(new DescopeAuthorizationHandler(manager, "API.example.com") { InnerHandler = inner });

        await client.GetAsync("https://api.example.com/me");
        await client.GetAsync("http://api.example.com/me");
        await client.GetAsync("https://other.example.net/collect");

        Assert.Equal($"Bearer {session.SessionJwt}", inner.Authorization["https://api.example.com"]);
        Assert.Null(inner.Authorization["http://api.example.com"]);
        Assert.Null(inner.Authorization["https://other.example.net"]);
    }

    [Fact]
    public void RequiresAtLeastOneHost()
    {
        var manager = new DescopeSessionManager(new MemoryStorage(), (_, _) => Task.FromResult(new TokenResponse(null, null, null)), TimeProvider.System, new FakeAppLifecycle());

        Assert.Throws<ArgumentException>(() => new DescopeAuthorizationHandler(manager));
    }
}
