namespace Descope.Maui;

/// <summary>
/// Receives the redirect back from the browser on Android. Subclass it in your app with your
/// redirect URI's scheme:
/// <code>
/// [Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
/// [IntentFilter([Intent.ActionView],
///     Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
///     DataScheme = "myapp")]
/// public class DescopeCallback : DescopeCallbackActivity { }
/// </code>
/// </summary>
public abstract class DescopeCallbackActivity : Microsoft.Maui.Authentication.WebAuthenticatorCallbackActivity
{
}
