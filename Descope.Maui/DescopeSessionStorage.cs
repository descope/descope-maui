using System.Text.Json;
using Descope.Maui.Internal;
using Microsoft.Maui.Storage;

namespace Descope.Maui;

/// <summary>
/// Persists the session between app launches. The default implementation,
/// <see cref="SecureSessionStorage"/>, uses MAUI's <see cref="SecureStorage"/>.
/// </summary>
public interface IDescopeSessionStorage
{
    /// <summary>Loads the saved session, or returns <c>null</c> if there's none.</summary>
    Task<DescopeSession?> LoadSessionAsync();

    /// <summary>Saves the session, replacing any saved one.</summary>
    Task SaveSessionAsync(DescopeSession session);

    /// <summary>Removes the saved session.</summary>
    Task RemoveSessionAsync();
}

/// <summary>
/// Stores the session in the iOS Keychain or the Android Keystore through MAUI's <see cref="SecureStorage"/>.
/// </summary>
/// <remarks>
/// On Android, exclude <c>SecureStorage</c> from auto backup, or a restored backup can't be
/// decrypted. See the MAUI <c>SecureStorage</c> documentation.
/// </remarks>
public class SecureSessionStorage : IDescopeSessionStorage
{
    private readonly ISecureStorage _secureStorage;
    private readonly string _key;

    /// <summary>Creates a storage that saves the session for a project under its own key.</summary>
    public SecureSessionStorage(string projectId, ISecureStorage? secureStorage = null)
    {
        _key = $"descope.session.{projectId}";
        _secureStorage = secureStorage ?? SecureStorage.Default;
    }

    /// <inheritdoc/>
    public async Task<DescopeSession?> LoadSessionAsync()
    {
        var json = await _secureStorage.GetAsync(_key).ConfigureAwait(false);
        if (json == null) return null;
        try
        {
            var stored = JsonSerializer.Deserialize(json, JsonContext.Default.StoredSession);
            if (stored?.SessionJwt == null || stored.RefreshJwt == null) return null;
            return new DescopeSession(stored.SessionJwt, stored.RefreshJwt, stored.IdToken);
        }
        catch (Exception)
        {
            // a corrupt or outdated entry is treated as no session
            _secureStorage.Remove(_key);
            return null;
        }
    }

    /// <inheritdoc/>
    public Task SaveSessionAsync(DescopeSession session)
    {
        var json = JsonSerializer.Serialize(new StoredSession(session.SessionJwt, session.RefreshJwt, session.IdToken), JsonContext.Default.StoredSession);
        return _secureStorage.SetAsync(_key, json);
    }

    /// <inheritdoc/>
    public Task RemoveSessionAsync()
    {
        _secureStorage.Remove(_key);
        return Task.CompletedTask;
    }
}
