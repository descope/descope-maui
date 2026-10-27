using Android.App;
using Android.OS;

namespace Descope.Maui.Internal;

internal sealed partial class AppLifecycle
{
    partial void Observe()
    {
        if (Application.Context is Application application)
        {
            application.RegisterActivityLifecycleCallbacks(new ActivityCallbacks(this));
        }
    }

    // Counts started activities like androidx ProcessLifecycleOwner does, so the app is in the background
    // once none are visible. Activities started before this was registered aren't counted, so the first
    // stop without a matching start still moves the app to the background.
    private sealed class ActivityCallbacks(AppLifecycle lifecycle) : Java.Lang.Object, Application.IActivityLifecycleCallbacks
    {
        private int _started;

        public void OnActivityStarted(Activity activity)
        {
            _started++;
            lifecycle.SetForeground(true);
        }

        public void OnActivityStopped(Activity activity)
        {
            _started = Math.Max(0, _started - 1);
            if (_started == 0) lifecycle.SetForeground(false);
        }

        public void OnActivityCreated(Activity activity, Bundle? savedInstanceState) { }

        public void OnActivityDestroyed(Activity activity) { }

        public void OnActivityPaused(Activity activity) { }

        public void OnActivityResumed(Activity activity) { }

        public void OnActivitySaveInstanceState(Activity activity, Bundle outState) { }
    }
}
