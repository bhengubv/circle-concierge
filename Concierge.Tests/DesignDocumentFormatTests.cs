using System.Text.Json;
using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// Writing a design down and reading it back.
///
/// Built before anything is saved, which is the only cheap moment. Pascal carries
/// seven migration test files; we had none, because nothing was persisted yet —
/// which is a gap rather than a defence. The day a design is saved without a
/// version on it, every later change to the shape becomes a choice between
/// breaking somebody's work and carrying the old shape forever.
///
/// The tests worth having here are the ones about what a careless loader does
/// quietly: loses child order, opens a newer file as if it were older, or hands
/// back an empty page that then gets saved over the real one.
/// </summary>
public sealed class DesignDocumentFormatTests
{
    private static DesignDocument Sample()
    {
        var document = DesignDocument.Blank(medium: DesignMedium.Deck);
        var first = DesignNode.New(DesignNodeKind.Frame, null, ("name", "One"));
        var second = DesignNode.New(DesignNodeKind.Frame, null, ("name", "Two"));

        document = document.Add(first).Add(second);
        document = document.Add(DesignNode.New(DesignNodeKind.Heading, first.Id, ("text", "Hello")));
        document = document.Add(DesignNode.New(DesignNodeKind.Text, first.Id, ("text", "Some words")));

        return document.Wearing(DesignLooks.Default);
    }

    [Fact]
    public void A_design_survives_being_written_down_and_read_back()
    {
        var original = Sample();

        Assert.True(DesignDocumentFormat.TryDeserialize(
            DesignDocumentFormat.Serialize(original), out var reopened, out var problem));
        Assert.Null(problem);
        Assert.NotNull(reopened);

        Assert.Equal(original.Medium, reopened!.Medium);
        Assert.Equal(original.Look, reopened.Look);
        Assert.Equal(original.RootId, reopened.RootId);
        Assert.Equal(original.Nodes.Count, reopened.Nodes.Count);
    }

    /// <summary>
    /// The one a naive serialiser loses.
    ///
    /// Children are held in insertion order in a list private to the document,
    /// because a person who adds a heading and then a paragraph expects them in
    /// that order. Writing only the node dictionary would drop it, and a reopened
    /// deck would have its slides shuffled — noticed for the first time by
    /// somebody standing in front of an audience.
    /// </summary>
    [Fact]
    public void The_order_things_were_added_in_survives()
    {
        var original = Sample();
        var expected = original.Frames.Select(f => f.Props["name"]).ToList();
        Assert.Equal(["One", "Two"], expected);

        Assert.True(DesignDocumentFormat.TryDeserialize(
            DesignDocumentFormat.Serialize(original), out var reopened, out _));

        Assert.Equal(expected, reopened!.Frames.Select(f => f.Props["name"]).ToList());
    }

    [Fact]
    public void What_is_inside_something_survives_with_its_order()
    {
        var original = Sample();
        var frame = original.Frames[0];
        var expected = original.ChildrenOf(frame.Id).Select(c => c.Text).ToList();
        Assert.Equal(["Hello", "Some words"], expected);

        Assert.True(DesignDocumentFormat.TryDeserialize(
            DesignDocumentFormat.Serialize(original), out var reopened, out _));

        Assert.Equal(expected, reopened!.ChildrenOf(frame.Id).Select(c => c.Text).ToList());
    }

    /// <summary>
    /// A version is written even though there is only one, because a file that
    /// does not say what it is can only be identified by sniffing its contents.
    /// </summary>
    [Fact]
    public void Every_saved_design_says_which_version_it_is()
    {
        using var parsed = JsonDocument.Parse(DesignDocumentFormat.Serialize(Sample()));

        Assert.Equal(
            DesignDocumentFormat.CurrentVersion,
            parsed.RootElement.GetProperty("SchemaVersion").GetInt32());
    }

    /// <summary>
    /// A newer file is refused rather than opened.
    ///
    /// It may hold a node kind, a medium or a property this build has never heard
    /// of. Opening it by ignoring what we do not understand gives somebody a
    /// design that silently lost half of itself — which they then save back over
    /// the good copy.
    /// </summary>
    [Fact]
    public void A_design_from_a_newer_Concierge_is_refused_and_says_so()
    {
        var written = DesignDocumentFormat.Serialize(Sample())
            .Replace(
                $"\"SchemaVersion\": {DesignDocumentFormat.CurrentVersion}",
                $"\"SchemaVersion\": {DesignDocumentFormat.CurrentVersion + 1}",
                StringComparison.Ordinal);

        Assert.False(DesignDocumentFormat.TryDeserialize(written, out var document, out var problem));
        Assert.Null(document);
        Assert.Contains("newer", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_design_with_no_version_is_refused()
    {
        var written = DesignDocumentFormat.Serialize(Sample())
            .Replace(
                $"\"SchemaVersion\": {DesignDocumentFormat.CurrentVersion}",
                "\"SchemaVersion\": 0",
                StringComparison.Ordinal);

        Assert.False(DesignDocumentFormat.TryDeserialize(written, out _, out var problem));
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"SchemaVersion\":1}")]
    public void Anything_that_is_not_a_design_is_refused_rather_than_thrown(string input)
    {
        var opened = DesignDocumentFormat.TryDeserialize(input, out var document, out var problem);

        Assert.False(opened);
        Assert.Null(document);
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    /// <summary>
    /// A saved order naming something no longer present must not take the whole
    /// document down. Throwing here would happen on the first read of
    /// ChildrenOf — long after the load appeared to succeed.
    /// </summary>
    [Fact]
    public void An_order_that_names_something_missing_does_not_break_the_load()
    {
        var written = DesignDocumentFormat.Serialize(Sample())
            .Replace("\"Order\": [", "\"Order\": [\n    \"ghostnode\",", StringComparison.Ordinal);

        Assert.True(DesignDocumentFormat.TryDeserialize(written, out var reopened, out var problem));
        Assert.Null(problem);
        Assert.Equal(2, reopened!.Frames.Count);
    }

    [Fact]
    public void Something_this_build_cannot_draw_is_refused()
    {
        var written = DesignDocumentFormat.Serialize(Sample())
            .Replace("\"Kind\": \"Heading\"", "\"Kind\": \"Hologram\"", StringComparison.Ordinal);

        Assert.False(DesignDocumentFormat.TryDeserialize(written, out _, out var problem));
        Assert.Contains("Hologram", problem, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Changing what you are making throws nothing away, and that has to survive
    /// being saved — it is the point of separating what a thing is from how it is
    /// drawn.
    /// </summary>
    [Fact]
    public void Changing_the_medium_survives_being_saved()
    {
        var asMotion = Sample().As(DesignMedium.Motion);

        Assert.True(DesignDocumentFormat.TryDeserialize(
            DesignDocumentFormat.Serialize(asMotion), out var reopened, out _));

        Assert.Equal(DesignMedium.Motion, reopened!.Medium);
        Assert.Equal(2, reopened.Frames.Count);
    }
}
