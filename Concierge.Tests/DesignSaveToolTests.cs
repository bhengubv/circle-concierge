using System.Text.Json.Nodes;
using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// Saving the design as a file, from the same place a model reaches everything
/// else on the canvas.
///
/// The tool is deliberately absent when this machine has no encoder, so the two
/// halves are tested separately: what the catalogue offers, and what the tool
/// does once it is there.
/// </summary>
public sealed class DesignSaveToolTests
{
    private static DesignWorkbench Open(IMediaExport? encoder = null)
    {
        var bench = new DesignWorkbench();
        bench.Attach(new DesignSession());
        bench.Export = encoder;
        return bench;
    }

    private static IAgentToolAccessor Tools(DesignWorkbench bench) => new(new DesignToolSource(bench));

    /// <summary>Small helper so each test reads as one sentence.</summary>
    internal sealed class IAgentToolAccessor(DesignToolSource source)
    {
        public bool Has(string name) => source.Tools.Any(tool => tool.Name == name);

        public Task<Concierge.Shared.Tools.AgentToolResult> Run(string name, JsonNode? arguments = null)
            => source.Tools.Single(tool => tool.Name == name).InvokeAsync(arguments);
    }

    // ── Whether it is offered at all ──────────────────────────────────────

    [Fact]
    public void With_no_encoder_there_is_nothing_to_save_with_and_the_tool_is_absent()
        => Assert.False(Tools(Open()).Has("design_save"));

    [Fact]
    public void With_an_encoder_it_is_offered()
        => Assert.True(Tools(Open(new Refusing())).Has("design_save"));

    [Fact]
    public void With_no_canvas_open_it_is_absent_like_every_other_design_tool()
    {
        var bench = new DesignWorkbench { Export = new Refusing() };
        Assert.False(Tools(bench).Has("design_save"));
    }

    // ── What it does ──────────────────────────────────────────────────────

    /// <summary>
    /// A page, a deck and a room save as one HTML file.
    ///
    /// This test used to assert the opposite — that a page "is printed from the
    /// page itself" — which meant three of the five media could not be handed to
    /// anybody at all, and the test held that shut. Cloning the six made the cost
    /// obvious: open-design's entire pitch is "real files, HTML/PDF/PPTX/MP4
    /// export", and Concierge could produce a file for two media out of five.
    ///
    /// It writes into Documents, so these tests assert on what comes back rather
    /// than reading the disk — the path is real and the file is really written,
    /// which the tool's own message reports.
    /// </summary>
    [Theory]
    [InlineData(DesignMedium.Page)]
    [InlineData(DesignMedium.Deck)]
    [InlineData(DesignMedium.Scene)]
    public async Task A_page_a_deck_and_a_room_save_as_one_html_file(DesignMedium medium)
    {
        var bench = Open(new Refusing());
        bench.Session!.Record(bench.Session.Current.As(medium), "Something");

        var result = await Tools(bench).Run("design_save", new JsonObject { ["name"] = $"handout-{medium}" });

        Assert.True(result.Success, result.Output);
        Assert.Contains(".html", result.Output, StringComparison.Ordinal);

        var path = result.Output.Split(' ').First(word => word.Contains(".html", StringComparison.Ordinal)).TrimEnd('.');
        Assert.True(File.Exists(path), path);

        try
        {
            var written = await File.ReadAllTextAsync(path);

            // The same bytes the canvas shows, not an export of them.
            Assert.StartsWith("<!doctype html>", written, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// It does not need an encoder to write one. A machine with no ffmpeg can
    /// still hand somebody a page — which is most of what a person makes.
    /// </summary>
    [Fact]
    public async Task A_page_saves_without_an_encoder()
    {
        var bench = Open(new Refusing());
        bench.Session!.Record(bench.Session.Current.As(DesignMedium.Page), "A page");

        var result = await Tools(bench).Run("design_save", new JsonObject { ["name"] = "no-encoder-needed" });

        Assert.True(result.Success, result.Output);

        var path = result.Output.Split(' ').First(word => word.Contains(".html", StringComparison.Ordinal)).TrimEnd('.');
        File.Delete(path);
    }

    [Fact]
    public async Task A_sound_goes_to_the_encoder_as_a_sound()
    {
        var encoder = new Watching();
        var bench = Open(encoder);
        bench.Session!.Record(bench.Session.Current.As(DesignMedium.Sound), "A sound");

        await Tools(bench).Run("design_save");

        Assert.Equal("sound", encoder.Asked);
        Assert.EndsWith(".m4a", encoder.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_motion_piece_goes_to_the_encoder_as_a_video()
    {
        var encoder = new Watching();
        var bench = Open(encoder);
        bench.Session!.Record(bench.Session.Current.As(DesignMedium.Motion), "A film");

        await Tools(bench).Run("design_save");

        Assert.Equal("video", encoder.Asked);
        Assert.EndsWith(".mp4", encoder.Path, StringComparison.Ordinal);
    }

    /// <summary>
    /// A model asked for a name hands back a title, not a filename. Slashes and
    /// colons in it must not decide where the file goes.
    /// </summary>
    [Fact]
    public async Task A_name_from_a_sentence_cannot_choose_the_folder()
    {
        var encoder = new Watching();
        var bench = Open(encoder);
        bench.Session!.Record(bench.Session.Current.As(DesignMedium.Motion), "A film");

        await Tools(bench).Run("design_save", new JsonObject { ["name"] = @"..\..\Act 1: the fall" });

        var name = Path.GetFileNameWithoutExtension(encoder.Path);
        Assert.DoesNotContain("..", name, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, name);
        Assert.Contains("the fall", name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_name_given_it_still_has_one()
    {
        var encoder = new Watching();
        var bench = Open(encoder);
        bench.Session!.Record(bench.Session.Current.As(DesignMedium.Motion), "A film");

        await Tools(bench).Run("design_save");

        Assert.False(string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(encoder.Path)));
    }

    [Fact]
    public async Task A_refusal_is_reported_rather_than_thrown()
    {
        var bench = Open(new Refusing());
        bench.Session!.Record(bench.Session.Current.As(DesignMedium.Sound), "A sound");

        var result = await Tools(bench).Run("design_save");

        Assert.False(result.Success);
        Assert.Contains("no", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task What_it_saved_is_said_back_so_somebody_can_find_it()
    {
        var bench = Open(new Watching());
        bench.Session!.Record(bench.Session.Current.As(DesignMedium.Sound), "A sound");

        var result = await Tools(bench).Run("design_save", new JsonObject { ["name"] = "Birdsong" });

        Assert.True(result.Success);
        Assert.Contains("Birdsong", result.Output, StringComparison.Ordinal);
    }

    // ── Stand-ins ─────────────────────────────────────────────────────────

    private sealed class Watching : IMediaExport
    {
        public string? Asked { get; private set; }

        public string Path { get; private set; } = string.Empty;

        public Task<ExportResult> SoundAsync(
            DesignDocument document, string outputPath, CancellationToken cancellationToken = default)
        {
            Asked = "sound";
            Path = outputPath;
            return Task.FromResult(ExportResult.Made(outputPath));
        }

        public Task<ExportResult> VideoAsync(
            DesignDocument document, string outputPath, CancellationToken cancellationToken = default)
        {
            Asked = "video";
            Path = outputPath;
            return Task.FromResult(ExportResult.Made(outputPath));
        }
    }

    private sealed class Refusing : IMediaExport
    {
        public Task<ExportResult> SoundAsync(
            DesignDocument document, string outputPath, CancellationToken cancellationToken = default)
            => Task.FromResult(ExportResult.Failed("There is nothing to save yet."));

        public Task<ExportResult> VideoAsync(
            DesignDocument document, string outputPath, CancellationToken cancellationToken = default)
            => Task.FromResult(ExportResult.Failed("There is nothing to save yet."));
    }
}
