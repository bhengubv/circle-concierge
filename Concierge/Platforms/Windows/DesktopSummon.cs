using System.Runtime.InteropServices;
using Concierge.Shared.Devices;

namespace Concierge.Platforms.Windows;

/// <summary>A registered Windows hotkey; no background keyboard monitoring.</summary>
public sealed class DesktopSummon : IDesktopSummon
{
    private const int HotkeyId = 0x4343;
    private const uint HotkeyMessage = 0x0312;
    private readonly SubclassProc _callback;
    private nint _handle;
    private bool _registered, _subclassed;
    public bool Enabled { get; private set; } = Preferences.Default.Get("desktop.summon.enabled", true);
    public string Shortcut { get; private set; } = Preferences.Default.Get("desktop.summon.shortcut", "Ctrl + Alt + C");
    public string Status { get; private set; } = "Connecting the shortcut to Concierge…";
    public event Action? Summoned;
    public DesktopSummon() => _callback = WindowMessage;

    public void Attach(Microsoft.Maui.Controls.Window window)
    {
        window.Created += (_, _) =>
        {
            if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native) return;
            _handle = WinRT.Interop.WindowNative.GetWindowHandle(native);
            _subclassed = SetWindowSubclass(_handle, _callback, (nuint)HotkeyId, 0);
            Apply();
        };
        window.Destroying += (_, _) =>
        {
            if (_registered) UnregisterHotKey(_handle, HotkeyId);
            if (_subclassed) RemoveWindowSubclass(_handle, _callback, (nuint)HotkeyId);
            _handle = 0; _registered = _subclassed = false;
        };
    }

    public Task SetEnabledAsync(bool enabled) => MainThread.InvokeOnMainThreadAsync(() =>
    {
        Enabled = enabled;
        Preferences.Default.Set("desktop.summon.enabled", enabled);
        Apply();
    });

    public Task SetShortcutAsync(string shortcut) => MainThread.InvokeOnMainThreadAsync(() =>
    {
        if (shortcut is not ("Ctrl + Alt + C" or "Ctrl + Alt + Space" or "Ctrl + Shift + Space"))
            throw new ArgumentException("Choose a supported shortcut.");
        Shortcut = shortcut;
        Preferences.Default.Set("desktop.summon.shortcut", shortcut);
        Apply();
    });

    private void Apply()
    {
        if (_registered) { UnregisterHotKey(_handle, HotkeyId); _registered = false; }
        if (!Enabled) { Status = "Quick summon is off."; return; }
        if (!_subclassed) { Status = "Quick summon could not attach to this window."; return; }
        var modifiers = Shortcut == "Ctrl + Shift + Space" ? 0x4006u : 0x4003u;
        var key = Shortcut == "Ctrl + Alt + C" ? 0x43u : 0x20u;
        _registered = RegisterHotKey(_handle, HotkeyId, modifiers, key);
        Status = _registered ? $"{Shortcut} brings Concierge forward while it is running."
            : $"{Shortcut} is unavailable (Windows error {Marshal.GetLastWin32Error()}). Choose another shortcut; another app may own it.";
    }

    private nint WindowMessage(nint window, uint message, nuint wParam, nint lParam, nuint subclassId, nuint data)
    {
        if (message == HotkeyMessage && wParam == (nuint)HotkeyId)
        {
            if (IsIconic(window)) ShowWindow(window, 9);
            SetForegroundWindow(window);
            // Never allow a subscriber exception to cross the native callback boundary.
            try { Summoned?.Invoke(); }
            catch (Exception ex) { Status = $"Concierge opened, but could not focus the conversation: {ex.Message}"; }
            return 0;
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint subclassId, nuint data);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
}
