namespace Descope.Maui.Test;

public class DescopeUserTests
{
    private static readonly DateTimeOffset Expiry = DateTimeOffset.FromUnixTimeSeconds(2_000_000_000);

    [Fact]
    public void ReadsProfileClaims()
    {
        var idToken = Jwt.Make(Expiry, new { name = "Andy", email = "andy@example.com", email_verified = true, phone_number = "+15555550100" });

        var user = DescopeUser.FromIdToken(idToken, "fallback");

        Assert.Equal("U123", user.UserId);
        Assert.Equal("Andy", user.Name);
        Assert.Equal("andy@example.com", user.Email);
        Assert.True(user.IsVerifiedEmail);
        Assert.Equal("+15555550100", user.Phone);
        Assert.False(user.IsVerifiedPhone);
    }

    [Fact]
    public void TreatsEmptyClaimsAsMissing()
    {
        var idToken = Jwt.Make(Expiry, new { name = "", given_name = " ", email = "andy@example.com" });

        var user = DescopeUser.FromIdToken(idToken, "fallback");

        Assert.Null(user.Name);
        Assert.Null(user.GivenName);
        Assert.Equal("andy@example.com", user.Name ?? user.Email);
    }

    [Fact]
    public void FallsBackToSessionSubjectWithoutIdToken()
    {
        var user = DescopeUser.FromIdToken(null, "U456");

        Assert.Equal("U456", user.UserId);
        Assert.Null(user.Email);
    }
}
