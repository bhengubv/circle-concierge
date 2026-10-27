using Android.Webkit;

namespace Concierge.Platforms.Android;

/// <summary>
/// Allows the trusted BlazorWebView page to capture audio only after the user
/// has granted the Android app's runtime microphone permission.
/// </summary>
internal sealed class ConciergeMicrophoneWebChromeClient(WebChromeClient? previous) : WebChromeClient
{
    public override void OnPermissionRequest(PermissionRequest? request)
    {
        if (request is null)
            return;

        var resources = request.GetResources();
        if (resources.Length == 1
            && string.Equals(resources[0], PermissionRequest.ResourceAudioCapture, StringComparison.Ordinal)
            && global::Android.App.Application.Context.CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio)
                == global::Android.Content.PM.Permission.Granted)
        {
            request.Grant(resources);
            return;
        }

        // Keep the framework's normal handling for other WebView capabilities.
        previous?.OnPermissionRequest(request);
    }

    public override void OnPermissionRequestCanceled(PermissionRequest? request)
    {
        previous?.OnPermissionRequestCanceled(request);
    }

    public override bool OnShowFileChooser(global::Android.Webkit.WebView? webView, IValueCallback? filePathCallback,
        FileChooserParams? fileChooserParams)
    {
        return previous?.OnShowFileChooser(webView, filePathCallback, fileChooserParams) ?? false;
    }

    public override bool OnCreateWindow(global::Android.Webkit.WebView? webView, bool isDialog, bool isUserGesture,
        global::Android.OS.Message? resultMsg)
    {
        return previous?.OnCreateWindow(webView, isDialog, isUserGesture, resultMsg) ?? false;
    }

    public override void OnShowCustomView(global::Android.Views.View? view, ICustomViewCallback? callback)
    {
        previous?.OnShowCustomView(view, callback);
    }

    public override void OnHideCustomView()
    {
        previous?.OnHideCustomView();
    }
}
