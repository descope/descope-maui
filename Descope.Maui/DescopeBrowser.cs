using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Authentication;

namespace Descope.Maui;

/// <summary>
/// Opens a URL in the system browser and waits for the redirect back to the app.
/// The default implementation, <see cref="WebAuthenticatorBrowser"/>, uses MAUI's <see cref="WebAuthenticator"/>.
/// </summary>
public interface IDescopeBrowser
{
    /// <summary>
    /// Opens <paramref name="url"/> and returns the query parameters of the redirect to <paramref name="callbackUrl"/>.
    /// Throws <see cref="TaskCanceledException"/> if the user closes the browser.
    /// </summary>
    Task<IDictionary<string, string>> AuthenticateAsync(Uri url, Uri callbackUrl, bool prefersEphemeralSession, CancellationToken cancellationToken);
}

/// <summary>
/// Runs the flow in <c>ASWebAuthenticationSession</c> on iOS and a Custom Tab on Android.
/// </summary>
public class WebAuthenticatorBrowser : IDescopeBrowser
{
    /// <inheritdoc/>
    public async Task<IDictionary<string, string>> AuthenticateAsync(Uri url, Uri callbackUrl, bool prefersEphemeralSession, CancellationToken cancellationToken)
    {
        var options = new WebAuthenticatorOptions
        {
            Url = url,
            CallbackUrl = callbackUrl,
            PrefersEphemeralWebBrowserSession = prefersEphemeralSession,
        };
        var result = await MainThread.InvokeOnMainThreadAsync(() => WebAuthenticator.Default.AuthenticateAsync(options, cancellationToken)).ConfigureAwait(false);
        return result.Properties;
    }
}
