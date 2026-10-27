namespace Descope.Maui;

/// <summary>
/// The kind of failure a <see cref="DescopeException"/> reports.
/// </summary>
public enum DescopeErrorCode
{
    /// <summary><see cref="DescopeOptions"/> are missing or invalid.</summary>
    InvalidConfiguration,
    /// <summary>The user closed the browser before the flow finished.</summary>
    Cancelled,
    /// <summary>The flow redirected back with an OIDC error, or without an authorization code.</summary>
    FlowFailed,
    /// <summary>The <c>state</c> returned to the redirect URI didn't match the request.</summary>
    StateMismatch,
    /// <summary>A Descope endpoint returned an error response.</summary>
    ServerError,
    /// <summary>The request to Descope failed before a response was received.</summary>
    NetworkError,
    /// <summary>A JWT couldn't be decoded or is missing required claims.</summary>
    TokenError,
    /// <summary>There's no session to refresh, or its refresh token has expired.</summary>
    NoSession,
}

/// <summary>
/// The exception thrown by Descope SDK operations.
/// </summary>
public class DescopeException : Exception
{
    /// <summary>The kind of failure.</summary>
    public DescopeErrorCode Code { get; }

    /// <summary>The OIDC <c>error</c> value returned by Descope, if any, e.g. <c>invalid_grant</c>.</summary>
    public string? OAuthError { get; }

    /// <summary>Creates a new <see cref="DescopeException"/>.</summary>
    public DescopeException(DescopeErrorCode code, string message, string? oauthError = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        OAuthError = oauthError;
    }
}
