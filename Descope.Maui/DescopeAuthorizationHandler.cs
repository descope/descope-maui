using System.Net.Http.Headers;

namespace Descope.Maui;

/// <summary>
/// An <see cref="HttpClient"/> handler that refreshes the session if needed and sends its JWT as a
/// bearer token on every request.
/// </summary>
/// <remarks>
/// <code>
/// var api = new HttpClient(new DescopeAuthorizationHandler(descope.SessionManager) { InnerHandler = new HttpClientHandler() });
/// </code>
/// Requests are sent without an <c>Authorization</c> header when no user is signed in.
/// </remarks>
public class DescopeAuthorizationHandler(DescopeSessionManager sessionManager) : DelegatingHandler
{
    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await sessionManager.RefreshSessionIfNeededAsync(cancellationToken).ConfigureAwait(false);
        if (sessionManager.Session is { } session)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.SessionJwt);
        }
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
