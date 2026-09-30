#if ANDROID
using Android.Content;
using Android.Net.Wifi;

namespace Concierge.Platforms.Android;

/// <summary>Temporarily allows Android Wi-Fi to deliver the local Desktop beacon.</summary>
internal static class WifiMulticastLease
{
    public static IDisposable Acquire()
    {
        var wifi = global::Android.App.Application.Context
            .GetSystemService(Context.WifiService) as WifiManager
            ?? throw new InvalidOperationException("Android Wi-Fi discovery is unavailable.");
        var lease = wifi.CreateMulticastLock("ConciergeDesktopDiscovery")
            ?? throw new InvalidOperationException("Android could not create a Wi-Fi multicast lock.");
        lease.SetReferenceCounted(false);
        lease.Acquire();
        return new ReleaseOnDispose(lease);
    }

    private sealed class ReleaseOnDispose(WifiManager.MulticastLock lease) : IDisposable
    {
        public void Dispose()
        {
            if (lease.IsHeld)
                lease.Release();
            lease.Dispose();
        }
    }
}
#endif
