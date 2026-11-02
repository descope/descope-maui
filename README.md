# Descope SDK for .NET MAUI

`Descope.Maui` adds Descope authentication to .NET MAUI apps on **iOS and Android**. Users sign in with a
[Descope Flow](https://docs.descope.com/flows) in the system browser (OIDC authorization code + PKCE), and the
SDK stores, refreshes and clears their session.

> **Status: Phase 1 preview.** Hosted flows through the system browser on iOS and Android.
> Native (embedded) flows, Mac Catalyst and Windows are not supported yet. Magic links have limitations, see
> [Magic links](#magic-links).

## Requirements

- .NET 10 with the `net10.0-ios` and/or `net10.0-android` target frameworks
- iOS 15+ / Android 5.0 (API 21)+
- A Descope project that accepts your redirect URI (e.g. `myapp://auth`), see [Allow the redirect URI](#4-allow-the-redirect-uri)

## Installation

```bash
dotnet add package Descope.Maui
```

## Setup

### 1. Register the client

In `MauiProgram.cs`:

```csharp
using Descope.Maui;

builder.Services.AddDescope(options =>
{
    options.ProjectId = "<Your-Project-Id>";
    options.RedirectUri = "myapp://auth";
    options.ClientId = "<Your-App-Client-Id>"; // optional but highly recommended, defaults to the default OIDC app
    options.FlowId = "sign-up-or-in"; // optional, defaults to the application's Flow Hosting URL
});
```

This registers a singleton `DescopeClient` and `DescopeSessionManager`. You can also create the client directly
with `new DescopeClient(new DescopeOptions { ... })`.

#### Use a specific OIDC application

By default the SDK uses the project's default OIDC application. To sign in through another OIDC application in
the project, set its client ID from the application's settings in the Descope Console:

```csharp
options.ClientId = "<Your-Application-Client-Id>";
```

That application's Flow Hosting URL, redirect URLs and token settings then apply. The app has to be a public
client, because a mobile app can't keep a client secret. Keep `ProjectId` set too, since requests are sent to
the project's endpoints.

### 2. Android: handle the redirect

Add a subclass of `DescopeCallbackActivity` with your redirect URI's scheme, e.g. in
`Platforms/Android/DescopeCallback.cs`:

```csharp
using Android.App;
using Android.Content;
using Android.Content.PM;
using Descope.Maui;

[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter([Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "myapp")]
public class DescopeCallback : DescopeCallbackActivity { }
```

On Android 11+, the app also needs to be able to see the browser. Add this to `Platforms/Android/AndroidManifest.xml`:

```xml
<queries>
  <intent>
    <action android:name="android.support.customtabs.action.CustomTabsService" />
  </intent>
</queries>
```

### 3. iOS

The flow runs in `ASWebAuthenticationSession`, which receives the redirect itself, so for most flows you don't need
to register a URL scheme. Flows with magic links need one, see [Magic links](#magic-links).

The session is stored with `SecureStorage`, so on the iOS simulator the app needs a keychain access group, or
saving the session fails after sign in. Add `Platforms/iOS/Entitlements.plist`:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>keychain-access-groups</key>
  <array>
    <string>$(AppIdentifierPrefix)com.example.myapp</string>
  </array>
</dict>
</plist>
```

### 4. Allow the redirect URI

Descope only redirects back to the app when it accepts the redirect URI:

- If the OIDC application has approved redirect URLs, add the full redirect URI, e.g. `myapp://auth`.
- Otherwise the project's approved domains are checked, if it has any. A custom scheme doesn't match a domain
  entry such as `example.com`, so add the full redirect URI there.

Wildcards don't work for the redirect URI, so add the exact URI the app uses. If it uses more than one, for
example a different one per platform, add each of them.

`LogoutAsync(endBrowserSession: true)` also sends the redirect URI to Descope, as the post-logout redirect. That's
always checked against the project's approved domains, so add it there too if the project has any.

If the project has **Apply Trusted Domains on Flow Execution** enabled, also add the domain the flow is hosted on
to the approved domains: `api.descope.com` for flows hosted by Descope, or your custom domain. Otherwise Descope
rejects the flow with "Forbidden origin" and the sign-in page stays blank. For example:

```text
example.com, api.descope.com, myapp://auth
```

## Usage

### Sign in

```csharp
try
{
    var session = await descope.LoginAsync(new DescopeLoginOptions
    {
        FlowId = "sign-up-or-in",   // overrides DescopeOptions.FlowId
        Tenant = "T2abc...",        // optional: route straight to a tenant's SSO
        LoginHint = "andy@example.com",
    });
    Console.WriteLine($"Signed in as {session.User.Email}");
}
catch (DescopeException ex) when (ex.Code == DescopeErrorCode.Cancelled)
{
    // the user closed the browser
}
```

Extra query parameters (for example `dynamic_val`, read by a flow scriptlet) go in `DescopeLoginOptions.AdditionalParameters`.

The flow runs in the system browser, so authentication methods that finish in that browser, such as OTP codes and
passkeys, work. Passkeys are registered for the flow page's domain (`api.descope.com` or your custom domain), not
your app's.

#### Magic links

The emailed link opens in the device's browser, outside the sign-in session. When the flow finishes there, the
browser redirects to the redirect URI, and the operating system has to open your app with it:

- On iOS, register the redirect URI's scheme in `Platforms/iOS/Info.plist`:

  ```xml
  <key>CFBundleURLTypes</key>
  <array>
    <dict>
      <key>CFBundleURLName</key>
      <string>com.example.myapp</string>
      <key>CFBundleURLSchemes</key>
      <array>
        <string>myapp</string>
      </array>
    </dict>
  </array>
  ```

  Safari asks the user to open the app, and `LoginAsync` then completes. The sign-in sheet isn't closed
  automatically when this happens, so the user closes it to see the signed in app.
- On Android, `DescopeCallbackActivity` is already registered for the scheme. This hasn't been tested yet.

Either way, the app has to still be running with `LoginAsync` waiting, since it holds the values that complete the
sign in, and the link has to be opened on the same device. If that's a problem, use OTP codes or enchanted links,
which finish in the original sign-in page.

### Restore the session on launch

```csharp
var session = await descope.SessionManager.LoadSessionAsync();
if (session != null)
{
    // the user is still signed in
}
```

A saved session whose refresh token has expired is removed and returned as `null`, since it can't be refreshed.
After a login or logout, `LoadSessionAsync` returns the current session without reading the storage again.

### Session refresh

While the app is in the foreground, the session manager checks the session every 30 seconds and refreshes it
when its JWT expires within 60 seconds. The check pauses while the app is in the background and runs right away
when it returns. A network error is retried on the next check. Any other failure, such as a refresh token that
was revoked, stops the checks until a new session is set or the app returns to the foreground. The session isn't
cleared, so the app sees the error the next time it refreshes the session itself.

```csharp
descope.SessionManager.RefreshTriggerInterval = TimeSpan.FromMinutes(2); // refresh earlier
descope.SessionManager.PeriodicCheckFrequency = TimeSpan.Zero;           // or turn off automatic refresh
```

### Call your backend

The session manager refreshes the session JWT shortly before it expires. Before calling your backend, call
`RefreshSessionIfNeededAsync`, or use `DescopeAuthorizationHandler` to do it for every request:

```csharp
var api = new HttpClient(new DescopeAuthorizationHandler(descope.SessionManager)
{
    InnerHandler = new HttpClientHandler(),
});
```

Your backend must validate the session JWT, for example with the [Descope .NET SDK](https://github.com/descope/descope-dotnet).

### User, tenants and roles

```csharp
var session = descope.SessionManager.Session!;
session.User.UserId;
session.User.Name;
session.TenantIds;            // tenants the user belongs to
session.Roles();              // project-level roles
session.Roles("T2abc...");    // roles in a tenant
session.Permissions("T2abc...");
session.SessionToken.Claims;  // custom claims
```

Tenant roles and custom claims need the `descope.claims` and `descope.custom_claims` scopes, which are requested by
default (`DescopeOptions.Scope`).

### Sign out

```csharp
await descope.LogoutAsync();
```

This revokes the refresh token and clears the stored session. On Android, the browser's Descope session is kept
unless you pass `endBrowserSession: true`, so the next sign-in may skip the login screen. On iOS the flow runs in an
ephemeral browser session by default (`DescopeOptions.PrefersEphemeralWebBrowserSession`), so there's nothing to end.

### Listen for session changes

```csharp
descope.SessionManager.SessionChanged += (_, session) =>
{
    // session is null after logout; may be raised on a background thread
};
```

## Customization

Pass your own implementations to the `DescopeClient` constructor:

- `IDescopeSessionStorage`: where the session is persisted. The default, `SecureSessionStorage`, uses MAUI `SecureStorage`
  (Keychain / Keystore). On Android, exclude `SecureStorage` from auto backup, as described in the MAUI docs.
- `IDescopeBrowser`: how the flow URL is opened. The default uses MAUI `WebAuthenticator`.
- `HttpClient`: used for the token, refresh and revoke requests.

## Development

```bash
dotnet test Descope.Maui.Test
```

The unit tests run against the `net10.0` target and don't need the mobile workloads. Building the iOS and Android
targets needs `dotnet workload install ios android`, plus Xcode or the Android SDK.

## License

MIT, see [LICENSE](LICENSE).
