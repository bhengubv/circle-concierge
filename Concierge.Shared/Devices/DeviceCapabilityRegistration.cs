using Concierge.Shared.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Concierge.Shared.Devices;

/// <summary>
/// Wiring the device into the catalogue.
/// </summary>
public static class DeviceCapabilityServiceCollectionExtensions
{
    /// <summary>
    /// Registers the capabilities that hold on every head, and the source that
    /// publishes whatever this device turns out to have.
    ///
    /// A head calls this and then adds its own: <c>services.AddSingleton&lt;
    /// IDeviceCapability, ClipboardCapability&gt;()</c> and so on. Nothing here
    /// knows what those will be, which is the point — the shared library must not
    /// contain a list of what a phone can do, or it will be wrong about phones.
    /// </summary>
    public static IServiceCollection AddConciergeDeviceCapabilities(this IServiceCollection services)
    {
        services.AddSingleton<IDeviceCapability, DeviceInfoCapability>();
        services.AddSingleton<IDeviceCapability, NetworkStateCapability>();

        // TryAddEnumerable rather than AddSingleton: a host that calls this twice
        // — easy to do when heads share composition — would otherwise publish the
        // whole device catalogue twice, and the model would see every capability
        // in duplicate with no way to tell which to call.
        //
        // Registered by type rather than by factory, which is not a style choice.
        // TryAddEnumerable deduplicates on the implementation type, so a factory
        // descriptor gives it nothing to compare and it throws — during service
        // composition, which on a MAUI head means the window never appears and
        // the only trace is a stowed exception in Microsoft.UI.Xaml. Both
        // constructor arguments resolve from the container anyway.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, DeviceCapabilitySource>());

        return services;
    }
}
