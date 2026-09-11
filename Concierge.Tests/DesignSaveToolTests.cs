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

    [Fact]
    public async Task A_page_is_not_saved_as_a_file_and_says_why()
    {
        var bench = Open(new Refusing());
        bench.Session!.Record(bench.Session.Current.As(DesignMedium.Page), "A page");

        var result = await Tools(bench).Run("design_save");

        Assert.False(result.Success);
        Assert.Contains("printed", result.Output, StringComparison.OrdinalIgnoreCase);
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
