namespace Descope.Maui.Test;

public class DescopeOptionsTests
{
    [Theory]
    [InlineData("P2abcdefghijklmnopqrstuvwxyz", "https://api.descope.com")]
    [InlineData("Peuc12abcdefghijklmnopqrstuvwxyz", "https://api.euc1.descope.com")]
    public void DerivesBaseUrlFromProjectId(string projectId, string expected)
    {
        Assert.Equal(expected, new DescopeOptions { ProjectId = projectId }.ResolvedBaseUrl);
    }

    [Fact]
    public void BaseUrlOverrideWins()
    {
        var options = new DescopeOptions { ProjectId = "Peuc12abcdefghijklmnopqrstuvwxyz", BaseUrl = "https://auth.example.com/" };

        Assert.Equal("https://auth.example.com", options.ResolvedBaseUrl);
    }

    [Theory]
    [InlineData("P2abcdefghijklmnopqrstuvwxyz:SA2app", "SA2app")]
    [InlineData("P2abcdefghijklmnopqrstuvwxyz:SA2app##", "SA2app")]
    [InlineData("P2otherprojectabcdefghijklm:SA2app", null)]
    [InlineData("not-a-client-id", null)]
    public void ReadsAppIdFromGeneratedClientId(string payload, string? expected)
    {
        var clientId = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload));

        Assert.Equal(expected, DescopeOptions.AppIdFromClientId(clientId, "P2abcdefghijklmnopqrstuvwxyz"));
    }

    [Theory]
    [InlineData("my-custom-client")]
    [InlineData("P2abcdefghijklmnopqrstuvwxyz")]
    public void CustomClientIdHasNoAppId(string clientId)
    {
        Assert.Null(DescopeOptions.AppIdFromClientId(clientId, "P2abcdefghijklmnopqrstuvwxyz"));
    }

    [Theory]
    [InlineData("http://auth.example.com")]
    [InlineData("auth.example.com")]
    public void RejectsBaseUrlThatIsNotHttps(string baseUrl)
    {
        var options = new DescopeOptions { ProjectId = "P2abcdefghijklmnopqrstuvwxyz", RedirectUri = "myapp://auth", BaseUrl = baseUrl };

        var ex = Assert.Throws<DescopeException>(options.Validate);

        Assert.Equal(DescopeErrorCode.InvalidConfiguration, ex.Code);
    }
}
