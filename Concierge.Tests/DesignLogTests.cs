using System.Text.Json.Nodes;
using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// A short memory of what has been made, so the next one differs.
///
/// The failure this prevents is specific: a model driving a canvas picks the same
/// look every time — not because it is right, but because it is first in the list
/// and the safest-sounding word. Six looks with one of them used forever is the
/// same product as one look, and nobody notices until every page anybody made
/// looks identical.
///
/// Taken from hallmark, which logs macrostructure, theme and enrichment on every
/// build and requires the next to differ. It is the only rule in that corpus aimed
/// at the model's *tendency* rather than its output, which is why it carries over
/// when almost nothing else about that skill does.
/// </summary>
public sealed class DesignLogTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "concierge-designlog-tests", Guid.NewGuid().ToString("N"));

    private FileDesignLog Log() => new(Path.Combine(_root, "designs-made.json"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Swept with the test-run temp root regardless.
        }
    }

    [Fact]
    public async Task With_nothing_made_yet_there_is_nothing_to_avoid()
        => Assert.Empty(await Log().RecentAsync());

    [Fact]
    public async Task What_was_made_comes_back_newest_first()
    {
        var log = Log();
        await log.RecordAsync("Calm", DesignMedium.Page);
        await log.RecordAsync("Bold", DesignMedium.Deck);

        var recent = await log.RecentAsync();

        Assert.Equal(["Bold", "Calm"], recent.Select(made => made.Look));
        Assert.Equal("Deck", recent[0].Making);
    }

    /// <summary>
    /// Short on purpose. A long history lets a model reason about trends nobody
    /// asked it to notice; this answers one question — what did the last few look
    /// like?
    /// </summary>
    [Fact]
    public async Task Only_the_last_few_are_kept()
    {
        var log = Log();

        foreach (var look in new[] { "Calm", "Bold", "Warm", "Night", "Plain", "Fresh", "Calm" })
        {
            await log.RecordAsync(look, DesignMedium.Page);
        }

        Assert.Equal(FileDesignLog.Keep, (await log.RecentAsync(50)).Count);
    }

    [Fact]
    public async Task Asking_for_fewer_gives_fewer()
    {
        var log = Log();
        await log.RecordAsync("Calm", DesignMedium.Page);
        await log.RecordAsync("Bold", DesignMedium.Page);
        await log.RecordAsync("Warm", DesignMedium.Page);

        Assert.Equal(2, (await log.RecentAsync(2)).Count);
    }

    [Fact]
    public async Task A_nameless_look_is_not_recorded()
    {
        var log = Log();
        await log.RecordAsync("   ", DesignMedium.Page);

        Assert.Empty(await log.RecentAsync());
    }

    /// <summary>
    /// A memory aid must never be able to break the thing it is helping with.
    /// </summary>
    [Fact]
    public async Task An_unreadable_log_is_an_empty_one_rather_than_a_failure()
    {
        var log = Log();
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(log.Path, "{ this is not the log }");

        Assert.Empty(await log.RecentAsync());

        // And it recovers: the next record replaces the rubbish.
        await log.RecordAsync("Fresh", DesignMedium.Sound);
        Assert.Single(await log.RecentAsync());
    }

    // ── What the model is actually told ───────────────────────────────────

    [Fact]
    public async Task The_canvas_tells_the_model_what_the_last_few_wore()
    {
        var bench = new DesignWorkbench();
        bench.Attach(new DesignSession());

        var log = Log();
        await log.RecordAsync("Bold", DesignMedium.Deck);
        bench.Log = log;

        var tools = new DesignToolSource(bench);
        var described = (await tools.Tools.Single(t => t.Name == "design_describe")
            .InvokeAsync(null)).Output;

        Assert.Contains("The last few designs wore", described, StringComparison.Ordinal);
        Assert.Contains("Bold", described, StringComparison.Ordinal);
        Assert.Contains("pick a different look", described, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_history_nothing_is_said_about_variety()
    {
        var bench = new DesignWorkbench();
        bench.Attach(new DesignSession());
        bench.Log = Log();

        var described = (await new DesignToolSource(bench).Tools
            .Single(t => t.Name == "design_describe").InvokeAsync(null)).Output;

        Assert.DoesNotContain("The last few designs wore", described, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Choosing_a_look_records_it()
    {
        var bench = new DesignWorkbench();
        bench.Attach(new DesignSession());
        var log = Log();
        bench.Log = log;

        await new DesignToolSource(bench).Tools.Single(t => t.Name == "design_look")
            .InvokeAsync(new JsonObject { ["look"] = "Night" });

        Assert.Equal("Night", (await log.RecentAsync())[0].Look);
    }

    /// <summary>
    /// A head without a log keeps its canvas. The tools simply say nothing about
    /// variety rather than failing on a missing optional service.
    /// </summary>
    [Fact]
    public async Task A_canvas_without_a_log_still_works()
    {
        var bench = new DesignWorkbench();
        bench.Attach(new DesignSession());

        var tools = new DesignToolSource(bench);

        Assert.True((await tools.Tools.Single(t => t.Name == "design_describe").InvokeAsync(null)).Success);
        Assert.True((await tools.Tools.Single(t => t.Name == "design_look")
            .InvokeAsync(new JsonObject { ["look"] = "Warm" })).Success);
    }
}
