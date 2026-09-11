using System.Text.Json.Nodes;
using Concierge.Shared.Design;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

/// <summary>
/// A folder of music, read rather than downloaded.
///
/// **This is the part of Antra that needs no account.** Antra's headline is seven streaming
/// services and every one of them needs credentials nobody here has. What it does either side
/// of the download does not: filing tracks into artist and album folders, and noticing the
/// same recording is already there twice. Two of its twenty-odd features, working on files
/// that are already on the machine — said plainly rather than counted as parity.
///
/// The tags come from the encoder, so the reading half is tested against real files the
/// encoder made. The deciding half — what is a duplicate, where a track belongs — is tested
/// on its own, because that is where the answers can be wrong in ways nobody notices until
/// their music is somewhere else.
/// </summary>
public sealed class MusicLibraryTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "concierge-music", Guid.NewGuid().ToString("n"));

    private static bool EncoderHere => MediaLook.Possible;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static LibraryTrack Track(
        string path, string artist, string title, string album = "An Album", long bytes = 1000)
        => new(path, new MediaTags(true, title, artist, album, string.Empty, 0), bytes);

    // ── What is the same recording ────────────────────────────────────────

    /// <summary>
    /// The same track downloaded twice is "05 Wild Is The Wind.mp3" and "Nina Simone - Wild
    /// Is The Wind.m4a", and nothing about those two strings says they are the same thing.
    /// </summary>
    [Fact]
    public void The_same_recording_is_found_whatever_the_files_are_called()
    {
        var same = MusicLibrary.Duplicates([
            Track(@"C:\m\05 wild.mp3", "Nina Simone", "Wild Is The Wind"),
            Track(@"C:\m\nina-simone-wild.m4a", "nina simone", "wild is the wind!"),
        ]);

        Assert.Single(same);
        Assert.Equal(2, same[0].Copies.Count);
    }

    [Fact]
    public void Two_different_tracks_are_not_the_same_recording()
        => Assert.Empty(MusicLibrary.Duplicates([
            Track(@"C:\m\one.mp3", "Nina Simone", "Wild Is The Wind"),
            Track(@"C:\m\two.mp3", "Nina Simone", "Sinnerman"),
        ]));

    /// <summary>
    /// Two untagged files would otherwise match each other, and the answer would be
    /// "everything untagged in your library is the same song" — which is both wrong and the
    /// kind of wrong somebody acts on.
    /// </summary>
    [Fact]
    public void A_track_with_nothing_on_it_is_never_called_a_duplicate()
        => Assert.Empty(MusicLibrary.Duplicates([
            new(@"C:\m\one.mp3", new MediaTags(false, "", "", "", "", 0), 1000),
            new(@"C:\m\two.mp3", new MediaTags(false, "", "", "", "", 0), 1000),
        ]));

    /// <summary>
    /// The larger file is usually the better copy, and whoever is deciding what to keep wants
    /// it named first. A hint, not a judgement — nothing removes anything.
    /// </summary>
    [Fact]
    public void The_biggest_copy_is_named_first()
    {
        var same = MusicLibrary.Duplicates([
            Track(@"C:\m\small.mp3", "Nina Simone", "Sinnerman", bytes: 3_000_000),
            Track(@"C:\m\big.flac", "Nina Simone", "Sinnerman", bytes: 30_000_000),
        ]);

        Assert.Equal(@"C:\m\big.flac", same[0].Copies[0].Path);
    }

    [Fact]
    public void A_leading_the_does_not_make_it_a_different_band()
        => Assert.Equal(MusicLibrary.Plain("The Beatles"), MusicLibrary.Plain("Beatles"));

    // ── Where a track belongs ─────────────────────────────────────────────

    [Fact]
    public void A_track_goes_under_its_artist_and_its_album()
    {
        var filings = MusicLibrary.Filings(
            [Track(@"C:\downloads\05.mp3", "Nina Simone", "Sinnerman", "Pastel Blues")], @"D:\Music");

        Assert.Equal(
            Path.Combine(@"D:\Music", "Nina Simone", "Pastel Blues", "Sinnerman.mp3"),
            Assert.Single(filings).To);
    }

    /// <summary>
    /// A folder called Unknown Artist is where music goes to be lost. Untidy is a smaller
    /// problem than somewhere nobody will look.
    /// </summary>
    [Fact]
    public void A_track_that_does_not_know_what_it_is_stays_where_it_is()
        => Assert.Empty(MusicLibrary.Filings(
            [new(@"C:\downloads\05.mp3", new MediaTags(false, "", "", "", "", 0), 1000)], @"D:\Music"));

    /// <summary>
    /// Somebody reading "312 files to move" would believe their library was in a worse state
    /// than it is.
    /// </summary>
    [Fact]
    public void A_track_already_where_it_belongs_is_not_a_move()
    {
        var already = Path.Combine(@"D:\Music", "Nina Simone", "Pastel Blues", "Sinnerman.mp3");

        Assert.Empty(MusicLibrary.Filings(
            [Track(already, "Nina Simone", "Sinnerman", "Pastel Blues")], @"D:\Music"));
    }

    /// <summary>
    /// A folder called "AC/DC" is two folders, and one ending in a full stop cannot be opened
    /// afterwards.
    /// </summary>
    [Theory]
    [InlineData("AC/DC", "AC-DC")]
    [InlineData("Where Are We Now?", "Where Are We Now-")]
    [InlineData("Trailing. ", "Trailing")]
    public void A_name_a_filesystem_would_refuse_is_made_into_one_it_takes(string name, string expected)
        => Assert.Equal(expected, MusicLibrary.Safe(name));

    // ── Reading a real file ───────────────────────────────────────────────

    /// <summary>
    /// The tags come out of the encoder's own report, so this is checked against a file the
    /// encoder made rather than against a string somebody wrote here.
    /// </summary>
    [Fact]
    public async Task What_a_real_file_says_about_itself_is_what_comes_back()
    {
        if (!EncoderHere)
        {
            return;
        }

        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "made.m4a");

        var made = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            FfmpegMediaExport.Find())
        {
            ArgumentList =
            {
                "-y", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", "1",
                "-metadata", "title=Wild Is The Wind",
                "-metadata", "artist=Nina Simone",
                "-metadata", "album=Wild Is The Wind",
                "-metadata", "date=1966",
                "-c:a", "aac", path,
            },
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        await made.WaitForExitAsync();

        var tags = await new MediaLook().TagsAsync(path);

        Assert.True(tags.Ok);
        Assert.Equal("Wild Is The Wind", tags.Title);
        Assert.Equal("Nina Simone", tags.Artist);
        Assert.Equal(1966, tags.Year);
    }

    /// <summary>
    /// "Duration: 00:03:12" and "Stream #0:0" sit in the same block as the tags and are not
    /// tags. Both would otherwise land in somebody's library as an artist.
    /// </summary>
    [Fact]
    public async Task Nothing_that_is_not_a_tag_is_read_as_one()
    {
        if (!EncoderHere)
        {
            return;
        }

        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "bare.m4a");

        var made = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            FfmpegMediaExport.Find())
        {
            ArgumentList = { "-y", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", "1", "-c:a", "aac", path },
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        await made.WaitForExitAsync();

        var tags = await new MediaLook().TagsAsync(path);

        Assert.Equal(string.Empty, tags.Artist);
        Assert.DoesNotContain("Stream", tags.Title, StringComparison.Ordinal);
    }

    // ── As tools ──────────────────────────────────────────────────────────

    private sealed class Answers(ToolApprovalDecision decision) : IToolApprovalService
    {
        public ToolApprovalRequest? Asked { get; private set; }

        public ValueTask<ToolApprovalDecision> RequestAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Asked = request;
            return ValueTask.FromResult(decision);
        }
    }

    private static IAgentTool? Tool(string name, IToolApprovalService? approval = null)
        => new MusicLibraryToolSource(approval: approval).Tools.SingleOrDefault(tool => tool.Name == name);

    [Fact]
    public void Looking_at_a_library_changes_nothing_so_neither_looking_tool_asks()
    {
        if (!EncoderHere)
        {
            return;
        }

        Assert.True(Tool("music_duplicates")!.IsReadOnly);
        Assert.True(Tool("music_filing")!.IsReadOnly);
    }

    /// <summary>
    /// Moving somebody's music is not something a tool does on its own judgement.
    /// </summary>
    [Fact]
    public void With_no_way_to_ask_nothing_that_moves_a_file_is_offered()
    {
        if (!EncoderHere)
        {
            return;
        }

        Assert.Null(Tool("music_file"));
        Assert.NotNull(Tool("music_file", new Answers(ToolApprovalDecision.Allowed)));
    }

    [Fact]
    public async Task A_folder_that_is_not_there_says_so()
    {
        if (!EncoderHere)
        {
            return;
        }

        var result = await Tool("music_duplicates")!.InvokeAsync(
            new JsonObject { ["folder"] = Path.Combine(_folder, "nowhere") });

        Assert.False(result.Success);
    }

    /// <summary>
    /// Nothing moves without a yes, and a no means nothing moved at all rather than some of
    /// it moved and then stopped.
    /// </summary>
    [Fact]
    public async Task Refused_means_every_file_is_still_where_it_was()
    {
        if (!EncoderHere)
        {
            return;
        }

        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "made.m4a");

        var made = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            FfmpegMediaExport.Find())
        {
            ArgumentList =
            {
                "-y", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", "1",
                "-metadata", "title=Sinnerman", "-metadata", "artist=Nina Simone",
                "-metadata", "album=Pastel Blues", "-c:a", "aac", path,
            },
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        await made.WaitForExitAsync();

        var approver = new Answers(ToolApprovalDecision.Denied);

        var result = await Tool("music_file", approver)!.InvokeAsync(
            new JsonObject { ["folder"] = _folder });

        Assert.False(result.Success);
        Assert.True(File.Exists(path));
        Assert.NotNull(approver.Asked);
        Assert.Contains("Move 1", approver.Asked!.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Allowed_means_it_is_where_it_belongs_and_nothing_was_lost()
    {
        if (!EncoderHere)
        {
            return;
        }

        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "made.m4a");

        var made = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            FfmpegMediaExport.Find())
        {
            ArgumentList =
            {
                "-y", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", "1",
                "-metadata", "title=Sinnerman", "-metadata", "artist=Nina Simone",
                "-metadata", "album=Pastel Blues", "-c:a", "aac", path,
            },
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        await made.WaitForExitAsync();

        var result = await Tool("music_file", new Answers(ToolApprovalDecision.Allowed))!.InvokeAsync(
            new JsonObject { ["folder"] = _folder });

        Assert.True(result.Success, result.FailureMessage);
        Assert.True(File.Exists(Path.Combine(_folder, "Nina Simone", "Pastel Blues", "Sinnerman.m4a")));
        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// Two different recordings can genuinely share an artist, an album and a title, and
    /// overwriting one with the other loses music somebody cannot get back. So a name already
    /// taken gets a number — the promise `design_save` already makes, for a stronger reason.
    /// </summary>
    [Fact]
    public async Task A_name_already_taken_gets_a_number_rather_than_replacing_anything()
    {
        if (!EncoderHere)
        {
            return;
        }

        Directory.CreateDirectory(_folder);

        // One already filed, and a different recording with the same three tags beside it.
        var belongs = Path.Combine(_folder, "Nina Simone", "Pastel Blues", "Sinnerman.m4a");
        Directory.CreateDirectory(Path.GetDirectoryName(belongs)!);
        await File.WriteAllTextAsync(belongs, "an older copy");

        var loose = Path.Combine(_folder, "made.m4a");

        var made = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            FfmpegMediaExport.Find())
        {
            ArgumentList =
            {
                "-y", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", "1",
                "-metadata", "title=Sinnerman", "-metadata", "artist=Nina Simone",
                "-metadata", "album=Pastel Blues", "-c:a", "aac", loose,
            },
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        await made.WaitForExitAsync();

        await Tool("music_file", new Answers(ToolApprovalDecision.Allowed))!.InvokeAsync(
            new JsonObject { ["folder"] = _folder });

        Assert.Equal("an older copy", await File.ReadAllTextAsync(belongs));
        Assert.True(File.Exists(Path.Combine(
            _folder, "Nina Simone", "Pastel Blues", "Sinnerman (2).m4a")));
    }

    // ── How good a file is ────────────────────────────────────────────────

    /// <summary>
    /// A track whose peak sits at the ceiling has been squashed somewhere in its history, and
    /// it is the commonest thing wrong with a file that otherwise looks perfect. Measured
    /// against files the encoder made loud and quiet on purpose.
    /// </summary>
    [Fact]
    public async Task A_squashed_file_is_noticed_and_an_ordinary_one_is_not()
    {
        if (!EncoderHere)
        {
            return;
        }

        Directory.CreateDirectory(_folder);

        var loud = Path.Combine(_folder, "loud.wav");
        var quiet = Path.Combine(_folder, "quiet.wav");

        // ffmpeg's sine comes out at an eighth of full scale — measured, because the first
        // version of this test assumed 0dB meant "as loud as it goes" and got -18.
        await Encode("sine=frequency=440:duration=1", "volume=18dB", loud);
        await Encode("sine=frequency=440:duration=1", "volume=-20dB", quiet);

        var squashed = await new MediaLook().QualityAsync(loud);
        var ordinary = await new MediaLook().QualityAsync(quiet);

        Assert.True(squashed.Ok, squashed.Problem);
        Assert.True(squashed.Clipped, $"peak was {squashed.Peak}");
        Assert.False(ordinary.Clipped, $"peak was {ordinary.Peak}");
        Assert.True(ordinary.Loudness < squashed.Loudness);
    }

    /// <summary>
    /// "Hi-res" is anybody's definition and this is the usual one: better than a CD in how
    /// often it was sampled or how finely. Read from the encoder's own line rather than the
    /// file extension, because a .flac can hold anything.
    /// </summary>
    [Fact]
    public async Task Better_than_a_cd_is_read_off_the_file_rather_than_its_name()
    {
        if (!EncoderHere)
        {
            return;
        }

        Directory.CreateDirectory(_folder);

        var cd = Path.Combine(_folder, "cd.wav");
        var better = Path.Combine(_folder, "better.flac");

        await Encode("sine=frequency=440:duration=1", "aresample=44100", cd, "-c:a", "pcm_s16le");
        await Encode("sine=frequency=440:duration=1", "aresample=96000", better, "-c:a", "flac", "-sample_fmt", "s32");

        Assert.False((await new MediaLook().QualityAsync(cd)).BetterThanCd);

        var hires = await new MediaLook().QualityAsync(better);

        Assert.True(hires.BetterThanCd, $"{hires.SampleRate}Hz at {hires.Bits} bits");
        Assert.Equal(96000, hires.SampleRate);
    }

    [Fact]
    public async Task A_file_that_is_not_there_cannot_be_measured()
        => Assert.False((await new MediaLook().QualityAsync(Path.Combine(_folder, "nothing.wav"))).Ok);

    private static async Task Encode(string source, string filter, string path, params string[] extra)
    {
        var run = new System.Diagnostics.ProcessStartInfo(FfmpegMediaExport.Find())
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in new[] { "-y", "-f", "lavfi", "-i", source, "-af", filter })
        {
            run.ArgumentList.Add(argument);
        }

        foreach (var argument in extra)
        {
            run.ArgumentList.Add(argument);
        }

        run.ArgumentList.Add(path);

        var made = System.Diagnostics.Process.Start(run)!;
        await made.WaitForExitAsync();
    }
}
