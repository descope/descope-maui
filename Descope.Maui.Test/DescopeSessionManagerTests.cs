using Descope.Maui.Internal;
using Microsoft.Extensions.Time.Testing;

namespace Descope.Maui.Test;

public class DescopeSessionManagerTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly MemoryStorage _storage = new();
    private int _refreshCount;
    private TaskCompletionSource? _refreshGate;
    private Exception? _refreshError;

    private DescopeSessionManager CreateManager() => new(_storage, RefreshAsync, _time) { PeriodicCheckFrequency = TimeSpan.Zero };

    private async Task<TokenResponse> RefreshAsync(string refreshJwt, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _refreshCount);
        if (_refreshGate != null) await _refreshGate.Task;
        if (_refreshError != null) throw _refreshError;
        return new TokenResponse(Jwt.Session(_time.GetUtcNow(), TimeSpan.FromMinutes(10)), null, null);
    }

    private DescopeSession SessionExpiringIn(TimeSpan expiresIn) =>
        new(Jwt.Session(_time.GetUtcNow(), expiresIn), Jwt.Refresh(_time.GetUtcNow()));

    [Fact]
    public async Task LoadsSavedSession()
    {
        var saved = SessionExpiringIn(TimeSpan.FromMinutes(10));
        _storage.Saved = saved;
        var manager = CreateManager();

        Assert.Null(manager.Session);
        Assert.Same(saved, await manager.LoadSessionAsync());
        Assert.Same(saved, manager.Session);
    }

    [Fact]
    public void RefreshesOnlyCloseToExpiry()
    {
        var manager = CreateManager();

        Assert.False(manager.ShouldRefresh(SessionExpiringIn(TimeSpan.FromMinutes(5))));
        Assert.True(manager.ShouldRefresh(SessionExpiringIn(TimeSpan.FromSeconds(30))));
        Assert.True(manager.ShouldRefresh(SessionExpiringIn(TimeSpan.FromSeconds(-30))));
    }

    [Fact]
    public void DoesNotRefreshWithExpiredRefreshToken()
    {
        var manager = CreateManager();
        var session = new DescopeSession(
            Jwt.Session(_time.GetUtcNow(), TimeSpan.FromSeconds(-30)),
            Jwt.Make(_time.GetUtcNow() - TimeSpan.FromSeconds(1)));

        Assert.False(manager.ShouldRefresh(session));
    }

    [Fact]
    public async Task RefreshIfNeededSkipsFreshSession()
    {
        var manager = CreateManager();
        await manager.ManageSessionAsync(SessionExpiringIn(TimeSpan.FromMinutes(5)));

        await manager.RefreshSessionIfNeededAsync();

        Assert.Equal(0, _refreshCount);
    }

    [Fact]
    public async Task ConcurrentRefreshesShareOneRequest()
    {
        var manager = CreateManager();
        await manager.ManageSessionAsync(SessionExpiringIn(TimeSpan.FromSeconds(30)));
        _refreshGate = new TaskCompletionSource();

        var refreshes = Enumerable.Range(0, 5).Select(_ => manager.RefreshSessionIfNeededAsync()).ToList();
        _refreshGate.SetResult();
        await Task.WhenAll(refreshes);

        Assert.Equal(1, _refreshCount);
        Assert.False(manager.ShouldRefresh(manager.Session!));
    }

    [Fact]
    public async Task RefreshDoesNotResurrectClearedSession()
    {
        var manager = CreateManager();
        await manager.ManageSessionAsync(SessionExpiringIn(TimeSpan.FromSeconds(30)));
        _refreshGate = new TaskCompletionSource();

        var refresh = manager.RefreshSessionAsync();
        await manager.ClearSessionAsync();
        _refreshGate.SetResult();

        var ex = await Assert.ThrowsAsync<DescopeException>(() => refresh);
        Assert.Equal(DescopeErrorCode.NoSession, ex.Code);
        Assert.Null(manager.Session);
        Assert.Null(_storage.Saved);
    }

    [Fact]
    public async Task RaisesSessionChanged()
    {
        var manager = CreateManager();
        var events = new List<DescopeSession?>();
        manager.SessionChanged += (_, session) => events.Add(session);
        var session = SessionExpiringIn(TimeSpan.FromSeconds(30));

        await manager.ManageSessionAsync(session);
        await manager.RefreshSessionAsync();
        await manager.ClearSessionAsync();

        Assert.Equal(3, events.Count);
        Assert.Same(session, events[0]);
        Assert.NotSame(session, events[1]);
        Assert.Null(events[2]);
    }

    [Fact]
    public async Task PeriodicCheckRefreshesInBackground()
    {
        var manager = new DescopeSessionManager(_storage, RefreshAsync, _time);
        await manager.ManageSessionAsync(SessionExpiringIn(TimeSpan.FromSeconds(90)));

        _time.Advance(TimeSpan.FromSeconds(30));
        await WaitUntil(() => _refreshCount == 1);

        Assert.False(manager.ShouldRefresh(manager.Session!));
    }

    [Fact]
    public async Task PeriodicCheckPausesInBackgroundAndRunsOnResume()
    {
        var lifecycle = new FakeAppLifecycle();
        var manager = new DescopeSessionManager(_storage, RefreshAsync, _time, lifecycle);
        await manager.ManageSessionAsync(SessionExpiringIn(TimeSpan.FromMinutes(2)));

        lifecycle.Set(false);
        _time.Advance(TimeSpan.FromMinutes(3));
        // a session set while in the background doesn't start the periodic check either
        await manager.ManageSessionAsync(SessionExpiringIn(TimeSpan.FromMinutes(2)));
        _time.Advance(TimeSpan.FromMinutes(3));
        await Task.Delay(50);
        Assert.Equal(0, _refreshCount);

        lifecycle.Set(true);
        await WaitUntil(() => _refreshCount == 1);
    }

    [Fact]
    public async Task PeriodicCheckStopsAfterRejectedRefreshUntilResume()
    {
        _refreshError = new DescopeException(DescopeErrorCode.ServerError, "Descope returned 400 invalid_grant", "invalid_grant");
        var lifecycle = new FakeAppLifecycle();
        var manager = new DescopeSessionManager(_storage, RefreshAsync, _time, lifecycle);
        await manager.ManageSessionAsync(SessionExpiringIn(TimeSpan.FromSeconds(30)));

        _time.Advance(TimeSpan.FromSeconds(30));
        await WaitUntil(() => _refreshCount == 1);
        _time.Advance(TimeSpan.FromSeconds(30));
        _time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(50);
        Assert.Equal(1, _refreshCount);

        lifecycle.Set(false);
        lifecycle.Set(true);
        await WaitUntil(() => _refreshCount == 2);
        Assert.NotNull(manager.Session);
    }

    [Fact]
    public async Task PeriodicCheckRetriesAfterNetworkError()
    {
        _refreshError = new DescopeException(DescopeErrorCode.NetworkError, "The request to Descope failed");
        var manager = new DescopeSessionManager(_storage, RefreshAsync, _time, new FakeAppLifecycle());
        await manager.ManageSessionAsync(SessionExpiringIn(TimeSpan.FromSeconds(30)));

        _time.Advance(TimeSpan.FromSeconds(30));
        await WaitUntil(() => _refreshCount == 1);
        _time.Advance(TimeSpan.FromSeconds(30));
        await WaitUntil(() => _refreshCount == 2);
    }

    [Fact]
    public async Task RefreshWithoutSessionThrows()
    {
        var ex = await Assert.ThrowsAsync<DescopeException>(() => CreateManager().RefreshSessionAsync());

        Assert.Equal(DescopeErrorCode.NoSession, ex.Code);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}
