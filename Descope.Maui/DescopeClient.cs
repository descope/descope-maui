using Descope.Maui.Internal;

namespace Descope.Maui;

/// <summary>
/// Signs users in with a Descope Flow in the system browser (OIDC authorization code with PKCE),
/// and owns the <see cref="DescopeSessionManager"/> that keeps their session.
/// </summary>
/// <remarks>
/// Create one per app, or register it with <c>builder.Services.AddDescope(...)</c>:
/// <code>
/// var descope = new DescopeClient(new DescopeOptions { ProjectId = "P...", RedirectUri = "myapp://auth" });
/// var session = await descope.LoginAsync(new DescopeLoginOptions { FlowId = "sign-up-or-in" });
/// </code>
/// </remarks>
public class DescopeClient
{
    private readonly DescopeOptions _options;
    private readonly IDescopeBrowser _browser;
    private readonly OidcClient _oidc;

    /// <summary>The session manager for the signed in user.</summary>
    public DescopeSessionManager SessionManager { get; }

    /// <summary>
    /// Creates a client. Pass a custom <paramref name="storage"/>, <paramref name="browser"/> or
    /// <paramref name="httpClient"/> to replace the defaults.
    /// </summary>
    /// <exception cref="DescopeException">The options are invalid.</exception>
    public DescopeClient(DescopeOptions options, IDescopeSessionStorage? storage = null, IDescopeBrowser? browser = null, HttpClient? httpClient = null)
        : this(options, storage, browser, httpClient, TimeProvider.System)
    {
    }

    internal DescopeClient(DescopeOptions options, IDescopeSessionStorage? storage, IDescopeBrowser? browser, HttpClient? httpClient, TimeProvider timeProvider)
    {
        options.Validate();
        _options = options;
        _browser = browser ?? new WebAuthenticatorBrowser();
        _oidc = new OidcClient(options, httpClient ?? new HttpClient());
        SessionManager = new DescopeSessionManager(storage ?? new SecureSessionStorage(options.ProjectId), _oidc.RefreshAsync, timeProvider);
    }

    /// <summary>
    /// Runs a Descope Flow in the system browser and, once the user finishes it, exchanges the
    /// authorization code for tokens and hands the new session to <see cref="SessionManager"/>.
    /// </summary>
    /// <exception cref="DescopeException">
    /// The user closed the browser (<see cref="DescopeErrorCode.Cancelled"/>), or the flow or token exchange failed.
    /// </exception>
    public async Task<DescopeSession> LoginAsync(DescopeLoginOptions? loginOptions = null, CancellationToken cancellationToken = default)
    {
        var state = Pkce.RandomString();
        var (verifier, challenge) = Pkce.Generate();
        var url = _oidc.AuthorizeUrl(state, challenge, loginOptions);

        var result = await AuthenticateAsync(url, cancellationToken).ConfigureAwait(false);

        if (result.TryGetValue("error", out var error))
        {
            result.TryGetValue("error_description", out var description);
            throw new DescopeException(DescopeErrorCode.FlowFailed, description ?? $"The flow failed with {error}", error);
        }
        if (!result.TryGetValue("state", out var returnedState) || returnedState != state)
        {
            throw new DescopeException(DescopeErrorCode.StateMismatch, "The state returned by the flow doesn't match the request");
        }
        if (!result.TryGetValue("code", out var code) || string.IsNullOrEmpty(code))
        {
            throw new DescopeException(DescopeErrorCode.FlowFailed, "The flow redirected back without an authorization code");
        }

        var tokens = await _oidc.ExchangeCodeAsync(code, verifier, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(tokens.RefreshToken))
        {
            throw new DescopeException(DescopeErrorCode.ServerError, "The token endpoint didn't return a refresh token");
        }
        var session = new DescopeSession(tokens.AccessToken!, tokens.RefreshToken, tokens.IdToken);
        await SessionManager.ManageSessionAsync(session).ConfigureAwait(false);
        return session;
    }

    /// <summary>
    /// Revokes the refresh token and clears the session. The local session is cleared even if
    /// revoking fails, in which case the exception is rethrown afterwards.
    /// </summary>
    /// <param name="endBrowserSession">
    /// Also open Descope's end-session endpoint in the browser, signing the user out of the hosted
    /// flow. Only needed on Android, or on iOS when
    /// <see cref="DescopeOptions.PrefersEphemeralWebBrowserSession"/> is false.
    /// </param>
    /// <param name="cancellationToken">Cancels the revoke request.</param>
    public async Task LogoutAsync(bool endBrowserSession = false, CancellationToken cancellationToken = default)
    {
        await SessionManager.LoadSessionAsync().ConfigureAwait(false);
        DescopeSession? session;
        // a refresh in progress might rotate the refresh token, so wait for it and revoke the newest one
        using (await SessionManager.BlockRefreshesAsync(CancellationToken.None).ConfigureAwait(false))
        {
            session = SessionManager.Session;
            if (session == null) return;
            try
            {
                await _oidc.RevokeAsync(session.RefreshJwt, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await SessionManager.ClearSessionAsync().ConfigureAwait(false);
            }
        }
        if (endBrowserSession)
        {
            try
            {
                await AuthenticateAsync(_oidc.LogoutUrl(session.IdToken), cancellationToken).ConfigureAwait(false);
            }
            catch (DescopeException ex) when (ex.Code == DescopeErrorCode.Cancelled)
            {
                // the user closing the browser doesn't undo the local logout
            }
        }
    }

    /// <summary>
    /// Refreshes the session now, whether or not it's close to expiring.
    /// Use <see cref="DescopeSessionManager.RefreshSessionIfNeededAsync"/> to refresh only when needed.
    /// </summary>
    /// <exception cref="DescopeException">There's no session, or the refresh failed.</exception>
    public Task<DescopeSession> RefreshSessionAsync(CancellationToken cancellationToken = default) =>
        SessionManager.RefreshSessionAsync(cancellationToken);

    private async Task<IDictionary<string, string>> AuthenticateAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            return await _browser.AuthenticateAsync(url, new Uri(_options.RedirectUri), _options.PrefersEphemeralWebBrowserSession, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DescopeException(DescopeErrorCode.Cancelled, "The user closed the browser", innerException: ex);
        }
    }
}
