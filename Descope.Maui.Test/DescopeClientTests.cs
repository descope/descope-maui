using System.Net;
using Microsoft.Extensions.Time.Testing;

namespace Descope.Maui.Test;

public class DescopeClientTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly MemoryStorage _storage = new();
    private readonly FakeBrowser _browser = new();
    private readonly FakeHttpHandler _http = new();
    private readonly DescopeOptions _options = new() { ProjectId = Jwt.ProjectId, RedirectUri = "myapp://auth" };

    private DescopeClient CreateClient() => new(_options, _storage, _browser, new HttpClient(_http), _time);

    private void RespondWithTokens(object? sessionClaims = null) =>
        _http.Respond = (path, _) => Task.FromResult(path.EndsWith("/token")
            ? FakeHttpHandler.Json(new
            {
                access_token = Jwt.Session(_time.GetUtcNow(), TimeSpan.FromMinutes(10), sessionClaims),
                refresh_token = Jwt.Refresh(_time.GetUtcNow()),
                id_token = Jwt.Make(_time.GetUtcNow() + TimeSpan.FromMinutes(10), new { email = "andy@example.com", email_verified = true, name = "Andy" }),
            })
            : new HttpResponseMessage(HttpStatusCode.OK));

    [Fact]
    public void RejectsMissingConfiguration()
    {
        Assert.Throws<DescopeException>(() => new DescopeClient(new DescopeOptions { RedirectUri = "myapp://auth" }));
        Assert.Throws<DescopeException>(() => new DescopeClient(new DescopeOptions { ProjectId = Jwt.ProjectId, RedirectUri = "not a uri" }));
    }

    [Fact]
    public async Task LoginBuildsAuthorizeUrlWithFlowTenantAndCustomParameters()
    {
        RespondWithTokens();
        _options.FlowId = "default-flow";

        await CreateClient().LoginAsync(new DescopeLoginOptions
        {
            FlowId = "sign-up-or-in",
            Tenant = "T1",
            LoginHint = "andy@example.com",
            AdditionalParameters = { ["dynamic_val"] = "{\"fruit\":\"apple\"}", ["state"] = "attacker" },
        });

        var url = Assert.Single(_browser.Opened);
        Assert.Equal("https://api.descope.com/oauth2/v1/authorize", url.GetLeftPart(UriPartial.Path));
        Assert.Equal(Jwt.ProjectId, FakeBrowser.QueryParam(url, "client_id"));
        Assert.Equal("myapp://auth", FakeBrowser.QueryParam(url, "redirect_uri"));
        Assert.Equal("S256", FakeBrowser.QueryParam(url, "code_challenge_method"));
        Assert.Equal("sign-up-or-in", FakeBrowser.QueryParam(url, "flow"));
        Assert.Equal("T1", FakeBrowser.QueryParam(url, "tenant"));
        Assert.Equal("andy@example.com", FakeBrowser.QueryParam(url, "login_hint"));
        Assert.Equal("{\"fruit\":\"apple\"}", FakeBrowser.QueryParam(url, "dynamic_val"));
        Assert.NotEqual("attacker", FakeBrowser.QueryParam(url, "state"));
    }

    [Fact]
    public async Task LoginExchangesCodeAndStoresSession()
    {
        RespondWithTokens(new { tenants = new Dictionary<string, object> { ["T1"] = new { roles = new[] { "editor" } } } });

        var session = await CreateClient().LoginAsync();

        var (path, form) = Assert.Single(_http.Requests);
        Assert.Equal("/oauth2/v1/token", path);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("the-code", form["code"]);
        Assert.Equal("myapp://auth", form["redirect_uri"]);
        Assert.False(string.IsNullOrEmpty(form["code_verifier"]));

        Assert.Equal("U123", session.User.UserId);
        Assert.Equal("andy@example.com", session.User.Email);
        Assert.True(session.User.IsVerifiedEmail);
        Assert.Equal(["editor"], session.Roles("T1"));
        Assert.Same(session, _storage.Saved);
    }

    [Fact]
    public async Task LoginRejectsMismatchedState()
    {
        RespondWithTokens();
        _browser.Respond = _ => new Dictionary<string, string> { ["code"] = "the-code", ["state"] = "other" };

        var ex = await Assert.ThrowsAsync<DescopeException>(() => CreateClient().LoginAsync());

        Assert.Equal(DescopeErrorCode.StateMismatch, ex.Code);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task LoginReportsFlowError()
    {
        _browser.Respond = url => new Dictionary<string, string>
        {
            ["error"] = "access_denied",
            ["error_description"] = "User denied",
            ["state"] = FakeBrowser.QueryParam(url, "state")!,
        };

        var ex = await Assert.ThrowsAsync<DescopeException>(() => CreateClient().LoginAsync());

        Assert.Equal(DescopeErrorCode.FlowFailed, ex.Code);
        Assert.Equal("access_denied", ex.OAuthError);
        Assert.Equal("User denied", ex.Message);
    }

    [Fact]
    public async Task LoginIgnoresErrorRedirectWithoutMatchingState()
    {
        _browser.Respond = _ => new Dictionary<string, string> { ["error"] = "access_denied", ["error_description"] = "forged" };

        var ex = await Assert.ThrowsAsync<DescopeException>(() => CreateClient().LoginAsync());

        Assert.Equal(DescopeErrorCode.StateMismatch, ex.Code);
    }

    [Fact]
    public async Task LoginReportsBrowserClosed()
    {
        _browser.Respond = _ => throw new TaskCanceledException();

        var ex = await Assert.ThrowsAsync<DescopeException>(() => CreateClient().LoginAsync());

        Assert.Equal(DescopeErrorCode.Cancelled, ex.Code);
    }

    [Fact]
    public async Task LoginReportsTokenEndpointError()
    {
        _http.Respond = (_, _) => Task.FromResult(FakeHttpHandler.Json(new { error = "invalid_grant", error_description = "Code expired" }, HttpStatusCode.BadRequest));

        var ex = await Assert.ThrowsAsync<DescopeException>(() => CreateClient().LoginAsync());

        Assert.Equal(DescopeErrorCode.ServerError, ex.Code);
        Assert.Equal("invalid_grant", ex.OAuthError);
        Assert.Null(_storage.Saved);
    }

    [Fact]
    public async Task LogoutRevokesRefreshTokenAndClearsSession()
    {
        RespondWithTokens();
        var client = CreateClient();
        var session = await client.LoginAsync();
        _http.Requests.Clear();

        await client.LogoutAsync();

        var (path, form) = Assert.Single(_http.Requests);
        Assert.Equal("/oauth2/v1/revoke", path);
        Assert.Equal(session.RefreshJwt, form["token"]);
        Assert.Null(client.SessionManager.Session);
        Assert.Null(_storage.Saved);
    }

    [Fact]
    public async Task LogoutAfterLaunchLoadFoundNoSession()
    {
        RespondWithTokens();
        var client = CreateClient();
        Assert.Null(await client.SessionManager.LoadSessionAsync());
        var session = await client.LoginAsync();
        _http.Requests.Clear();

        await client.LogoutAsync();

        var (path, form) = Assert.Single(_http.Requests);
        Assert.Equal("/oauth2/v1/revoke", path);
        Assert.Equal(session.RefreshJwt, form["token"]);
        Assert.Null(client.SessionManager.Session);
        Assert.Null(_storage.Saved);
    }

    [Fact]
    public async Task LogoutRevokesTheCurrentSessionNotTheRestoredOne()
    {
        RespondWithTokens();
        var restored = new DescopeSession(Jwt.Session(_time.GetUtcNow(), TimeSpan.FromMinutes(5)), Jwt.Make(_time.GetUtcNow() + TimeSpan.FromDays(7)));
        _storage.Saved = restored;
        var client = CreateClient();
        Assert.Same(restored, await client.SessionManager.LoadSessionAsync());
        var current = await client.LoginAsync();
        Assert.NotEqual(restored.RefreshJwt, current.RefreshJwt);
        _http.Requests.Clear();

        await client.LogoutAsync();

        Assert.Equal(current.RefreshJwt, Assert.Single(_http.Requests).Form["token"]);
        Assert.Null(_storage.Saved);
    }

    [Fact]
    public async Task LogoutClearsSessionEvenWhenRevokeFails()
    {
        RespondWithTokens();
        var client = CreateClient();
        await client.LoginAsync();
        _http.Respond = (_, _) => throw new HttpRequestException("offline");

        var ex = await Assert.ThrowsAsync<DescopeException>(() => client.LogoutAsync());

        Assert.Equal(DescopeErrorCode.NetworkError, ex.Code);
        Assert.Null(client.SessionManager.Session);
        Assert.Null(_storage.Saved);
    }

    [Fact]
    public async Task LogoutCanEndBrowserSession()
    {
        RespondWithTokens();
        var client = CreateClient();
        var session = await client.LoginAsync();
        _browser.Respond = _ => new Dictionary<string, string>();

        await client.LogoutAsync(endBrowserSession: true);

        var url = _browser.Opened.Last();
        Assert.Equal("https://api.descope.com/oauth2/v1/logout", url.GetLeftPart(UriPartial.Path));
        Assert.Equal(session.IdToken, FakeBrowser.QueryParam(url, "id_token_hint"));
        Assert.Equal("myapp://auth", FakeBrowser.QueryParam(url, "post_logout_redirect_uri"));
    }

    [Fact]
    public async Task AppClientIdUsesProjectRoutesForEveryRequest()
    {
        RespondWithTokens();
        var clientId = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{Jwt.ProjectId}:SA2app"));
        _options.ClientId = clientId;
        var client = CreateClient();

        await client.LoginAsync();
        await client.RefreshSessionAsync();
        _browser.Respond = _ => new Dictionary<string, string>();
        await client.LogoutAsync(endBrowserSession: true);

        var authorize = _browser.Opened.First();
        Assert.Equal($"https://api.descope.com/oauth2/v1/{Jwt.ProjectId}/authorize", authorize.GetLeftPart(UriPartial.Path));
        Assert.Equal(clientId, FakeBrowser.QueryParam(authorize, "client_id"));
        Assert.Equal(
            [$"/oauth2/v1/{Jwt.ProjectId}/token", $"/oauth2/v1/{Jwt.ProjectId}/token", "/oauth2/v1/revoke"],
            _http.Requests.Select(r => r.Path));
        Assert.All(_http.Requests, r => Assert.Equal(clientId, r.Form["client_id"]));
        var logout = _browser.Opened.Last();
        Assert.Equal($"https://api.descope.com/oauth2/v1/{Jwt.ProjectId}/logout", logout.GetLeftPart(UriPartial.Path));
        Assert.Equal(clientId, FakeBrowser.QueryParam(logout, "client_id"));
        Assert.Equal("SA2app", FakeBrowser.QueryParam(logout, "ssoAppId"));
    }

    [Fact]
    public async Task CustomClientIdIsResolvedByDescopeOnLogout()
    {
        RespondWithTokens();
        _options.ClientId = "my-custom-client";
        var client = CreateClient();
        await client.LoginAsync();
        _browser.Respond = _ => new Dictionary<string, string>();

        await client.LogoutAsync(endBrowserSession: true);

        var logout = _browser.Opened.Last();
        Assert.Equal($"https://api.descope.com/oauth2/v1/{Jwt.ProjectId}/logout", logout.GetLeftPart(UriPartial.Path));
        Assert.Equal("my-custom-client", FakeBrowser.QueryParam(logout, "client_id"));
        Assert.Null(FakeBrowser.QueryParam(logout, "ssoAppId"));
    }

    [Fact]
    public async Task RefreshReportsTimeoutAsNetworkError()
    {
        RespondWithTokens();
        var client = CreateClient();
        await client.LoginAsync();
        _http.Respond = (_, _) => throw new TaskCanceledException("timed out");

        var ex = await Assert.ThrowsAsync<DescopeException>(() => client.RefreshSessionAsync());

        Assert.Equal(DescopeErrorCode.NetworkError, ex.Code);
    }

    [Fact]
    public async Task RefreshUsesRotatedRefreshTokenUntilLogout()
    {
        RespondWithTokens();
        var client = CreateClient();
        await client.LoginAsync();
        var rotated = Jwt.Make(_time.GetUtcNow() + TimeSpan.FromDays(20));
        _http.Respond = (path, _) => Task.FromResult(path.EndsWith("/token")
            ? FakeHttpHandler.Json(new { access_token = Jwt.Session(_time.GetUtcNow(), TimeSpan.FromMinutes(10)), refresh_token = rotated })
            : new HttpResponseMessage(HttpStatusCode.OK));

        var refreshed = await client.RefreshSessionAsync();
        _http.Requests.Clear();
        await client.LogoutAsync();

        Assert.Equal(rotated, refreshed.RefreshJwt);
        Assert.Equal(rotated, Assert.Single(_http.Requests).Form["token"]);
    }

    [Fact]
    public async Task LogoutWaitsForRefreshInProgressAndRevokesTheRotatedToken()
    {
        RespondWithTokens();
        var client = CreateClient();
        await client.LoginAsync();
        var rotated = Jwt.Make(_time.GetUtcNow() + TimeSpan.FromDays(20));
        var refreshStarted = new TaskCompletionSource();
        var refreshGate = new TaskCompletionSource();
        _http.Respond = async (path, _) =>
        {
            if (!path.EndsWith("/token")) return new HttpResponseMessage(HttpStatusCode.OK);
            refreshStarted.SetResult();
            await refreshGate.Task;
            return FakeHttpHandler.Json(new { access_token = Jwt.Session(_time.GetUtcNow(), TimeSpan.FromMinutes(10)), refresh_token = rotated });
        };
        _http.Requests.Clear();

        var refresh = client.RefreshSessionAsync();
        await refreshStarted.Task;
        var logout = client.LogoutAsync();
        await Task.Delay(50);
        Assert.False(logout.IsCompleted);
        refreshGate.SetResult();
        await refresh;
        await logout;

        var revoke = Assert.Single(_http.Requests, r => r.Path == "/oauth2/v1/revoke");
        Assert.Equal(rotated, revoke.Form["token"]);
        Assert.Null(client.SessionManager.Session);
        Assert.Null(_storage.Saved);
    }

    [Fact]
    public async Task RefreshSessionKeepsRefreshTokenWhenNotRotated()
    {
        RespondWithTokens();
        var client = CreateClient();
        var original = await client.LoginAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
        var newSessionJwt = Jwt.Session(_time.GetUtcNow(), TimeSpan.FromMinutes(10));
        _http.Respond = (_, _) => Task.FromResult(FakeHttpHandler.Json(new { access_token = newSessionJwt }));

        var refreshed = await client.RefreshSessionAsync();

        Assert.Equal("refresh_token", _http.Requests.Last().Form["grant_type"]);
        Assert.Equal(original.RefreshJwt, _http.Requests.Last().Form["refresh_token"]);
        Assert.Equal(newSessionJwt, refreshed.SessionJwt);
        Assert.Equal(original.RefreshJwt, refreshed.RefreshJwt);
        Assert.Equal(original.IdToken, refreshed.IdToken);
        Assert.Same(refreshed, _storage.Saved);
    }
}
