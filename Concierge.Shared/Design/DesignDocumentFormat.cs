using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Concierge.Shared.Design;

/// <summary>
/// Writing a design down, and reading one back that was written by an older
/// Concierge.
///
/// Built before anything is saved rather than after, which is the only cheap
/// moment to do it. A document with no version number is fine right up until the
/// first person has one on disk; from then on, every change to the shape is a
/// choice between breaking their work and carrying the old shape forever. Pascal
/// carries seven migration test files for exactly this reason and we had none,
/// because nothing was persisted yet — which is a gap, not a defence.
///
/// Three rules, each of them a decision rather than an implementation detail.
///
/// **A version is written even when there is only one.** Version 1 has nothing to
/// migrate from. Writing it anyway is what makes version 2 possible without
/// guessing, because a file that does not say what it is can only be identified by
/// sniffing its contents, and sniffing is how a loader silently mangles something
/// it half-recognises.
///
/// **Older is upgraded, newer is refused.** A document from a future Concierge may
/// contain a node kind, a medium, or a property this build has never heard of.
/// Loading it by ignoring the parts we do not understand would open a design that
/// silently lost half of itself, and the person would then save it back over the
/// good copy. Refusing is the answer that keeps their work.
///
/// **Order is part of the document.** Children are held in insertion order in a
/// list that is private to <see cref="DesignDocument"/>, because a person who adds
/// a heading and then a paragraph expects them in that order. A serialiser that
/// wrote only the node dictionary would lose it and nobody would notice until a
/// reopened deck had its slides shuffled.
/// </summary>
public static class DesignDocumentFormat
{
    /// <summary>
    /// The shape this build writes.
    ///
    /// Bump when the on-disk shape changes in a way an older reader could not
    /// understand, and add a migration from the previous number in the same
    /// change. Adding an optional property that older readers can ignore does not
    /// need a bump; removing or renaming one does.
    /// </summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        // Written for a person to be able to open in an editor and see what
        // happened to their design. A saved document is a record of somebody's
        // work; if it goes wrong they should be able to look.
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Writes a design out.</summary>
    public static string Serialize(DesignDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var wire = new DesignDocumentWire(
            SchemaVersion: CurrentVersion,
            Medium: document.Medium.ToString(),
            Look: document.Look,
            RootId: document.RootId,
            Order: [.. document.SerializationOrder],
            Nodes: [.. document.SerializationOrder
                .Where(document.Nodes.ContainsKey)
                .Select(id => document.Nodes[id])
                .Select(ToWire)],
            Root: ToWire(document.Nodes[document.RootId]));

        return JsonSerializer.Serialize(wire, Json);
    }

    /// <summary>
    /// Reads a design back, upgrading it if it was written by an older build.
    /// </summary>
    /// <returns>
    /// True when <paramref name="document"/> is a design this build fully
    /// understands. False when it is not, with <paramref name="problem"/> saying
    /// why in words that can be shown to a person — a loader that fails silently
    /// and hands back an empty page is worse than one that refuses, because the
    /// empty page gets saved back over the real one.
    /// </returns>
    public static bool TryDeserialize(
        string? json,
        out DesignDocument? document,
        out string? problem)
    {
        document = null;
        problem = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            problem = "There is nothing to open.";
            return false;
        }

        DesignDocumentWire? wire;
        try
        {
            wire = JsonSerializer.Deserialize<DesignDocumentWire>(json, Json);
        }
        catch (JsonException error)
        {
            problem = $"This does not look like a saved design: {error.Message}";
            return false;
        }

        if (wire is null)
        {
            problem = "This does not look like a saved design.";
            return false;
        }

        if (wire.SchemaVersion <= 0)
        {
            // No version at all. Everything this build writes carries one, so a
            // document without one was not written by Concierge — guessing at its
            // shape is how a loader produces a plausible-looking wrong answer.
            problem = "This design does not say which version it is, so it cannot be opened safely.";
            return false;
        }

        if (wire.SchemaVersion > CurrentVersion)
        {
            problem =
                $"This design was saved by a newer Concierge (version {wire.SchemaVersion}; this one reads up to "
                + $"{CurrentVersion}). Opening it here would drop whatever is new, so it has been left alone.";
            return false;
        }

        // Upgrade one step at a time rather than jumping straight to current.
        // A chain means version 3 only ever has to know how to come from
        // version 2, and a document from version 1 arrives having been through
        // every step — which is also what makes each step testable on its own.
        for (var from = wire.SchemaVersion; from < CurrentVersion; from++)
        {
            if (!Migrations.TryGetValue(from, out var migrate))
            {
                problem =
                    $"This design is version {from} and there is no way to bring it up to {CurrentVersion}. "
                    + "It has been left alone rather than opened as something it is not.";
                return false;
            }

            wire = migrate(wire);
        }

        try
        {
            document = Rebuild(wire);
        }
        catch (Exception error) when (error is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            problem = $"This design could not be rebuilt: {error.Message}";
            return false;
        }

        return true;
    }

    /// <summary>
    /// How to bring a document up one version, keyed by the version it is coming
    /// from.
    ///
    /// Empty at version 1 because there is nothing older. The dictionary exists
    /// now so that adding version 2 is a matter of adding one entry and one test,
    /// rather than inventing the mechanism at the moment somebody's saved work
    /// depends on it being right.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, Func<DesignDocumentWire, DesignDocumentWire>> Migrations
        = new Dictionary<int, Func<DesignDocumentWire, DesignDocumentWire>>();

    private static DesignDocument Rebuild(DesignDocumentWire wire)
    {
        if (string.IsNullOrWhiteSpace(wire.RootId))
        {
            throw new InvalidOperationException("it has no page.");
        }

        var medium = Enum.TryParse<DesignMedium>(wire.Medium, ignoreCase: true, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"'{wire.Medium}' is not a kind of thing this build can make.");

        var nodes = ImmutableDictionary<string, DesignNode>.Empty.WithComparers(StringComparer.Ordinal);

        // The root is stored separately from the ordered children because it is
        // the one node that has no place in the order — it is what everything else
        // is inside.
        var root = FromWire(wire.Root);
        nodes = nodes.Add(root.Id, root);

        foreach (var node in wire.Nodes.Select(FromWire))
        {
            if (node.Id == root.Id)
            {
                continue;
            }

            nodes = nodes.SetItem(node.Id, node);
        }

        // Only ids that actually arrived. A saved order naming something that is
        // no longer in the document would otherwise throw on the first read of
        // ChildrenOf, long after the load looked like it worked.
        var order = wire.Order.Where(nodes.ContainsKey).ToImmutableList();

        return DesignDocument.Rehydrate(nodes, root.Id, wire.Look ?? DesignLooks.Default, medium, order);
    }

    private static DesignNodeWire ToWire(DesignNode node)
        => new(node.Id, node.Kind.ToString(), node.ParentId, node.Props.ToDictionary(p => p.Key, p => p.Value));

    private static DesignNode FromWire(DesignNodeWire wire)
    {
        if (string.IsNullOrWhiteSpace(wire.Id))
        {
            throw new InvalidOperationException("something on the page has no identity.");
        }

        var kind = Enum.TryParse<DesignNodeKind>(wire.Kind, ignoreCase: true, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"'{wire.Kind}' is not something this build knows how to draw.");

        return new DesignNode(
            wire.Id,
            kind,
            wire.ParentId,
            (wire.Props ?? new Dictionary<string, string>())
                .ToImmutableDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The document as it is written down.</summary>
    private sealed record DesignDocumentWire(
        int SchemaVersion,
        string Medium,
        string? Look,
        string RootId,
        IReadOnlyList<string> Order,
        IReadOnlyList<DesignNodeWire> Nodes,
        DesignNodeWire Root);

    /// <summary>One thing on the canvas, as it is written down.</summary>
    private sealed record DesignNodeWire(
        string Id,
        string Kind,
        string? ParentId,
        IReadOnlyDictionary<string, string>? Props);
}
