using Concierge.Shared.Session;

namespace Concierge.Tests;

/// <summary>
/// What you had, restored — and what is deliberately not.
///
/// Restored: the thread you were in, text typed and not sent, and the skills
/// that were on. That last is the one worth the machinery: a skill composes
/// into the system prompt before every turn, so losing it across a restart
/// changes how the assistant answers with nothing on screen to say why.
///
/// Not restored: an interrupted run. A partial reply is already recovered into
/// the thread by the draft checkpoint; a run that stopped while asking to write
/// a file must not pick itself back up on next launch.
/// </summary>
public sealed class SessionStateTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"session-{Guid.NewGuid():N}.json");

    private FileSessionState New() => new(_path);

    public void Dispose()
    {
        foreach (var path in new[] { _path, _path + ".tmp" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best effort; the test-run temp root is swept regardless.
            }
        }
    }

    [Fact]
    public async Task A_first_launch_has_an_empty_session_rather_than_a_failure()
    {
        var snapshot = await New().LoadAsync();

        Assert.Null(snapshot.LastConversationId);
        Assert.Empty(snapshot.Skills);
        Assert.Empty(snapshot.Unsent);
    }

    [Fact]
    public async Task The_thread_and_the_skills_survive_a_restart()
    {
        var conversation = Guid.NewGuid();

        await New().SaveAsync(new SessionSnapshot(
            LastConversationId: conversation,
            ActiveSkillIds: new[] { "accessibility", "api-engineering" }));

        // A different instance, as a relaunch would be.
        var restored = await New().LoadAsync();

        Assert.Equal(conversation, restored.LastConversationId);
        Assert.Equal(2, restored.Skills.Count);
        Assert.Contains("accessibility", restored.Skills);
    }

    /// <summary>
    /// Two threads each holding a half-written question must not overwrite each
    /// other, which is why unsent text is keyed by conversation rather than
    /// held as one string.
    /// </summary>
    [Fact]
    public async Task Unsent_text_is_kept_per_thread()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await New().SaveAsync(new SessionSnapshot(UnsentText: new Dictionary<string, string>
        {
            [first.ToString("N")] = "half a question about billing",
            [second.ToString("N")] = "something else entirely",
        }));

        var restored = await New().LoadAsync();

        Assert.Equal("half a question about billing", restored.UnsentFor(first));
        Assert.Equal("something else entirely", restored.UnsentFor(second));
        Assert.Equal(string.Empty, restored.UnsentFor(Guid.NewGuid()));
    }

    /// <summary>
    /// A corrupt file is a first launch, not a crash. The alternative is an app
    /// that will not open because of a file that only holds your place.
    /// </summary>
    [Fact]
    public async Task A_corrupt_file_reads_as_an_empty_session()
    {
        await File.WriteAllTextAsync(_path, "{ this is not json");

        var snapshot = await New().LoadAsync();

        Assert.Null(snapshot.LastConversationId);
        Assert.Empty(snapshot.Skills);
    }

    [Fact]
    public async Task An_empty_file_reads_as_an_empty_session()
    {
        await File.WriteAllTextAsync(_path, string.Empty);

        Assert.Null((await New().LoadAsync()).LastConversationId);
    }

    /// <summary>
    /// Written beside and moved into place. A crash halfway through a direct
    /// write leaves a truncated file, and the next launch would read that as an
    /// empty session and silently drop every active skill.
    /// </summary>
    [Fact]
    public async Task A_save_leaves_no_temporary_file_behind()
    {
        await New().SaveAsync(new SessionSnapshot(LastConversationId: Guid.NewGuid()));

        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public async Task Saving_twice_replaces_rather_than_appends()
    {
        var state = New();
        var second = Guid.NewGuid();

        await state.SaveAsync(new SessionSnapshot(LastConversationId: Guid.NewGuid()));
        await state.SaveAsync(new SessionSnapshot(LastConversationId: second));

        Assert.Equal(second, (await state.LoadAsync()).LastConversationId);
    }

    /// <summary>
    /// The composer saves as you type, so concurrent saves are not theoretical.
    /// </summary>
    [Fact]
    public async Task Concurrent_saves_do_not_corrupt_the_file()
    {
        var state = New();

        await Task.WhenAll(Enumerable.Range(0, 24).Select(i =>
            state.SaveAsync(new SessionSnapshot(
                ActiveSkillIds: new[] { $"skill-{i}" }))));

        var restored = await state.LoadAsync();

        // Whichever won, the file is readable and holds exactly one write.
        Assert.Single(restored.Skills);
    }

    [Fact]
    public async Task Clearing_forgets_everything()
    {
        var state = New();
        await state.SaveAsync(new SessionSnapshot(LastConversationId: Guid.NewGuid()));

        await state.ClearAsync();

        Assert.Null((await state.LoadAsync()).LastConversationId);
    }

    /// <summary>
    /// Saving must never be what stops the app. An unwritable path is a lost
    /// place next launch, not an exception into the composer.
    /// </summary>
    [Fact]
    public async Task An_unwritable_path_is_swallowed()
    {
        var impossible = new FileSessionState(
            Path.Combine(Path.GetTempPath(), "session-\0-invalid", "session.json"));

        await impossible.SaveAsync(new SessionSnapshot(LastConversationId: Guid.NewGuid()));
        Assert.Null((await impossible.LoadAsync()).LastConversationId);
    }
}
