using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.Core.View;

namespace Concierge;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, WindowSoftInputMode = SoftInput.AdjustResize, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private const int CircleAiLinkRequest = 0xC1A3;
    private TaskCompletionSource<bool>? _circleAiLink;

    // Start FOR RESULT: Android supplies the real calling package to Circle AI.
    // No client-written grant, signature bypass, or impersonated approval.
    public Task<bool> RequestCircleAiLinkAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_circleAiLink is null)
        {
            _circleAiLink = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                using var intent = new global::Android.Content.Intent(CircleAI.Linking.LinkIpc.ConsentAction);
                intent.SetPackage(CircleAI.Linking.LinkIpc.HostPackage);
                intent.PutExtra(CircleAI.Linking.LinkIpc.ScopeExtra, (int)CircleAI.Linking.LinkScope.Chat);
                StartActivityForResult(intent, CircleAiLinkRequest);
            }
            catch
            {
                _circleAiLink = null;
                throw;
            }
        }
        // A cancelled view stops waiting, but cannot open a second approval sheet.
        // The OS result remains the authority and releases this pending request.
        return _circleAiLink.Task.WaitAsync(cancellationToken);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, global::Android.Content.Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != CircleAiLinkRequest) return;
        var pending = _circleAiLink;
        _circleAiLink = null;
        pending?.TrySetResult(resultCode == Result.Ok);
    }

    protected override void OnDestroy()
    {
        _circleAiLink?.TrySetCanceled();
        _circleAiLink = null;
        base.OnDestroy();
    }

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
        // Set this before MAUI starts its background hosted services. Circle AI
        // must use the phone's ActivityManager and app-volume readings rather
        // than the much smaller managed-heap limit and sandbox DriveInfo guess.
        var initialResources = Concierge.Platforms.Android.AndroidDeviceResourceProbe.Read();
        CircleAI.Core.DeviceProbe.PlatformMemoryProbe = Concierge.Platforms.Android.AndroidDeviceResourceProbe.Read;
        global::Android.Util.Log.Warn(
            "Concierge.DeviceProbe",
            $"Registered Android resource probe: available={initialResources.RamAvailableBytes}, total={initialResources.RamTotalBytes}, storage={initialResources.StorageFreeBytes}.");

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
                return insets ?? WindowInsetsCompat.Consumed!;
            }

            // System bars and the display cutout both matter: a punch-hole or
            // notch is not covered by the status bar inset on every device.
            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout())!;

            view!.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);

            // MAUI's WebView did not shrink its viewport when the Android IME
            // opened on the Android 16 handset: the keyboard covered the composer
            // and send button. In edge-to-edge mode, apply the larger bottom inset
            // here so the WebView lays itself out above the keyboard. Keep the
            // insets unconsumed so descendants can still react to them.
            var ime = insets!.GetInsets(WindowInsetsCompat.Type.Ime())!;
            view!.SetPadding(bars.Left, bars.Top, bars.Right, Math.Max(bars.Bottom, ime.Bottom));

            return insets!;
        }
    }
}
