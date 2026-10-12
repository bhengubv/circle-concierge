using Android.App;
using Android.Content;
using Android.OS;
using CircleAI.Core;
using Microsoft.Maui.Storage;

namespace Concierge.Platforms.Android;

/// <summary>
/// Supplies Circle AI with Android's device-level resource readings. The GC heap
/// limit is not the phone's available RAM; using it makes a phone appear unable
/// to run even the smallest model.
/// </summary>
internal static class AndroidDeviceResourceProbe
{
    public static DeviceProbe.PlatformMemory Read()
    {
        var manager = global::Android.App.Application.Context.GetSystemService(global::Android.Content.Context.ActivityService) as ActivityManager;
        if (manager is null)
            return default;

        var memory = new ActivityManager.MemoryInfo();
        manager.GetMemoryInfo(memory);

        long? storageAvailable = null;
        try
        {
            var stats = new StatFs(FileSystem.AppDataDirectory);
            storageAvailable = stats.AvailableBytes;
        }
        catch
        {
            // Keep RAM usable even if the app data volume cannot be queried.
        }

        global::Android.Util.Log.Info(
            "Concierge.DeviceProbe",
            $"Android resources: availableRam={memory.AvailMem}, totalRam={memory.TotalMem}, freeStorage={storageAvailable?.ToString() ?? "unknown"}.");

        return new DeviceProbe.PlatformMemory(
            RamAvailableBytes: memory.AvailMem,
            StorageFreeBytes: storageAvailable,
            RamTotalBytes: memory.TotalMem);
    }
}
