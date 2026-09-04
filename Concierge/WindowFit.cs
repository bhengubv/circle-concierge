namespace Concierge;

/// <summary>
/// Keeps the window inside the screen it is on.
///
/// The bug this exists for: the shell is <c>height: 100dvh</c>, so the bottom
/// navigation is pinned to the bottom edge of the window. If the window is ever
/// taller than the display, that edge — and every tab on it — is off the glass,
/// and the app has no visible navigation at all. It is not a rendering fault
/// and no stylesheet can correct it; the window is simply bigger than the
/// screen. Seen for real on a 1366x768 laptop with an 900-unit-tall window.
///
/// Nothing here hard-codes a screen size. The bounds are read from the display
/// the window is actually on, and re-read when that changes — a second monitor,
/// a resolution change, a rotation, a DPI change.
///
/// On Windows the WORK area is used rather than the full display, because the
/// taskbar owns the rest and a window sized to the full height still has its
/// bottom strip covered. Other platforms fall back to the display bounds, which
/// is the best MAUI exposes cross-platform.
/// </summary>
internal static class WindowFit
{
    // Floors, not screen sizes: the smallest window that can still render the
    // shell. Both are clamped against the display before use, so a device
    // smaller than this is still handled.
    private const double FloorWidth = 320;
    private const double FloorHeight = 400;

    internal static void Apply(Window window)
    {
        // Before the first frame, using whatever the display reports.
        Clamp(window);

        // Created is when the platform view exists, which is the first moment
        // the accurate per-window work area can be read on Windows.
        window.Created += (_, _) => Clamp(window);

        // A window dragged to a second monitor, a resolution change, a rotation
        // on a tablet: all of them can make the current size too large.
        DeviceDisplay.Current.MainDisplayInfoChanged += (_, _) =>
            MainThread.BeginInvokeOnMainThread(() => Clamp(window));
    }

    private static void Clamp(Window window)
    {
        var (availableWidth, availableHeight) = Available(window);
        if (availableWidth <= 0 || availableHeight <= 0)
        {
            // No usable reading — leave the window alone rather than resize it
            // to a guess.
            return;
        }

        window.MinimumWidth = Math.Min(FloorWidth, availableWidth);
        window.MinimumHeight = Math.Min(FloorHeight, availableHeight);

        // The ceiling is the real fix: the platform will not let the user or
        // the app grow the window past the screen from here on.
        window.MaximumWidth = availableWidth;
        window.MaximumHeight = availableHeight;

        // And bring a window that is already too big back inside. Only shrink —
        // a window smaller than the screen is a legitimate choice and not ours
        // to overrule.
        if (!double.IsNaN(window.Width) && window.Width > availableWidth)
        {
            window.Width = availableWidth;
        }

        if (!double.IsNaN(window.Height) && window.Height > availableHeight)
        {
            window.Height = availableHeight;
        }
    }

    /// <summary>
    /// The usable size of the display this window is on, in the same
    /// device-independent units <see cref="Window"/> uses.
    /// </summary>
    private static (double Width, double Height) Available(Window window)
    {
#if WINDOWS
        // The work area excludes the taskbar. Sizing to the full display height
        // leaves the bottom of the window — the tab bar — behind it.
        try
        {
            if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window platformWindow)
            {
                var handle = WinRT.Interop.WindowNative.GetWindowHandle(platformWindow);
                var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
                var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                    id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);

                // WorkArea is physical pixels; the window is not.
                var scale = platformWindow.Content?.XamlRoot?.RasterizationScale ?? 1d;
                if (scale <= 0)
                {
                    scale = 1d;
                }

                if (area.WorkArea.Width > 0 && area.WorkArea.Height > 0)
                {
                    return (area.WorkArea.Width / scale, area.WorkArea.Height / scale);
                }
            }
        }
        catch
        {
            // Fall through to the cross-platform reading below. A window that
            // fits the display but sits under the taskbar is a far smaller
            // problem than a crash on startup.
        }
#endif

        var display = DeviceDisplay.Current.MainDisplayInfo;
        var density = display.Density > 0 ? display.Density : 1d;
        return (display.Width / density, display.Height / density);
    }
}
