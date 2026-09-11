using Concierge.Ai;
using Concierge.Shared.Chat;
using Microsoft.Extensions.Logging.Abstractions;

namespace Concierge.Tests;

/// <summary>
/// Unhappy-path tests for the on-device chat runtime's new
/// <see cref="IPersistableChatRuntime"/> surface. The happy paths require a
/// real MNN model handle (and several hundred MB of weights), which we
/// deliberately do NOT load here — these tests exercise the gates around the
/// generator so the no-throw contract holds even when the engine never came
/// up.
/// </summary>
public sealed class CircleAiChatRuntimeTests
{
    private static CircleAiChatRuntime BuildRuntime() =>
        new(NullLogger<CircleAiChatRuntime>.Instance, new CircleAiChatOptions());

    [Fact]
    public void Default_options_expose_a_session_snapshot_path()
    {
        var options = new CircleAiChatOptions();
        Assert.False(string.IsNullOrEmpty(options.SessionSnapshotPath));
        Assert.Contains("Concierge", options.SessionSnapshotPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sessions", options.SessionSnapshotPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Runtime_implements_persistable_contract()
    {
        // Concierge.MAUI's App.OnSleep / OnResume hooks pattern-match against
        // IPersistableChatRuntime before calling Save/Load. Regression-guard
        // the runtime so the cast keeps working.
        IChatRuntime runtime = BuildRuntime();
        Assert.IsAssignableFrom<IPersistableChatRuntime>(runtime);
    }

    [Fact]
    public void Snapshot_path_property_reflects_constructor_option()
    {
        var custom = "/tmp/concierge-test/active.session";
        var runtime = new CircleAiChatRuntime(
            NullLogger<CircleAiChatRuntime>.Instance,
            new CircleAiChatOptions { SessionSnapshotPath = custom });
        Assert.Equal(custom, ((IPersistableChatRuntime)runtime).SessionSnapshotPath);
    }

    [Fact]
    public async Task Save_session_with_empty_path_returns_false()
    {
        var runtime = BuildRuntime();
        var ok = await ((IPersistableChatRuntime)runtime).SaveSessionAsync("");
        Assert.False(ok);
    }

    [Fact]
    public async Task Save_session_before_engine_loads_returns_false_not_throws()
    {
        // Generator-load hasn't been kicked, so _generatorReady stays unfinished.
        // SaveSessionAsync must return false instead of waiting forever or
        // throwing — App.OnSleep can't block.
        var runtime = BuildRuntime();
        var ok = await ((IPersistableChatRuntime)runtime).SaveSessionAsync("/tmp/concierge-test-noengine.session");
        Assert.False(ok);
    }

    [Fact]
    public async Task Load_session_with_missing_file_returns_false()
    {
        var runtime = BuildRuntime();
        var bogus = Path.Combine(Path.GetTempPath(), $"concierge-doesnt-exist-{Guid.NewGuid():N}.session");
        var ok = await ((IPersistableChatRuntime)runtime).LoadSessionAsync(bogus);
        Assert.False(ok);
    }

    [Fact]
    public async Task Load_session_with_empty_path_returns_false()
    {
        var runtime = BuildRuntime();
        var ok = await ((IPersistableChatRuntime)runtime).LoadSessionAsync("");
        Assert.False(ok);
    }

    [Fact]
    public void Engine_label_shows_pending_state_before_load()
    {
        var runtime = BuildRuntime();
        Assert.Equal("CircleAI (pending)", runtime.EngineLabel);
        Assert.False(runtime.IsReady);
        Assert.Contains("queued", runtime.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Runtime_id_is_stable()
    {
        var runtime = BuildRuntime();
        Assert.Equal("circleai", runtime.Id);
    }

    [Fact]
    public async Task DisposeAsync_is_safe_when_engine_never_loaded()
    {
        // Critical — the loader-failed branch is the most likely state on
        // CI / clean dev boxes that don't have MNN natives. DisposeAsync
        // must complete cleanly without waiting on the dangling generator
        // ready task.
        var runtime = BuildRuntime();
        await runtime.DisposeAsync();
    }

    [Fact]
    public void Options_with_custom_models_directory_and_context_size_are_honoured()
    {
        var options = new CircleAiChatOptions
        {
            ModelsDirectory = "/tmp/circleai-test-models",
            RepositoryUrl = new Uri("https://example.test/"),
            ContextSize = 8192,
            MaxOutputTokens = 1024,
            SessionSnapshotPath = "/tmp/circleai-test/session.bin",
        };
        Assert.Equal("/tmp/circleai-test-models", options.ModelsDirectory);
        Assert.Equal(new Uri("https://example.test/"), options.RepositoryUrl);
        Assert.Equal(8192u, options.ContextSize);
        Assert.Equal(1024u, options.MaxOutputTokens);
        Assert.Equal("/tmp/circleai-test/session.bin", options.SessionSnapshotPath);
    }

    [Fact]
    public void Options_repository_url_can_be_disabled_for_offline_hosts()
    {
        // Pre-staged-model hosts (CircleOS, kiosk installs) want the
        // downloader off — the runtime must accept RepositoryUrl = null.
        var options = new CircleAiChatOptions { RepositoryUrl = null };
        Assert.Null(options.RepositoryUrl);
    }

    /// <summary>
    /// The prefill KV cache stays off, because the product does not work with it
    /// on.
    ///
    /// The cache is keyed on (modelId, systemPrompt), so it only engages once a
    /// system turn exists — and that path faults inside the native generator,
    /// killing the host mid-reply. Every real turn carries a system prompt, so
    /// that was every turn, and Concierge could not hold a conversation at all.
    /// It looked like a package bug for weeks because running the model host by
    /// hand sends no system prompt and therefore never touched the cache.
    ///
    /// This test is the reason the switch is an option rather than a literal.
    /// Turning it back on is a decision that needs evidence: feed the host a
    /// system turn and a 6,882-char payload and watch for "Fatal error." before
    /// changing this line.
    /// </summary>
    [Fact]
    public void The_prefill_cache_is_off_by_default()
    {
        Assert.False(new CircleAiChatOptions().UsePrefixCache);
    }
}
