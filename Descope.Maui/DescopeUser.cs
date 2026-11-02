using System.Text.Json;

namespace Descope.Maui;

/// <summary>
/// The signed in user, read from the standard OIDC claims in the ID token.
/// </summary>
public sealed record DescopeUser
{
    /// <summary>The Descope user ID (the <c>sub</c> claim).</summary>
    public required string UserId { get; init; }

    /// <summary>The user's full name.</summary>
    public string? Name { get; init; }

    /// <summary>The user's given name.</summary>
    public string? GivenName { get; init; }

    /// <summary>The user's middle name.</summary>
    public string? MiddleName { get; init; }

    /// <summary>The user's family name.</summary>
    public string? FamilyName { get; init; }

    /// <summary>A URL of the user's profile picture.</summary>
    public string? Picture { get; init; }

    /// <summary>The user's email address.</summary>
    public string? Email { get; init; }

    /// <summary>Whether the email address has been verified.</summary>
    public bool IsVerifiedEmail { get; init; }

    /// <summary>The user's phone number.</summary>
    public string? Phone { get; init; }

    /// <summary>Whether the phone number has been verified.</summary>
    public bool IsVerifiedPhone { get; init; }

    internal static DescopeUser FromIdToken(string? idToken, string fallbackUserId)
    {
        Dictionary<string, JsonElement> claims;
        try
        {
            claims = idToken != null ? DescopeToken.DecodePayload(idToken) : [];
        }
        catch (Exception)
        {
            claims = [];
        }

        // Descope sends empty strings for unset profile fields, which callers should see as missing
        string? Str(string key) => claims.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;
        bool Bool(string key) => claims.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.True;

        return new DescopeUser
        {
            UserId = Str("sub") ?? fallbackUserId,
            Name = Str("name"),
            GivenName = Str("given_name"),
            MiddleName = Str("middle_name"),
            FamilyName = Str("family_name"),
            Picture = Str("picture"),
            Email = Str("email"),
            IsVerifiedEmail = Bool("email_verified"),
            Phone = Str("phone_number"),
            IsVerifiedPhone = Bool("phone_number_verified"),
        };
    }
}
