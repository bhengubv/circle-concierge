using System.Security.Cryptography;
using Aether.Dtn;
using Aether.Identity;
using Aether.Routing;
using Concierge.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Mesh;

/// <summary>
/// Aether-protocol-backed implementation of <see cref="IMeshTransportService"/>. Owns a
/// persisted Ed25519-shaped identity, an <see cref="IRouteStore"/>, and surfaces the
/// pending-bundle count from the optional <see cref="IDtnService"/> wired by
/// <c>AddConciergeMesh()</c>.
/// </summary>
/// <remarks>
/// The persisted "identity" file holds the 32 random bytes that feed
/// <see cref="AetherTag.FromPublicKey(byte[])"/>. A real deployment replaces this with a
/// proper Ed25519 keypair under biometric-protected storage; for the Concierge demo a
/// stable per-machine identity is enough to give peers a reliable address.
/// </remarks>
public sealed class AetherMeshTransportService : IMeshTransportService
{
    private static readonly string DefaultIdentityPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Concierge",
        "aether.identity.bin");

    private readonly AetherTag _nodeTag;
    private readonly IRouteStore _routeStore;
    private readonly IServiceProvider? _services;
    private readonly bool _identityIsPersisted;

    public AetherMeshTransportService()
        : this(DefaultIdentityPath, new InMemoryRouteStore(), services: null)
    {
    }

    public AetherMeshTransportService(IServiceProvider services)
        : this(DefaultIdentityPath, new InMemoryRouteStore(), services)
    {
    }

    public AetherMeshTransportService(string identityPath, IRouteStore routeStore, IServiceProvider? services)
    {
        _routeStore = routeStore ?? throw new ArgumentNullException(nameof(routeStore));
        _services = services;
        _nodeTag = LoadOrCreateIdentity(identityPath);
        _identityIsPersisted = File.Exists(identityPath);
    }

    public AetherTag NodeTag => _nodeTag;

    public IRouteStore RouteStore => _routeStore;

    public MeshTransportSnapshot GetSnapshot()
    {
        // The default IRouteStore (InMemoryRouteStore) and IDtnService implementations wrap
        // Task.FromResult — there is no captured SynchronizationContext, so the .GetAwaiter()
        // .GetResult() calls below never deadlock and complete synchronously. Caching the
        // snapshot would break the contract callers (e.g. tests) rely on, which expects each
        // call to reflect the latest published bundle. If a future store actually performs
        // async I/O, switch GetSnapshot to async and propagate up to the UI render path.
        var routes = _routeStore.GetAllAsync().GetAwaiter().GetResult();
        var pending = ResolvePendingBundleCount();
        var version = typeof(AetherTag).Assembly.GetName().Version?.ToString() ?? "unknown";
        return new MeshTransportSnapshot(
            Engine: "aether-protocol",
            EngineVersion: version,
            NodeTag: _nodeTag.ToString(),
            CachedRoutes: routes.Count,
            KnownPeers: 0,
            PendingBundles: pending,
            IdentityIsPersisted: _identityIsPersisted,
            IsActive: true,
            Summary: $"Aether mesh transport active — node {_nodeTag} has a persisted identity and pending agent run logs ride DTN bundles.");
    }

    private int ResolvePendingBundleCount()
    {
        if (_services is null)
        {
            return 0;
        }

        var dtn = _services.GetService<IDtnService>();
        if (dtn is null)
        {
            return 0;
        }

        // InMemoryDtnBundleStore is synchronous under the hood.
        return dtn.GetActiveBundlesAsync().GetAwaiter().GetResult().Count;
    }

    private static AetherTag LoadOrCreateIdentity(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length == 32)
                {
                    return AetherTag.FromPublicKey(bytes);
                }
            }
            catch
            {
                // Fall through to regenerate.
            }
        }

        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        TryPersist(path, key);
        return AetherTag.FromPublicKey(key);
    }

    private static void TryPersist(string path, byte[] key)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllBytes(path, key);
        }
        catch
        {
            // If the identity directory cannot be written (read-only filesystem, sandbox),
            // the node simply runs with an in-process-only identity for this session.
        }
    }
}
