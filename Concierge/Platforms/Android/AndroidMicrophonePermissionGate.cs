using Concierge.Shared.Components.Workspace;
using Microsoft.Maui.ApplicationModel;

namespace Concierge.Platforms.Android;

internal sealed class AndroidMicrophonePermissionGate : IMicrophonePermissionGate
{
    public async Task<bool> EnsureGrantedAsync()
    {
        var status = await Permissions.CheckStatusAsync<Permissions.Microphone>();
        if (status != PermissionStatus.Granted)
            status = await Permissions.RequestAsync<Permissions.Microphone>();

        return status == PermissionStatus.Granted;
    }
}
