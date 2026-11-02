using System.Text.Json;
using System.Text.Json.Serialization;

namespace Descope.Maui.Internal;

internal sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("id_token")] string? IdToken);

// The Descope OIDC endpoints, see https://docs.descope.com/getting-started/oidc-endpoints
internal sealed class OidcClient(DescopeOptions options, HttpClient httpClient)
{
    // Parameters the SDK sets itself, which login options can't override
    private static readonly HashSet<string> ReservedParameters =
        ["response_type", "client_id", "redirect_uri", "scope", "state", "code_challenge", "code_challenge_method"];

    private string BaseUrl => options.ResolvedBaseUrl;

    // An app's client ID may be a custom one that doesn't encode the project, so its requests go to the
    // routes that take the project ID in the path. Descope resolves both kinds of client ID to the app there.
    private string OidcUrl => options.HasAppClientId
        ? $"{BaseUrl}/oauth2/v1/{Uri.EscapeDataString(options.ProjectId)}"
        : $"{BaseUrl}/oauth2/v1";

    public Uri AuthorizeUrl(string state, string codeChallenge, DescopeLoginOptions? login)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"),
            new("client_id", options.ResolvedClientId),
            new("redirect_uri", options.RedirectUri),
            new("scope", options.Scope),
            new("state", state),
            new("code_challenge", codeChallenge),
            new("code_challenge_method", "S256"),
        };
        var flowId = login?.FlowId ?? options.FlowId;
        if (!string.IsNullOrEmpty(flowId)) parameters.Add(new("flow", flowId));
        if (!string.IsNullOrEmpty(login?.Tenant)) parameters.Add(new("tenant", login.Tenant));
        if (!string.IsNullOrEmpty(login?.LoginHint)) parameters.Add(new("login_hint", login.LoginHint));
        if (login != null)
        {
            parameters.AddRange(login.AdditionalParameters.Where(p => !ReservedParameters.Contains(p.Key)));
        }
        return new Uri($"{OidcUrl}/authorize?{Query(parameters)}");
    }

    public Uri LogoutUrl(string? idToken)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("client_id", options.ResolvedClientId),
            new("post_logout_redirect_uri", options.RedirectUri),
        };
        if (idToken != null) parameters.Add(new("id_token_hint", idToken));
        // logout only resolves custom client IDs, so name the app of a generated one explicitly, or the
        // redirect URI would be checked against the default application
        if (options.HasAppClientId && DescopeOptions.AppIdFromClientId(options.ResolvedClientId, options.ProjectId) is { } appId)
        {
            parameters.Add(new("ssoAppId", appId));
        }
        return new Uri($"{OidcUrl}/logout?{Query(parameters)}");
    }

    public Task<TokenResponse> ExchangeCodeAsync(string code, string codeVerifier, CancellationToken cancellationToken) =>
        TokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = options.RedirectUri,
            ["client_id"] = options.ResolvedClientId,
            ["code_verifier"] = codeVerifier,
        }, cancellationToken);

    public Task<TokenResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        TokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = options.ResolvedClientId,
        }, cancellationToken);

    public async Task RevokeAsync(string token, CancellationToken cancellationToken)
    {
        // there's no per-project revoke route, Descope finds the project and session from the token
        using var response = await PostAsync($"{BaseUrl}/oauth2/v1/revoke", new Dictionary<string, string>
        {
            ["token"] = token,
            ["client_id"] = options.ResolvedClientId,
        }, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TokenResponse> TokenRequestAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var response = await PostAsync($"{OidcUrl}/token", form, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        TokenResponse? tokens;
        try
        {
            tokens = JsonSerializer.Deserialize(body, JsonContext.Default.TokenResponse);
        }
        catch (JsonException ex)
        {
            throw new DescopeException(DescopeErrorCode.ServerError, "Invalid response from the token endpoint", innerException: ex);
        }
        if (string.IsNullOrEmpty(tokens?.AccessToken))
        {
            throw new DescopeException(DescopeErrorCode.ServerError, "The token endpoint didn't return an access token");
        }
        return tokens;
    }

    private async Task<HttpResponseMessage> PostAsync(string url, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new FormUrlEncodedContent(form);
            return await httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new DescopeException(DescopeErrorCode.NetworkError, "The request to Descope failed", innerException: ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient reports a timeout as a cancellation
            throw new DescopeException(DescopeErrorCode.NetworkError, "The request to Descope timed out", innerException: ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string? error = null;
        string? description = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) error = e.GetString();
            if (doc.RootElement.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String) description = d.GetString();
        }
        catch (JsonException)
        {
            // not every error response has a JSON body
        }
        var message = $"Descope returned {(int)response.StatusCode}{(error != null ? $" {error}" : "")}{(description != null ? $": {description}" : "")}";
        throw new DescopeException(DescopeErrorCode.ServerError, message, error);
    }

    private static string Query(IEnumerable<KeyValuePair<string, string>> parameters) =>
        string.Join("&", parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
}
