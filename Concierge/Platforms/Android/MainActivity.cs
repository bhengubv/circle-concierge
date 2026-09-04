using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.Core.View;

namespace Concierge;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    /// <summary>
    /// Keep the app out from under the status and gesture bars.
    ///
    /// Android 15 draws every app edge to edge for targetSdk 35 and above, and
    /// this one targets 36. On a phone that put the workspace top bar — the
    /// title and the menu control that is the only way to the sidebar —
    /// entirely underneath a status bar taller than it, and cut "Shift + Enter
    /// for a new line" off behind the gesture bar.
    ///
    /// The CSS answer does not work here. env(safe-area-inset-*) is populated
    /// for a browser's own display cutout, not for a WebView embedded in an
    /// app: the host already asks for viewport-fit=cover and the values still
    /// came back zero. SetDecorFitsSystemWindows(true) is no help either — it
    /// is ignored under the Android 15 enforcement.
    ///
    /// So the insets are read where they exist and applied as padding to the
    /// content view. The window still paints edge to edge, which is why the
    /// system bars show the app's own background rather than a letterbox.
    /// </summary>
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var content = Window?.DecorView?.FindViewById(Android.Resource.Id.Content);
        if (content is null)
        {
            return;
        }

        ViewCompat.SetOnApplyWindowInsetsListener(content, new InsetPadding());
        ViewCompat.RequestApplyInsets(content);
    }

    private sealed class InsetPadding : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat OnApplyWindowInsets(Android.Views.View? view, WindowInsetsCompat? insets)
        {
            if (view is null || insets is null)
            {
                return insets ?? WindowInsetsCompat.Consumed;
            }

            // System bars and the display cutout both matter: a punch-hole or
            // notch is not covered by the status bar inset on every device.
            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout());

            view.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);

            // Returned unconsumed: the keyboard inset is handled separately by
            // the WebView, and swallowing everything here would break it.
            return insets;
        }
    }
}
