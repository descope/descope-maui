using Descope.Maui.Internal;

namespace Descope.Maui.Test;

public class SystemInfoTests
{
    [Theory]
    [InlineData(true, "18.7", "18.7", "18.7.0")]
    [InlineData(true, "18.7.2", "18.7", "18.7.2")]
    [InlineData(true, null, "18.7", null)]
    [InlineData(false, "6.1.0", "15", "15")]
    [InlineData(false, null, null, null)]
    public void FormatsPlatformVersionLikeTheNativeSdks(bool isIos, string? osVersion, string? versionString, string? expected) =>
        Assert.Equal(expected, SystemInfo.FormatPlatformVersion(isIos, osVersion == null ? null : Version.Parse(osVersion), versionString));

    [Fact]
    public void EncodesTheProjectId()
    {
        var headers = new SystemInfo(null, null, null, null, null).Headers("P1\r\nx-evil: 1");
        Assert.Equal("P1%0D%0Ax-evil: 1", headers.Single(h => h.Key == "x-descope-project-id").Value);
    }

    [Theory]
    [InlineData(true, "2.0.1", "42", "2.0.1.42")]
    [InlineData(true, "2.0.1", "", "2.0.1")]
    [InlineData(true, "", "42", null)]
    [InlineData(false, "2.0.1", "42", "2.0.1")]
    [InlineData(false, null, "42", null)]
    public void FormatsAppVersionLikeTheNativeSdks(bool isIos, string? version, string? build, string? expected) =>
        Assert.Equal(expected, SystemInfo.FormatAppVersion(isIos, version, build));

    [Theory]
    [InlineData(true, false, "Apple", "iPhone17,2", "iPhone17,2")]
    [InlineData(true, true, "Apple", "arm64", "Simulator")]
    [InlineData(false, false, "Google", "Pixel 9", "Google, Pixel 9")]
    [InlineData(false, false, "samsung", "SM-S921B,x", "samsung, SM-S921B_x")]
    [InlineData(false, false, "unknown", "Pixel 9", "Pixel 9")]
    [InlineData(false, false, "Pixel", "Pixel", "Pixel")]
    [InlineData(false, false, "", null, null)]
    public void FormatsDeviceLikeTheNativeSdks(bool isIos, bool isVirtual, string? manufacturer, string? model, string? expected) =>
        Assert.Equal(expected, SystemInfo.FormatDevice(isIos, isVirtual, manufacturer, model));

    [Theory]
    [InlineData("My App", "My App")]
    [InlineData("  My App ", "My App")]
    [InlineData("Café", "Caf%C3%A9")]
    [InlineData("100%", "100%25")]
    [InlineData("a\nb", "a%0Ab")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void KeepsHeaderValuesAscii(string? value, string? expected) =>
        Assert.Equal(expected, SystemInfo.HeaderValue(value));

    [Fact]
    public void CurrentDoesNotThrowWithoutAPlatform()
    {
        // the net10.0 target has no Essentials implementation, so only the SDK headers are sent
        var headers = SystemInfo.Current.Headers("P1").Select(h => h.Key).Order();
        Assert.Equal(["x-descope-project-id", "x-descope-sdk-name", "x-descope-sdk-version"], headers);
    }
}
