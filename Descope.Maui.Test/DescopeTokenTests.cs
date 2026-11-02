namespace Descope.Maui.Test;

public class DescopeTokenTests
{
    private static readonly DateTimeOffset Expiry = DateTimeOffset.FromUnixTimeSeconds(2_000_000_000);

    [Fact]
    public void DecodesStandardClaims()
    {
        var token = new DescopeToken(Jwt.Make(Expiry));

        Assert.Equal("U123", token.EntityId);
        Assert.Equal(Jwt.ProjectId, token.ProjectId);
        Assert.Equal(Expiry, token.ExpiresAt);
        Assert.Equal(Expiry.AddMinutes(-10), token.IssuedAt);
        Assert.False(token.IsExpired);
        Assert.Empty(token.Claims);
    }

    [Fact]
    public void ReadsProjectAndTenantAuthorization()
    {
        var token = new DescopeToken(Jwt.Make(Expiry, new
        {
            roles = new[] { "admin" },
            permissions = new[] { "read", "write" },
            tenants = new Dictionary<string, object>
            {
                ["T1"] = new { roles = new[] { "editor" }, permissions = new[] { "publish" } },
                ["T2"] = new { },
            },
        }));

        Assert.Equal(["admin"], token.Roles());
        Assert.Equal(["read", "write"], token.Permissions());
        Assert.Equal(["T1", "T2"], token.TenantIds);
        Assert.Equal(["editor"], token.Roles("T1"));
        Assert.Equal(["publish"], token.Permissions("T1"));
        Assert.Empty(token.Roles("T2"));
        Assert.Empty(token.Roles("missing"));
    }

    [Fact]
    public void ExposesCustomClaimsOnly()
    {
        var token = new DescopeToken(Jwt.Make(Expiry, new { dob = "1979-01-01", roles = new[] { "admin" } }));

        Assert.Equal(["dob"], token.Claims.Keys);
        Assert.Equal("1979-01-01", token.Claims["dob"].GetString());
    }

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("a.b.c")]
    [InlineData("eyJhbGciOiJub25lIn0.eyJzdWIiOiJVMSJ9.sig")] // missing iss and exp
    public void RejectsInvalidTokens(string jwt)
    {
        var ex = Assert.Throws<DescopeException>(() => new DescopeToken(jwt));
        Assert.Equal(DescopeErrorCode.TokenError, ex.Code);
    }

    [Fact]
    public void ToStringHasNoSecrets()
    {
        var jwt = Jwt.Make(Expiry);

        Assert.DoesNotContain(jwt, new DescopeToken(jwt).ToString());
    }
}
