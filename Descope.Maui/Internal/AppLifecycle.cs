namespace Descope.Maui.Internal;

// Tells the session manager when the app moves between the foreground and the background
internal interface IAppLifecycle
{
    bool IsForeground { get; }

    event EventHandler<bool>? ForegroundChanged;
}

// The platform files observe the app's state, other platforms are always in the foreground
internal sealed partial class AppLifecycle : IAppLifecycle
{
    public static AppLifecycle Shared { get; } = new();

    private AppLifecycle() => Observe();

    public bool IsForeground { get; private set; } = true;

    public event EventHandler<bool>? ForegroundChanged;

    partial void Observe();

    private void SetForeground(bool isForeground)
    {
        if (IsForeground == isForeground) return;
        IsForeground = isForeground;
        ForegroundChanged?.Invoke(this, isForeground);
    }
}
