namespace Concierge.Shared.Components;

/// <summary>
/// Empty marker type so hosts can locate this assembly via <c>typeof(ComponentAssemblyMarker).Assembly</c>
/// when calling <c>AddAdditionalAssemblies(...)</c>. Razor-only assemblies have no C# entry point;
/// the marker keeps the reference resilient to refactors of the generated _Imports type.
/// </summary>
public static class ComponentAssemblyMarker
{
}
