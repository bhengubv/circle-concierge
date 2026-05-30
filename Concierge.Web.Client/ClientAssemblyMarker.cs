namespace Concierge.Web.Client;

/// <summary>
/// Empty marker type so the server host can reference the WebAssembly client assembly via
/// <c>typeof(ClientAssemblyMarker).Assembly</c> when calling <c>AddAdditionalAssemblies(...)</c>.
/// </summary>
public static class ClientAssemblyMarker
{
}
