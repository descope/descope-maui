namespace Descope.Maui;

/// <summary>
/// The tokens and user from a successful login.
/// </summary>
/// <remarks>
/// In most apps the <see cref="DescopeSessionManager"/> owns the session: it persists it, refreshes
/// it before it expires and clears it on logout. Read the current one from
/// <see cref="DescopeSessionManager.Session"/>.
/// </remarks>
public sealed class DescopeSession
{
    /// <summary>The short-lived session JWT (the OIDC access token). Send it to your backend as a bearer token.</summary>
    public DescopeToken SessionToken { get; }

    /// <summary>The refresh token used to get a new session JWT.</summary>
    public string RefreshJwt { get; }

    /// <summary>The decoded refresh token, or <c>null</c> if it isn't a JWT.</summary>
    public DescopeToken? RefreshToken { get; }

    /// <summary>The OIDC ID token, used as the <c>id_token_hint</c> when ending the browser session.</summary>
    public string? IdToken { get; }

    /// <summary>The signed in user.</summary>
    public DescopeUser User { get; }

    /// <summary>Creates a session from the tokens returned by the Descope token endpoint.</summary>
    public DescopeSession(string sessionJwt, string refreshJwt, string? idToken = null)
    {
        SessionToken = new DescopeToken(sessionJwt);
        RefreshJwt = refreshJwt;
        RefreshToken = DescopeToken.TryParse(refreshJwt);
        IdToken = idToken;
        User = DescopeUser.FromIdToken(idToken, SessionToken.EntityId);
    }

    /// <summary>The session JWT value.</summary>
    public string SessionJwt => SessionToken.Jwt;

    /// <summary>The IDs of the tenants the user belongs to.</summary>
    public IReadOnlyList<string> TenantIds => SessionToken.TenantIds;

    /// <summary>The user's roles in a tenant, or project-level roles when <paramref name="tenant"/> is <c>null</c>.</summary>
    public IReadOnlyList<string> Roles(string? tenant = null) => SessionToken.Roles(tenant);

    /// <summary>The user's permissions in a tenant, or project-level permissions when <paramref name="tenant"/> is <c>null</c>.</summary>
    public IReadOnlyList<string> Permissions(string? tenant = null) => SessionToken.Permissions(tenant);

    // Descope only returns a new refresh token or ID token when it rotates them
    internal DescopeSession WithUpdatedTokens(string sessionJwt, string? refreshJwt, string? idToken) =>
        new(sessionJwt, refreshJwt ?? RefreshJwt, idToken ?? IdToken);

    /// <summary>A safe representation of the session with no secret data.</summary>
    public override string ToString() => $"DescopeSession(userId={User.UserId}, session={SessionToken})";
}
