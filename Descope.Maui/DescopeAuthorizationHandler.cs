using System.Net.Http.Headers;

namespace Descope.Maui;

/// <summary>
/// An <see cref="HttpClient"/> handler that refreshes the session if needed and sends its JWT as a
/// bearer token on requests to the given hosts, such as your backend's.
/// </summary>
/// <remarks>
/// <code>
/// var api = new HttpClient(new DescopeAuthorizationHandler(descope.SessionManager, "api.example.com") { InnerHandler = new HttpClientHandler() });
/// </code>
/// Requests to other hosts, or sent when no user is signed in, have no <c>Authorization</c> header.
/// </remarks>
public class DescopeAuthorizationHandler(DescopeSessionManager sessionManager, params string[] hosts) : DelegatingHandler
{
    private readonly string[] _hosts = hosts.Length > 0 ? hosts : throw new ArgumentException("Pass the hosts that should receive the session JWT", nameof(hosts));

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_hosts.Contains(request.RequestUri?.Host, StringComparer.OrdinalIgnoreCase))
        {
            await sessionManager.RefreshSessionIfNeededAsync(cancellationToken).ConfigureAwait(false);
            if (sessionManager.Session is { } session)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.SessionJwt);
            }
        }
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
