using System.Collections.Immutable;

namespace Concierge.Shared.Design;

/// <summary>
/// The kinds of thing that can be on a canvas.
///
/// Six, and the list is short on purpose. Every one of them is a thing a person
/// would name unprompted — a heading, some words, a picture — rather than a term
/// from a design tool. There is no "container", no "flex row", no "component
/// instance". A five-year-old asked what is on a page says: a title, some words, a
/// picture, a button. That is this list.
/// </summary>
public enum DesignNodeKind
{
    /// <summary>The page itself. Exactly one, and it is the root.</summary>
    Page,

    /// <summary>Big words at the top of something.</summary>
    Heading,

    /// <summary>Ordinary words.</summary>
    Text,

    /// <summary>A picture.</summary>
    Image,

    /// <summary>Something you press.</summary>
    Button,

    /// <summary>A box that holds other things, so a group can be moved as one.</summary>
    Box,
}

/// <summary>
/// One thing on the canvas.
///
/// Flat, with a <see cref="ParentId"/>, rather than nested children. A nested tree
/// reads better on paper and is worse in every operation that matters here: moving
/// a node means splicing two lists, and undo means rebuilding the whole shape. A
/// flat dictionary keyed by id makes "change this one thing" a single assignment,
/// which is the operation this surface performs constantly.
///
/// Properties are strings rather than a typed bag. What goes in them comes from a
/// model or from a person's sentence, and neither produces a well-typed value; a
/// renderer that has to cope with "reddish" is being honest about its input.
/// </summary>
public sealed record DesignNode(
    string Id,
    DesignNodeKind Kind,
    string? ParentId,
    ImmutableDictionary<string, string> Props)
{
    public static DesignNode New(DesignNodeKind kind, string? parentId, params (string Key, string Value)[] props)
        => new(
            Guid.NewGuid().ToString("N")[..8],
            kind,
            parentId,
            props.ToImmutableDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase));

    /// <summary>What this says, if it says anything.</summary>
    public string Text => Props.TryGetValue("text", out var text) ? text : string.Empty;

    public DesignNode With(string key, string value)
        => this with { Props = Props.SetItem(key, value) };
}

/// <summary>
/// A design, and everything that has happened to it.
///
/// Immutable, and that is the whole trick. Undo is not an operation that reverses
/// things — it is picking an earlier value out of a list. There is no undo stack to
/// get out of step with the document, no operation that forgot to write its
/// inverse, and no state you saw that cannot be returned to, because every one of
/// them is still sitting there.
///
/// That matters more here than anywhere else in Concierge. Everywhere else, acting
/// asks first, because acting on your files is hard to reverse. On a canvas,
/// asking permission to try something is the thing that makes a tool unusable for
/// the people this is for — a five-year-old's entire method is try it and see. So
/// Design acts freely, and what makes that safe is that going back is free.
/// </summary>
public sealed record DesignDocument
{
    private DesignDocument(ImmutableDictionary<string, DesignNode> nodes, string rootId, string look)
    {
        Nodes = nodes;
        RootId = rootId;
        Look = look;
    }

    /// <summary>Everything on the canvas, by id.</summary>
    public ImmutableDictionary<string, DesignNode> Nodes { get; }

    /// <summary>The page.</summary>
    public string RootId { get; }

    /// <summary>
    /// Which look this wears, by name.
    ///
    /// A name, not a stylesheet and not a file. The look is chosen by pointing at a
    /// picture of it; a person is never shown the colours that make it up, because
    /// "#2196F3" is not a thing anybody asked for.
    /// </summary>
    public string Look { get; }

    /// <summary>An empty page, wearing the first look.</summary>
    public static DesignDocument Blank(string look = DesignLooks.Default)
    {
        var page = DesignNode.New(DesignNodeKind.Page, null);

        return new DesignDocument(
            ImmutableDictionary<string, DesignNode>.Empty
                .Add(page.Id, page).WithComparers(StringComparer.Ordinal),
            page.Id,
            look);
    }

    /// <summary>Nothing on it but the page.</summary>
    public bool IsEmpty => Nodes.Count <= 1;

    public DesignNode? Find(string? id)
        => id is not null && Nodes.TryGetValue(id, out var node) ? node : null;

    /// <summary>
    /// What sits directly inside something, in the order it was added.
    ///
    /// Insertion order rather than an explicit index: a person adding a heading and
    /// then a paragraph expects them in that order, and an ordering field is one
    /// more thing that can disagree with what is on screen.
    /// </summary>
    public IReadOnlyList<DesignNode> ChildrenOf(string parentId)
        => _order.Where(id => Nodes.TryGetValue(id, out var n) && n.ParentId == parentId)
                 .Select(id => Nodes[id])
                 .ToList();

    private ImmutableList<string> _order = ImmutableList<string>.Empty;

    /// <summary>Adds something to the page, or inside a box.</summary>
    public DesignDocument Add(DesignNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var parent = node.ParentId ?? RootId;

        // Somewhere that is not there any more, because a model named a node it
        // remembered from three edits ago. The page is a safe home; refusing the
        // whole edit would lose whatever it was trying to make.
        if (!Nodes.ContainsKey(parent))
        {
            parent = RootId;
        }

        var placed = node with { ParentId = parent };

        return new DesignDocument(Nodes.SetItem(placed.Id, placed), RootId, Look)
        {
            _order = _order.Add(placed.Id),
        };
    }

    /// <summary>Changes one property of one thing, leaving everything else alone.</summary>
    public DesignDocument Set(string id, string key, string value)
    {
        if (Find(id) is not { } node)
        {
            return this;
        }

        return new DesignDocument(Nodes.SetItem(id, node.With(key, value)), RootId, Look)
        {
            _order = _order,
        };
    }

    /// <summary>
    /// Removes something, and everything inside it.
    ///
    /// The page itself cannot be removed. A canvas with no page is not an empty
    /// canvas, it is a broken one, and the person who said "delete that" meant the
    /// thing they were pointing at.
    /// </summary>
    public DesignDocument Remove(string id)
    {
        if (id == RootId || Find(id) is null)
        {
            return this;
        }

        var doomed = new HashSet<string>(StringComparer.Ordinal) { id };
        var changed = true;

        while (changed)
        {
            changed = false;

            foreach (var node in Nodes.Values)
            {
                if (node.ParentId is not null && doomed.Contains(node.ParentId) && doomed.Add(node.Id))
                {
                    changed = true;
                }
            }
        }

        return new DesignDocument(Nodes.RemoveRange(doomed), RootId, Look)
        {
            _order = _order.RemoveAll(doomed.Contains),
        };
    }

    /// <summary>Wears a different look. Nothing on the canvas moves.</summary>
    public DesignDocument Wearing(string look)
        => new(Nodes, RootId, DesignLooks.Resolve(look)) { _order = _order };
}
