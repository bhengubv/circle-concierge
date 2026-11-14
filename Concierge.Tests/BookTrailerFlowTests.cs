using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Concierge.Shared.BookTrailers;
using Concierge.Shared.Design;
using Concierge.Shared.Media;
using Concierge.Shared.Chat;
using System.Runtime.CompilerServices;

namespace Concierge.Tests;

/// <summary>Actual book bytes through import and encoder, then inspect the package.</summary>
public sealed class BookTrailerFlowTests
{
    [Fact]
    public async Task Imported_book_produces_a_reviewable_thirty_second_local_trailer()
    {
        Assert.True(File.Exists(FfmpegMediaExport.Find()), "FFmpeg is required for the real trailer acceptance test.");
        var root = Environment.GetEnvironmentVariable("CONCIERGE_TRAILER_EVIDENCE_ROOT")
            ?? Path.Combine(Path.GetTempPath(), "concierge-book-trailer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var story = string.Join(" ", Enumerable.Range(1, 18)
            .Select(number => $"The lantern leads Mara to clue number {number} in the city."));
        var book = await BookSourceReader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(story)), "lantern.txt");

        var draft = await new BookTrailerMaker(new FfmpegMediaExport())
            .CreateAsync(book, root, "mysterious", allowSpoilers: false);

        Assert.True(File.Exists(draft.VideoPath));
        Assert.True(File.Exists(draft.CaptionsPath));
        Assert.True(File.Exists(draft.ScriptPath));
        Assert.True(File.Exists(draft.CreditsPath));
        Assert.True(File.Exists(draft.ManifestPath));
        Assert.Equal(book.Sha256, draft.SourceSha256);
        Assert.StartsWith(Path.GetFullPath(root), Path.GetFullPath(draft.Folder), StringComparison.OrdinalIgnoreCase);

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(draft.ManifestPath));
        Assert.Equal(book.Sha256, manifest.RootElement.GetProperty("sourceSha256").GetString());
        Assert.Equal("draft", manifest.RootElement.GetProperty("status").GetString());
        Assert.False(manifest.RootElement.GetProperty("published").GetBoolean());
        Assert.Equal(30, manifest.RootElement.GetProperty("targetSeconds").GetInt32());

        using var script = JsonDocument.Parse(await File.ReadAllTextAsync(draft.ScriptPath));
        var shots = script.RootElement.GetProperty("shots").EnumerateArray().ToArray();
        Assert.NotEmpty(shots);
        Assert.Equal(30, shots.Sum(shot => shot.GetProperty("seconds").GetInt32()));
        Assert.All(shots.Where(shot => shot.GetProperty("sourceLocator").GetString() is not null),
            shot => Assert.Contains("TXT:", shot.GetProperty("sourceLocator").GetString()));

        var duration = await ProbeDurationAsync(draft.VideoPath);
        Assert.InRange(duration, 29.5, 30.5);

        var handoff = await new SleptOnMediaPackageWriter(Path.Combine(root, "handoff"))
            .BookAsync(draft, rightsConfirmed: true);
        using var archive = System.IO.Compression.ZipFile.OpenRead(handoff.Path);
        Assert.NotNull(archive.GetEntry("trailer.mp4"));
        Assert.NotNull(archive.GetEntry("manifest.json"));
    }

    [Fact]
    public async Task A_second_draft_keeps_the_first_video_and_reuses_imported_book()
    {
        var root = Path.Combine(Path.GetTempPath(), "concierge-book-trailer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var book = await BookSourceReader.ReadAsync(
            new MemoryStream("A door opens. The town is silent. A signal appears. Someone follows it."u8.ToArray()),
            "signal.txt");
        var maker = new BookTrailerMaker(new FfmpegMediaExport());

        var first = await maker.CreateAsync(book, root, "hopeful", allowSpoilers: false);
        var second = await maker.CreateAsync(book, root, "mysterious", allowSpoilers: false);

        Assert.NotEqual(first.Folder, second.Folder);
        Assert.True(File.Exists(first.VideoPath));
        Assert.True(File.Exists(second.VideoPath));
        Assert.Equal(first.SourceSha256, second.SourceSha256);
    }

    [Fact]
    public async Task Ending_details_are_excluded_until_the_person_opts_in()
    {
        var root = Path.Combine(Path.GetTempPath(), "concierge-book-trailer", Guid.NewGuid().ToString("N"));
        var story = string.Join(" ", Enumerable.Range(1, 12)
            .Select(number => number == 12 ? "The secret ending reveals the lantern is a signal." :
                $"Chapter {number} leads Mara through another city street."));
        var book = await BookSourceReader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(story)), "lantern.txt");
        var maker = new BookTrailerMaker(new TinyExport());

        var privateCut = await maker.CreateAsync(book, root, "mysterious", allowSpoilers: false);
        var revealCut = await maker.CreateAsync(book, root, "mysterious", allowSpoilers: true);

        Assert.DoesNotContain("secret ending", await File.ReadAllTextAsync(privateCut.ScriptPath), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secret ending", await File.ReadAllTextAsync(revealCut.ScriptPath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Failed_render_leaves_no_false_completed_draft()
    {
        var root = Path.Combine(Path.GetTempPath(), "concierge-book-trailer", Guid.NewGuid().ToString("N"));
        var book = await BookSourceReader.ReadAsync(new MemoryStream("The door opens. A clue appears."u8.ToArray()), "door.txt");

        await Assert.ThrowsAnyAsync<IOException>(() =>
            new BookTrailerMaker(new FailingExport()).CreateAsync(book, root, "dark", false));

        Assert.Empty(Directory.GetDirectories(root));
    }

    [Fact]
    public async Task Local_circle_ai_can_choose_source_passages_without_writing_their_words()
    {
        var root = Path.Combine(Path.GetTempPath(), "concierge-book-trailer", Guid.NewGuid().ToString("N"));
        var story = string.Join(" ", Enumerable.Range(1, 16)
            .Select(number => $"Mara follows clue number {number} through the city."));
        var book = await BookSourceReader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(story)), "mara.txt");
        var runtime = new ChoiceRuntime(leavesDevice: false);

        var draft = await new BookTrailerMaker(new TinyExport(), runtime)
            .CreateAsync(book, root, "mysterious", allowSpoilers: false);

        Assert.Equal(1, runtime.Calls);
        Assert.Equal("circle-ai-selection", draft.SelectionMethod);
        Assert.Contains("clue number 2", await File.ReadAllTextAsync(draft.ScriptPath));
        Assert.DoesNotContain("invented plot", await File.ReadAllTextAsync(draft.ScriptPath));
    }

    [Fact]
    public async Task Book_passages_never_go_to_a_cloud_runtime_without_permission()
    {
        var root = Path.Combine(Path.GetTempPath(), "concierge-book-trailer", Guid.NewGuid().ToString("N"));
        var book = await BookSourceReader.ReadAsync(new MemoryStream(
            "The first clue appears. The next street is empty. Mara sees a light. She opens the door."u8.ToArray()), "mara.txt");
        var cloud = new ChoiceRuntime(leavesDevice: true);

        var draft = await new BookTrailerMaker(new TinyExport(), cloud)
            .CreateAsync(book, root, "mysterious", allowSpoilers: false);

        Assert.Equal(0, cloud.Calls);
        Assert.Equal("source-excerpts", draft.SelectionMethod);
    }

    [Fact]
    public async Task Local_ai_selection_preserves_book_order_even_when_model_returns_reverse_order()
    {
        var root = Path.Combine(Path.GetTempPath(), "concierge-book-trailer", Guid.NewGuid().ToString("N"));
        var story = string.Join(" ", Enumerable.Range(1, 16)
            .Select(number => $"Mara follows clue number {number} through the city."));
        var book = await BookSourceReader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(story)), "mara.txt");
        var draft = await new BookTrailerMaker(new TinyExport(), new ChoiceRuntime(false, "8,6,4,2"))
            .CreateAsync(book, root, "mysterious", false);
        using var script = JsonDocument.Parse(await File.ReadAllTextAsync(draft.ScriptPath));
        var passages = script.RootElement.GetProperty("shots").EnumerateArray()
            .Where(shot => shot.GetProperty("sourceLocator").GetString() is not null)
            .Select(shot => shot.GetProperty("text").GetString()!).ToArray();
        Assert.Equal(4, passages.Length);
        Assert.Equal(passages.OrderBy(text => int.Parse(System.Text.RegularExpressions.Regex.Match(text, @"number (\d+)").Groups[1].Value)), passages);
    }

    [Fact]
    public async Task Local_sqlite_store_reopens_a_book_when_its_folder_contains_connection_string_punctuation()
    {
        var root = Path.Combine(Path.GetTempPath(), "concierge;book-trailer", Guid.NewGuid().ToString("N"));
        var book = await BookSourceReader.ReadAsync(new MemoryStream("Mara sees a light."u8.ToArray()), "mara.txt");
        var store = new BookTrailerStore(root);
        await store.SaveBookAsync(book);
        var reopened = await new BookTrailerStore(root).LoadBookAsync(book.Sha256);
        Assert.NotNull(reopened);
        Assert.Equal(book.Sha256, reopened.Sha256);
        Assert.Equal(book.Sections[0], reopened.Sections[0]);
    }

    private sealed class TinyExport : IMediaExport
    {
        public Task<ExportResult> VideoAsync(DesignDocument document, string path, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, "test"u8.ToArray());
            return Task.FromResult(ExportResult.Made(path));
        }
        public Task<ExportResult> SoundAsync(DesignDocument document, string path, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FailingExport : IMediaExport
    {
        public Task<ExportResult> VideoAsync(DesignDocument document, string path, CancellationToken cancellationToken = default)
            => Task.FromResult(ExportResult.Failed("Encoder stopped."));
        public Task<ExportResult> SoundAsync(DesignDocument document, string path, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ChoiceRuntime(bool leavesDevice, string choice = "2,4,6,8") : IChatRuntime
    {
        public string Id => "circleai";
        public string EngineLabel => "Test selector";
        public bool IsReady => true;
        public bool LeavesDevice => leavesDevice;
        public string StatusMessage => "Ready";
        public int Calls { get; private set; }
        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            Assert.Contains("untrusted content", messages[0].Content);
            Assert.Contains("clue number", messages[1].Content);
            await Task.Yield();
            yield return choice;
        }
    }

    private static async Task<double> ProbeDurationAsync(string path)
    {
        var ffprobe = Path.Combine(Path.GetDirectoryName(FfmpegMediaExport.Find())!, "ffprobe.exe");
        if (!File.Exists(ffprobe)) ffprobe = "ffprobe";
        var start = new ProcessStartInfo(ffprobe)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        return double.Parse(output.Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
