using System.Reflection;
using System.Text;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices;

namespace Descope.Maui.Internal;

// Describes the SDK, app and device in the x-descope-* headers the other Descope mobile SDKs send, so
// Descope can tell MAUI traffic apart. The values match the Swift SDK on iOS and the Kotlin SDK on Android.
internal sealed record SystemInfo(string? PlatformName, string? PlatformVersion, string? AppName, string? AppVersion, string? Device)
{
    public const string SdkName = "maui";

    public static string SdkVersion { get; } = ReadSdkVersion();

    // Read once and kept, the values don't change while the app runs
    private static readonly Lazy<SystemInfo> _current = new(Read);

    public static SystemInfo Current => _current.Value;

    public KeyValuePair<string, string>[] Headers(string projectId)
    {
        var headers = new List<KeyValuePair<string, string>>
        {
            new("x-descope-sdk-name", SdkName),
            new("x-descope-sdk-version", SdkVersion),
        };
        Add("x-descope-project-id", projectId);
        Add("x-descope-platform-name", PlatformName);
        Add("x-descope-platform-version", PlatformVersion);
        Add("x-descope-app-name", AppName);
        Add("x-descope-app-version", AppVersion);
        Add("x-descope-device", Device);
        return [.. headers];

        void Add(string name, string? value)
        {
            if (HeaderValue(value) is { } v) headers.Add(new(name, v));
        }
    }

    // Header values must be printable ASCII, so other characters, like those in a localized app name,
    // are percent encoded rather than failing the request
    internal static string? HeaderValue(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        var builder = new StringBuilder(value.Length);
        Span<byte> bytes = stackalloc byte[4];
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value is >= 0x20 and < 0x7F && rune.Value != '%')
            {
                builder.Append((char)rune.Value);
                continue;
            }
            var count = rune.EncodeToUtf8(bytes);
            foreach (var b in bytes[..count]) builder.Append('%').Append(b.ToString("X2"));
        }
        return builder.ToString();
    }

    // The Swift SDK sends "major.minor.patch", e.g. "18.7.0", the Kotlin SDK sends Android's release
    // string, e.g. "15", which is what DeviceInfo returns there
    internal static string? FormatPlatformVersion(bool isIos, Version? osVersion, string? versionString)
    {
        if (!isIos) return versionString;
        return osVersion == null ? null : $"{osVersion.Major}.{osVersion.Minor}.{Math.Max(osVersion.Build, 0)}";
    }

    // The Swift SDK sends "version.build", the Kotlin SDK sends the version name alone
    internal static string? FormatAppVersion(bool isIos, string? version, string? build)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        if (!isIos || string.IsNullOrWhiteSpace(build)) return version;
        return $"{version}.{build}";
    }

    // The Swift SDK sends the model identifier, e.g. "iPhone17,2", or "Simulator". The Kotlin SDK sends
    // "manufacturer, model", e.g. "Google, Pixel 9", leaving out unknown or repeated values.
    internal static string? FormatDevice(bool isIos, bool isVirtual, string? manufacturer, string? model)
    {
        if (isIos) return isVirtual ? "Simulator" : model;
        var values = new[] { manufacturer, model }
            .Where(v => !string.IsNullOrWhiteSpace(v) && v != "unknown")
            .Select(v => v!.Replace(',', '_'))
            .Distinct()
            .ToArray();
        return values.Length > 0 ? string.Join(", ", values) : null;
    }

    private static SystemInfo Read()
    {
        // Essentials throws on platforms it doesn't implement, like the net10.0 target used by the tests,
        // and any one value failing shouldn't lose the others. None of the values read here need the
        // main thread, the client may be created on any thread.
        var platform = Try<DevicePlatform?>(() => DeviceInfo.Current.Platform);
        var isIos = platform == DevicePlatform.iOS;
        var isVirtual = Try(() => DeviceInfo.Current.DeviceType) == DeviceType.Virtual;
        return new SystemInfo(
            PlatformName: platform is { } p && p != DevicePlatform.Unknown ? p.ToString().ToLowerInvariant() : null,
            // on iOS .NET reads the OS version from NSProcessInfo, like the Swift SDK, rather than UIDevice
            PlatformVersion: FormatPlatformVersion(isIos, isIos ? Try(() => Environment.OSVersion.Version) : null, isIos ? null : Try(() => DeviceInfo.Current.VersionString)),
            AppName: ReadAppName(),
            AppVersion: FormatAppVersion(isIos, Try(() => AppInfo.Current.VersionString), Try(() => AppInfo.Current.BuildString)),
            Device: platform == null ? null : FormatDevice(isIos, isVirtual, Try(() => DeviceInfo.Current.Manufacturer), Try(() => DeviceInfo.Current.Model)));
    }

    // The Swift SDK sends CFBundleName, while AppInfo on iOS prefers CFBundleDisplayName
    private static string? ReadAppName()
    {
#if IOS
        return Try(() => Foundation.NSBundle.MainBundle.ObjectForInfoDictionary("CFBundleName")?.ToString());
#else
        return Try(() => AppInfo.Current.Name);
#endif
    }

    private static T? Try<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return default;
        }
    }

    private static string ReadSdkVersion()
    {
        var assembly = typeof(SystemInfo).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        // drop the "+commit" suffix the build appends
        if (version?.IndexOf('+') is int plus and >= 0) version = version[..plus];
        return string.IsNullOrEmpty(version) ? assembly.GetName().Version?.ToString(3) ?? "unknown" : version;
    }
}
