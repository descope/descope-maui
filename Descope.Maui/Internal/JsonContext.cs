using System.Text.Json.Serialization;

namespace Descope.Maui.Internal;

internal sealed record StoredSession(string? SessionJwt, string? RefreshJwt, string? IdToken);

// Source-generated so serialization keeps working in trimmed and AOT-compiled iOS apps
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(StoredSession))]
internal sealed partial class JsonContext : JsonSerializerContext
{
}
