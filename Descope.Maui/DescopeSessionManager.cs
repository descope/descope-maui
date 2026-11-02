namespace Descope.Maui;

/// <summary>
/// Keeps the signed in user's session: persists it between launches, refreshes it shortly before
/// it expires, and clears it on logout.
/// </summary>
/// <remarks>
/// <para>
/// Get it from <see cref="DescopeClient.SessionManager"/>. On launch, call
/// <see cref="LoadSessionAsync"/> to restore the previous session:
/// </para>
/// <code>
/// var session = await descope.SessionManager.LoadSessionAsync();
/// if (session != null) { /* the user is still signed in */ }
/// </code>
/// <para>
/// While the app is in the foreground, every <see cref="PeriodicCheckFrequency"/> (30 seconds by default)
/// the manager refreshes the session if its JWT expires within <see cref="RefreshTriggerInterval"/>
/// (60 seconds by default). The check pauses while the app is in the background and runs right away when
/// it returns. Network errors are retried on the next check, but any other failure, such as a rejected
/// refresh token, stops the checks until the session changes or the app returns to the foreground.
/// Call <see cref="RefreshSessionIfNeededAsync"/> before calling your backend, or use
/// <see cref="DescopeAuthorizationHandler"/> to do it for every request.
/// </para>
/// </remarks>
public sealed class DescopeSessionManager : IDisposable
{
    private readonly IDescopeSessionStorage _storage;
    private readonly Func<string, CancellationToken, Task<Internal.TokenResponse>> _refresh;
    private readonly TimeProvider _timeProvider;
    private readonly Internal.IAppLifecycle _lifecycle;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _loadLock = new();
    private readonly object _timerLock = new();
    private Task? _loadTask;
    // bumped whenever a session is set or cleared, so a storage load that finishes afterwards doesn't overwrite it
    private int _sessionVersion;
    private ITimer? _timer;
    private TimeSpan _periodicCheckFrequency = TimeSpan.FromSeconds(30);

    internal DescopeSessionManager(
        IDescopeSessionStorage storage,
        Func<string, CancellationToken, Task<Internal.TokenResponse>> refresh,
        TimeProvider timeProvider,
        Internal.IAppLifecycle? lifecycle = null)
    {
        _storage = storage;
        _refresh = refresh;
        _timeProvider = timeProvider;
        _lifecycle = lifecycle ?? Internal.AppLifecycle.Shared;
        _lifecycle.ForegroundChanged += OnForegroundChanged;
    }

    /// <summary>
    /// The current session, or <c>null</c> if no user is signed in or <see cref="LoadSessionAsync"/>
    /// hasn't completed yet.
    /// </summary>
    public DescopeSession? Session { get; private set; }

    /// <summary>
    /// Raised after the session is set, refreshed or cleared. The argument is the new session, or
    /// <c>null</c> after it's cleared. It may be raised on a background thread.
    /// </summary>
    public event EventHandler<DescopeSession?>? SessionChanged;

    /// <summary>How long before the session JWT expires it's considered to need a refresh.</summary>
    public TimeSpan RefreshTriggerInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How often the session is checked for a refresh while the app is in the foreground, or
    /// <see cref="TimeSpan.Zero"/> to disable the periodic check.
    /// </summary>
    public TimeSpan PeriodicCheckFrequency
    {
        get => _periodicCheckFrequency;
        set
        {
            _periodicCheckFrequency = value;
            ResetTimer();
        }
    }

    /// <summary>
    /// Restores the session saved by a previous launch and returns the current session. The storage is
    /// read once, by the first call to succeed, and not at all after a session is set or cleared. A saved
    /// session whose refresh token has expired can't be refreshed, so it's removed and treated as no session.
    /// </summary>
    public async Task<DescopeSession?> LoadSessionAsync()
    {
        Task load;
        lock (_loadLock)
        {
            load = _loadTask ??= LoadCoreAsync();
        }
        try
        {
            await load.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // don't keep a failed load, such as a keychain error, so the next call reads the storage again
            lock (_loadLock)
            {
                if (_loadTask == load) _loadTask = null;
            }
            throw;
        }
        return Session;
    }

    private async Task LoadCoreAsync()
    {
        int version;
        lock (_loadLock)
        {
            version = _sessionVersion;
        }
        var session = await _storage.LoadSessionAsync().ConfigureAwait(false);
        if (session != null && IsRefreshTokenExpired(session))
        {
            Task? remove = null;
            lock (_loadLock)
            {
                // start the removal under the lock, so a login or logout that replaces the session runs after it
                if (_sessionVersion == version) remove = _storage.RemoveSessionAsync();
            }
            if (remove != null) await remove.ConfigureAwait(false);
            return;
        }
        if (session == null) return;
        lock (_loadLock)
        {
            // a session set or cleared while loading wins over the stored one
            if (_sessionVersion != version) return;
            Session = session;
        }
        ResetTimer();
    }

    // The session in memory replaces whatever is stored, so a failed or pending load no longer matters
    private void SetSession(DescopeSession? session)
    {
        lock (_loadLock)
        {
            Session = session;
            _sessionVersion++;
            if (_loadTask is not { IsCompletedSuccessfully: true }) _loadTask = Task.CompletedTask;
        }
    }

    /// <summary>
    /// Sets and persists a session. <see cref="DescopeClient.LoginAsync"/> calls this, so you
    /// only need it for a session you created yourself.
    /// </summary>
    public async Task ManageSessionAsync(DescopeSession session)
    {
        SetSession(session);
        await _storage.SaveSessionAsync(session).ConfigureAwait(false);
        ResetTimer();
        SessionChanged?.Invoke(this, session);
    }

    /// <summary>
    /// Clears the session and removes it from storage. <see cref="DescopeClient.LogoutAsync"/>
    /// calls this.
    /// </summary>
    public async Task ClearSessionAsync()
    {
        SetSession(null);
        await _storage.RemoveSessionAsync().ConfigureAwait(false);
        ResetTimer();
        SessionChanged?.Invoke(this, null);
    }

    /// <summary>
    /// Refreshes the session if its JWT expires within <see cref="RefreshTriggerInterval"/>.
    /// Concurrent calls share a single refresh.
    /// </summary>
    public async Task RefreshSessionIfNeededAsync(CancellationToken cancellationToken = default)
    {
        await LoadSessionAsync().ConfigureAwait(false);
        var current = Session;
        if (current == null || !ShouldRefresh(current)) return;
        await RefreshAsync(current, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refreshes the session now, whether or not it's close to expiring.
    /// </summary>
    /// <exception cref="DescopeException">There's no session, or the refresh failed.</exception>
    public async Task<DescopeSession> RefreshSessionAsync(CancellationToken cancellationToken = default)
    {
        await LoadSessionAsync().ConfigureAwait(false);
        var current = Session ?? throw new DescopeException(DescopeErrorCode.NoSession, "There's no session to refresh");
        return await RefreshAsync(current, cancellationToken).ConfigureAwait(false);
    }

    // Waits for a refresh in progress and blocks new ones until the result is disposed. A logout uses this so it
    // revokes the newest refresh token, and a refresh can't rotate it while it's being revoked.
    internal async Task<IDisposable> BlockRefreshesAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new RefreshBlock(_refreshLock);
    }

    private sealed class RefreshBlock(SemaphoreSlim refreshLock) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) refreshLock.Release();
        }
    }

    internal bool ShouldRefresh(DescopeSession session)
    {
        var now = _timeProvider.GetUtcNow();
        // no point trying if according to the device clock the refresh token has expired
        if (IsRefreshTokenExpired(session)) return false;
        // only refresh when the session JWT is close to expiring
        if (session.SessionToken.ExpiresAt - now > RefreshTriggerInterval) return false;
        // a refresh wouldn't extend the session past the refresh token's expiry
        if (session.RefreshToken != null && session.RefreshToken.ExpiresAt - session.SessionToken.ExpiresAt < TimeSpan.FromSeconds(1)) return false;
        return true;
    }

    private async Task<DescopeSession> RefreshAsync(DescopeSession current, CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // another caller refreshed or replaced the session while we waited
            if (Session == null)
            {
                throw new DescopeException(DescopeErrorCode.NoSession, "The session was cleared during the refresh");
            }
            if (Session.SessionJwt != current.SessionJwt)
            {
                return Session;
            }

            var tokens = await _refresh(current.RefreshJwt, cancellationToken).ConfigureAwait(false);
            var updated = current.WithUpdatedTokens(tokens.AccessToken!, tokens.RefreshToken, tokens.IdToken);

            // don't resurrect a session that was cleared or replaced during the request
            if (Session?.SessionJwt != current.SessionJwt)
            {
                return Session ?? throw new DescopeException(DescopeErrorCode.NoSession, "The session was cleared during the refresh");
            }
            await ManageSessionAsync(updated).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private bool IsRefreshTokenExpired(DescopeSession session) =>
        session.RefreshToken != null && session.RefreshToken.ExpiresAt <= _timeProvider.GetUtcNow();

    private void OnForegroundChanged(object? sender, bool isForeground)
    {
        if (isForeground)
        {
            // check right away, the session might have expired while the app was in the background
            ResetTimer(TimeSpan.Zero);
        }
        else
        {
            StopTimer();
        }
    }

    private void ResetTimer(TimeSpan? initialDelay = null)
    {
        lock (_timerLock)
        {
            _timer?.Dispose();
            _timer = null;
            var session = Session;
            if (session == null || IsRefreshTokenExpired(session) || !_lifecycle.IsForeground || _periodicCheckFrequency <= TimeSpan.Zero) return;
            _timer = _timeProvider.CreateTimer(_ => _ = PeriodicCheckAsync(), null, initialDelay ?? _periodicCheckFrequency, _periodicCheckFrequency);
        }
    }

    private void StopTimer()
    {
        lock (_timerLock)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private async Task PeriodicCheckAsync()
    {
        var current = Session;
        if (current == null) return;
        if (IsRefreshTokenExpired(current))
        {
            StopTimer();
            return;
        }
        try
        {
            await RefreshSessionIfNeededAsync().ConfigureAwait(false);
        }
        catch (DescopeException ex) when (ex.Code == DescopeErrorCode.NetworkError)
        {
            // retried on the next check
        }
        catch (Exception)
        {
            // the refresh token was probably rejected, so stop until the session changes or the app returns to
            // the foreground. The app sees the error the next time it calls RefreshSessionIfNeededAsync itself.
            if (ReferenceEquals(Session, current)) StopTimer();
        }
    }

    /// <summary>Stops the periodic refresh check.</summary>
    public void Dispose()
    {
        _lifecycle.ForegroundChanged -= OnForegroundChanged;
        StopTimer();
        _refreshLock.Dispose();
    }
}
