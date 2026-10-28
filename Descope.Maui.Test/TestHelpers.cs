using Descope.Maui.Internal;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Descope.Maui.Test;

internal static class Jwt
{
    public const string ProjectId = "P2abcdefghijklmnopqrstuvwxyz";

    // An unsigned JWT; the SDK decodes tokens but never verifies them
    public static string Make(DateTimeOffset expiresAt, object? extraClaims = null, string sub = "U123")
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = sub,
            ["iss"] = $"https://api.descope.com/{ProjectId}",
            ["iat"] = expiresAt.AddMinutes(-10).ToUnixTimeSeconds(),
            ["exp"] = expiresAt.ToUnixTimeSeconds(),
        };
        if (extraClaims != null)
        {
            foreach (var p in JsonSerializer.SerializeToElement(extraClaims).EnumerateObject())
            {
                claims[p.Name] = p.Value;
            }
        }
        return $"{Encode("{\"alg\":\"none\"}")}.{Encode(JsonSerializer.Serialize(claims))}.sig";
    }

    public static string Session(DateTimeOffset now, TimeSpan expiresIn, object? extraClaims = null) => Make(now + expiresIn, extraClaims);

    public static string Refresh(DateTimeOffset now) => Make(now + TimeSpan.FromDays(30));

    private static string Encode(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal sealed class MemoryStorage : IDescopeSessionStorage
{
    public DescopeSession? Saved { get; set; }

    // thrown by LoadSessionAsync, like a keychain error
    public Exception? LoadError { get; set; }

    // when set, LoadSessionAsync reads the session and then waits for it, to finish a load after something else happened
    public TaskCompletionSource? LoadGate { get; set; }

    public async Task<DescopeSession?> LoadSessionAsync()
    {
        var saved = Saved;
        if (LoadGate != null) await LoadGate.Task;
        if (LoadError != null) throw LoadError;
        return saved;
    }

    public Task SaveSessionAsync(DescopeSession session)
    {
        Saved = session;
        return Task.CompletedTask;
    }

    public Task RemoveSessionAsync()
    {
        Saved = null;
        return Task.CompletedTask;
    }
}

internal sealed class FakeBrowser : IDescopeBrowser
{
    public List<Uri> Opened { get; } = [];

    // Builds the redirect parameters from the authorize URL, so a test can echo back its state
    public Func<Uri, IDictionary<string, string>> Respond { get; set; } = url => new Dictionary<string, string>
    {
        ["code"] = "the-code",
        ["state"] = QueryParam(url, "state")!,
    };

    public Task<IDictionary<string, string>> AuthenticateAsync(Uri url, Uri callbackUrl, bool prefersEphemeralSession, CancellationToken cancellationToken)
    {
        Opened.Add(url);
        return Task.FromResult(Respond(url));
    }

    public static string? QueryParam(Uri url, string name) =>
        url.Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2))
            .Where(p => Uri.UnescapeDataString(p[0]) == name)
            .Select(p => Uri.UnescapeDataString(p[1]))
            .FirstOrDefault();
}

internal sealed class FakeHttpHandler : HttpMessageHandler
{
    public List<(string Path, Dictionary<string, string> Form)> Requests { get; } = [];

    public Func<string, Dictionary<string, string>, Task<HttpResponseMessage>> Respond { get; set; } =
        (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : "";
        var form = body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
        lock (Requests)
        {
            Requests.Add((request.RequestUri!.AbsolutePath, form));
        }
        return await Respond(request.RequestUri!.AbsolutePath, form);
    }

    public static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}

internal sealed class FakeAppLifecycle : IAppLifecycle
{
    public bool IsForeground { get; private set; } = true;

    public event EventHandler<bool>? ForegroundChanged;

    public void Set(bool isForeground)
    {
        IsForeground = isForeground;
        ForegroundChanged?.Invoke(this, isForeground);
    }
}
