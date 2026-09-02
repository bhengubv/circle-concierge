using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Concierge.Shared.Tools;

namespace Concierge.Shared.Extensibility;

/// <summary>What a loaded plugin contributed.</summary>
/// <param name="Name">The assembly it came from.</param>
/// <param name="Tools">The tools it offers.</param>
public sealed record LoadedPlugin(string Name, IReadOnlyList<IAgentTool> Tools);

/// <summary>
/// Loads and unloads plugin assemblies while the app is running.
/// </summary>
/// <remarks>
/// <para>
/// Built on a collectible <see cref="AssemblyLoadContext"/>, which is what makes unloading
/// possible at all: an assembly loaded into the default context stays for the life of the
/// process, so a plugin that could only be added would eventually be a leak with a nice name.
/// </para>
/// <para>
/// Unloading is cooperative, not immediate. The context is released when nothing holds a
/// reference into it, which means a caller keeping a tool alive keeps the assembly alive too.
/// That is a real constraint, not a bug, and it is why <see cref="Unload"/> drops the plugin's
/// tools before requesting collection.
/// </para>
/// </remarks>
public sealed class PluginHost : IDisposable
{
    private readonly Dictionary<string, PluginLoadContext> _loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The plugins currently loaded.</summary>
    public IReadOnlyCollection<string> Loaded => _loaded.Keys.ToList();

    /// <summary>
    /// Load an assembly and take whatever tools it offers.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file is missing or is not a plugin.</exception>
    public LoadedPlugin Load(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);

        if (!File.Exists(assemblyPath))
        {
            throw new InvalidOperationException($"No plugin at '{assemblyPath}'.");
        }

        var name = Path.GetFileNameWithoutExtension(assemblyPath);
        if (_loaded.ContainsKey(name))
        {
            throw new InvalidOperationException($"Plugin '{name}' is already loaded.");
        }

        var context = new PluginLoadContext(assemblyPath);
        try
        {
            var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
            var tools = Discover(assembly);

            _loaded[name] = context;
            return new LoadedPlugin(name, tools);
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            // A half-loaded context would keep the file locked and the memory held.
            context.Unload();
            throw new InvalidOperationException($"'{name}' could not be loaded as a plugin: {exception.Message}");
        }
    }

    /// <summary>
    /// Unload a plugin. Returns whether the assembly was actually released — false means
    /// something still holds a reference into it.
    /// </summary>
    public bool Unload(string name)
    {
        if (!_loaded.Remove(name, out var context))
        {
            return false;
        }

        var reference = new WeakReference(context, trackResurrection: true);
        context.Unload();

        // Collection is what actually releases the assembly, and it cannot happen while a
        // caller still holds one of the plugin's objects. Two passes is what the runtime
        // documents as sufficient when nothing does.
        for (var attempt = 0; attempt < 2 && reference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        return !reference.IsAlive;
    }

    /// <summary>
    /// Every public tool the assembly offers.
    /// </summary>
    /// <remarks>
    /// Only types with a parameterless constructor are taken. A plugin tool needing services
    /// would have to be handed the host's container, which is exactly the reach a plugin
    /// should not be given.
    /// </remarks>
    private static IReadOnlyList<IAgentTool> Discover(Assembly assembly)
    {
        var tools = new List<IAgentTool>();

        foreach (var type in assembly.GetExportedTypes())
        {
            if (!typeof(IAgentTool).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface)
            {
                continue;
            }

            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                continue;
            }

            if (Activator.CreateInstance(type) is IAgentTool tool)
            {
                tools.Add(tool);
            }
        }

        return tools;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var name in _loaded.Keys.ToList())
        {
            Unload(name);
        }
    }

    /// <summary>
    /// A collectible context that resolves a plugin's own dependencies from beside it, while
    /// leaving anything the host already has to the host.
    /// </summary>
    /// <remarks>
    /// Shared types must come from the host, or a plugin's <c>IAgentTool</c> would be a
    /// different type from the host's and nothing would match.
    /// </remarks>
    private sealed class PluginLoadContext(string assemblyPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Returning null defers to the default context, which is what keeps shared
            // interfaces identical on both sides of the boundary.
            if (Default.Assemblies.Any(loaded => loaded.GetName().Name == assemblyName.Name))
            {
                return null;
            }

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}
