using System.Text;
using System.Text.Json;

namespace Descope.Maui;

/// <summary>
/// A decoded Descope JWT. The session and refresh JWTs of a <see cref="DescopeSession"/> are
/// exposed as <see cref="DescopeToken"/> values.
/// </summary>
/// <remarks>
/// The token is decoded but its signature isn't verified. The app's backend must validate the
/// session JWT before trusting it.
/// </remarks>
public sealed class DescopeToken
{
    private static readonly HashSet<string> StandardClaims = ["sub", "iss", "iat", "exp", "tenants", "roles", "permissions"];

    private readonly Dictionary<string, JsonElement> _allClaims;

    /// <summary>The underlying JWT value.</summary>
    public string Jwt { get; }

    /// <summary>The <c>sub</c> claim: the ID of the user the JWT was issued for.</summary>
    public string EntityId { get; }

    /// <summary>The ID of the Descope project, taken from the last path segment of the <c>iss</c> claim.</summary>
    public string ProjectId { get; }

    /// <summary>The <c>iat</c> claim.</summary>
    public DateTimeOffset IssuedAt { get; }

    /// <summary>The <c>exp</c> claim.</summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>Whether <see cref="ExpiresAt"/> has passed according to the device clock.</summary>
    public bool IsExpired => ExpiresAt <= DateTimeOffset.UtcNow;

    /// <summary>
    /// All claims that aren't exposed by another property or method, such as custom claims
    /// added by a JWT template.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> Claims { get; }

    /// <summary>Decodes a JWT.</summary>
    /// <exception cref="DescopeException">The JWT is malformed or is missing a required claim.</exception>
    public DescopeToken(string jwt)
    {
        Jwt = jwt;
        try
        {
            _allClaims = DecodePayload(jwt);
            EntityId = GetString("sub");
            ProjectId = GetString("iss").Split('/').Last();
            IssuedAt = DateTimeOffset.FromUnixTimeSeconds(GetLong("iat"));
            ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(GetLong("exp"));
            Claims = _allClaims.Where(c => !StandardClaims.Contains(c.Key)).ToDictionary(c => c.Key, c => c.Value);
        }
        catch (Exception ex) when (ex is not DescopeException)
        {
            throw new DescopeException(DescopeErrorCode.TokenError, "Invalid JWT", innerException: ex);
        }
    }

    internal static DescopeToken? TryParse(string? jwt)
    {
        if (string.IsNullOrEmpty(jwt)) return null;
        try
        {
            return new DescopeToken(jwt);
        }
        catch (DescopeException)
        {
            return null;
        }
    }

    // Authorization

    /// <summary>The IDs of the tenants in the <c>tenants</c> claim.</summary>
    public IReadOnlyList<string> TenantIds =>
        _allClaims.TryGetValue("tenants", out var tenants) && tenants.ValueKind == JsonValueKind.Object
            ? tenants.EnumerateObject().Select(t => t.Name).ToList()
            : [];

    /// <summary>
    /// The roles granted in the JWT. Pass a tenant ID for that tenant's roles, or <c>null</c> for
    /// project-level roles.
    /// </summary>
    public IReadOnlyList<string> Roles(string? tenant = null) => AuthorizationItems(tenant, "roles");

    /// <summary>
    /// The permissions granted in the JWT. Pass a tenant ID for that tenant's permissions, or
    /// <c>null</c> for project-level permissions.
    /// </summary>
    public IReadOnlyList<string> Permissions(string? tenant = null) => AuthorizationItems(tenant, "permissions");

    private List<string> AuthorizationItems(string? tenant, string key)
    {
        if (tenant == null)
        {
            return _allClaims.TryGetValue(key, out var items) ? StringItems(items) : [];
        }
        if (!_allClaims.TryGetValue("tenants", out var tenants) || tenants.ValueKind != JsonValueKind.Object) return [];
        if (!tenants.TryGetProperty(tenant, out var tenantClaims) || tenantClaims.ValueKind != JsonValueKind.Object) return [];
        return tenantClaims.TryGetProperty(key, out var tenantItems) ? StringItems(tenantItems) : [];
    }

    private static List<string> StringItems(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];

    // Utilities

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is DescopeToken token && token.Jwt == Jwt;

    /// <inheritdoc/>
    public override int GetHashCode() => Jwt.GetHashCode();

    /// <summary>
    /// A safe representation of the token with only the entity ID and expiry, and no secret data.
    /// </summary>
    public override string ToString()
    {
        var expires = IsExpired ? "expired" : "expires";
        return $"DescopeToken(entityId={EntityId}, {expires}={ExpiresAt:yyyy-MM-dd HH:mm:ss}Z)";
    }

    // Decoding

    private string GetString(string claim) =>
        _allClaims.TryGetValue(claim, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new DescopeException(DescopeErrorCode.TokenError, $"Missing or invalid {claim} claim in token");

    private long GetLong(string claim) =>
        _allClaims.TryGetValue(claim, out var value) && value.ValueKind == JsonValueKind.Number
            ? (long)value.GetDouble()
            : throw new DescopeException(DescopeErrorCode.TokenError, $"Missing or invalid {claim} claim in token");

    internal static Dictionary<string, JsonElement> DecodePayload(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3)
        {
            throw new DescopeException(DescopeErrorCode.TokenError, "Invalid token format");
        }
        var json = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new DescopeException(DescopeErrorCode.TokenError, "Invalid token data");
        }
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        return Convert.FromBase64String(base64);
    }
}
