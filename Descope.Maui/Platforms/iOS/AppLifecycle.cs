using Foundation;
using UIKit;

namespace Descope.Maui.Internal;

internal sealed partial class AppLifecycle
{
    private NSObject[]? _observers;

    partial void Observe()
    {
        // the current state can only be read on the main thread, otherwise assume the app is in the foreground
        if (NSThread.IsMain)
        {
            IsForeground = UIApplication.SharedApplication.ApplicationState != UIApplicationState.Background;
        }
        var center = NSNotificationCenter.DefaultCenter;
        _observers =
        [
            center.AddObserver(UIApplication.WillEnterForegroundNotification, _ => SetForeground(true)),
            center.AddObserver(UIApplication.DidEnterBackgroundNotification, _ => SetForeground(false)),
        ];
    }
}
