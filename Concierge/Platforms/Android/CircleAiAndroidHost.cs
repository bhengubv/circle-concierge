using Android.Content;
using Android.Content.PM;
using CircleAI.Linking;

namespace Concierge.Platforms.Android;

/// <summary>
/// Circle OS publishes the same cross-app Circle AI link from its native system
/// package. Select that host on Circle OS; use the SDK's standalone host elsewhere.
/// Keep one host for the process lifetime so a conversation cannot switch brains.
/// </summary>
internal static class CircleAiAndroidHost
{
    private const string NativePackage = "za.co.circleos.circleaiservice";
    private static readonly Lazy<string> Selected = new(Resolve);

    internal static string Package => Selected.Value;

    private static string Resolve()
    {
        var context = global::Android.App.Application.Context;
        var manager = context.PackageManager;
        if (manager is null) return LinkIpc.HostPackage;

        try
        {
            var info = manager.GetPackageInfo(NativePackage, (PackageInfoFlags)0);
            var flags = info?.ApplicationInfo?.Flags ?? (ApplicationInfoFlags)0;
            if ((flags & (ApplicationInfoFlags.System | ApplicationInfoFlags.UpdatedSystemApp)) == 0)
                return LinkIpc.HostPackage;

            using var intent = new Intent(LinkIpc.BindAction);
            intent.SetPackage(NativePackage);
            var service = manager.ResolveService(intent, (PackageInfoFlags)0)?.ServiceInfo;
            if (service is { Exported: true } && service.PackageName == NativePackage)
                return NativePackage;
        }
        catch (PackageManager.NameNotFoundException)
        {
            // A normal Android phone uses the separately installed Circle AI host.
        }
        catch (Java.Lang.SecurityException)
        {
            // Package visibility or device policy refused this host.
        }

        return LinkIpc.HostPackage;
    }
}
