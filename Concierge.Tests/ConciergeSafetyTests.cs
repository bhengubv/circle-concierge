using CircleAI.ContentPolicy;
using Concierge.Shared.Chat;
using Concierge.Shared.Safety;

namespace Concierge.Tests;

/// <summary>
/// Family Mode pipeline tests — the content filter, the refusal policy, the
/// JSONL audit log, and the decorator that wires them around any IChatRuntime.
/// </summary>
public sealed class ConciergeSafetyTests
{
    // ── ConciergeContentFilter ────────────────────────────────────────

    [Fact]
    public async Task Filter_off_allows_everything()
    {
        var filter = new ConciergeContentFilter(() => SafetyStrictness.Off);
        var finding = await filter.ClassifyAsync("kill the lights and load the gun");
        Assert.Equal(SafetyVerdict.Allow, finding.Verdict);
    }

    [Fact]
    public async Task Filter_strict_refuses_on_violence_pattern()
    {
        var filter = new ConciergeContentFilter(() => SafetyStrictness.Strict);
        var finding = await filter.ClassifyAsync("I want to kill the dragon");
        Assert.Equal(SafetyVerdict.Refuse, finding.Verdict);
        Assert.Equal("violence", finding.Category);
    }

    [Fact]
    public async Task Filter_balanced_flags_but_does_not_refuse_violence_pattern()
    {
        // " kill " alone is in the Strict list but not the Balanced list, so
        // Balanced should NOT match it. " murder" is in both — pick the
        // Balanced-only term to assert the flag-but-not-refuse path.
        var filter = new ConciergeContentFilter(() => SafetyStrictness.Balanced);
        var finding = await filter.ClassifyAsync("plot the murder mystery");
        Assert.Equal(SafetyVerdict.Flag, finding.Verdict);
    }

    [Fact]
    public async Task Filter_does_not_match_substring_inside_a_safe_word()
    {
        // Pattern is " gun " (spaces). "shogun" should not trip the matcher.
        var filter = new ConciergeContentFilter(() => SafetyStrictness.Strict);
        var finding = await filter.ClassifyAsync("the shogun unified Japan");
        Assert.Equal(SafetyVerdict.Allow, finding.Verdict);
    }

    [Fact]
    public async Task Filter_allows_normal_text_in_strict()
    {
        var filter = new ConciergeContentFilter(() => SafetyStrictness.Strict);
        var finding = await filter.ClassifyAsync("Help me plan a birthday party");
        Assert.Equal(SafetyVerdict.Allow, finding.Verdict);
    }

    // ── ConciergeRefusalPolicy ────────────────────────────────────────

    [Fact]
    public async Task Policy_no_findings_no_refuse()
    {
        var policy = new ConciergeRefusalPolicy();
        Assert.False(await policy.ShouldRefuseAsync(Array.Empty<SafetyFinding>()));
    }

    [Fact]
    public async Task Policy_single_refuse_refuses()
    {
        var policy = new ConciergeRefusalPolicy();
        var findings = new[]
        {
            new SafetyFinding(SafetyVerdict.Refuse, "violence", "x", 0.9f),
        };
        Assert.True(await policy.ShouldRefuseAsync(findings));
    }

    [Fact]
    public async Task Policy_single_flag_does_not_refuse()
    {
        var policy = new ConciergeRefusalPolicy();
        var findings = new[]
        {
            new SafetyFinding(SafetyVerdict.Flag, "violence", "x", 0.6f),
        };
        Assert.False(await policy.ShouldRefuseAsync(findings));
    }

    [Fact]
    public async Task Policy_two_distinct_flag_categories_refuses()
    {
        var policy = new ConciergeRefusalPolicy();
        var findings = new[]
        {
            new SafetyFinding(SafetyVerdict.Flag, "violence", "x", 0.6f),
            new SafetyFinding(SafetyVerdict.Flag, "self-harm", "y", 0.6f),
        };
        Assert.True(await policy.ShouldRefuseAsync(findings));
    }

    [Fact]
    public async Task Policy_two_flags_same_category_does_not_refuse()
    {
        var policy = new ConciergeRefusalPolicy();
        var findings = new[]
        {
            new SafetyFinding(SafetyVerdict.Flag, "violence", "x", 0.6f),
            new SafetyFinding(SafetyVerdict.Flag, "violence", "y", 0.6f),
        };
        Assert.False(await policy.ShouldRefuseAsync(findings));
    }

    // ── JsonSafetyAuditLog ────────────────────────────────────────────

    [Fact]
    public async Task Audit_log_round_trip_appends_and_reads_tail_first()
    {
        var path = Path.Combine(Path.GetTempPath(), $"audit-{Guid.NewGuid():N}.jsonl");
        try
        {
            var log = new JsonSafetyAuditLog(path);
            await log.LogAsync(new SafetyAuditEntry(
                DateTimeOffset.UtcNow, "alice", "user-turn", SafetyVerdict.Allow, "ok"));
            await log.LogAsync(new SafetyAuditEntry(
                DateTimeOffset.UtcNow, "alice", "model-reply", SafetyVerdict.Flag, "violence"));
            await log.LogAsync(new SafetyAuditEntry(
                DateTimeOffset.UtcNow, "bob", "user-turn", SafetyVerdict.Allow, "ok"));

            var alice = await log.ReadAsync("alice", limit: 10);
            Assert.Equal(2, alice.Count);
            // Tail-first.
            Assert.Equal("model-reply", alice[0].Action);
            Assert.Equal("user-turn", alice[1].Action);

            var everyone = await log.ReadAsync(null, limit: 10);
            Assert.Equal(3, everyone.Count);
            Assert.Equal("bob", everyone[0].UserId);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task Audit_log_missing_file_returns_empty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"audit-missing-{Guid.NewGuid():N}.jsonl");
        var log = new JsonSafetyAuditLog(path);
        var entries = await log.ReadAsync(null, limit: 10);
        Assert.Empty(entries);
    }

    [Fact]
    public async Task Audit_log_skips_corrupt_line()
    {
        var path = Path.Combine(Path.GetTempPath(), $"audit-corrupt-{Guid.NewGuid():N}.jsonl");
        try
        {
            // First write a real entry, then a partial line (simulating an
            // OS kill mid-write), then another real entry.
            var log = new JsonSafetyAuditLog(path);
            await log.LogAsync(new SafetyAuditEntry(
                DateTimeOffset.UtcNow, "alice", "user-turn", SafetyVerdict.Allow, "ok"));
            await File.AppendAllTextAsync(path, "{ partial-json-mid-w" + Environment.NewLine);
            await log.LogAsync(new SafetyAuditEntry(
                DateTimeOffset.UtcNow, "alice", "model-reply", SafetyVerdict.Allow, "ok"));

            var entries = await log.ReadAsync(null, limit: 10);
            // Two real entries survive; corrupt line silently skipped.
            Assert.Equal(2, entries.Count);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // ── ContentFilterChatRuntimeDecorator ─────────────────────────────

    [Fact]
    public async Task Decorator_off_passes_through_without_invoking_filter()
    {
        var inner = new FakeChatRuntime("hello world");
        var filter = new TrackingFilter();
        var decorator = new ContentFilterChatRuntimeDecorator(
            inner,
            filter,
            new ConciergeRefusalPolicy(),
            new InMemoryAuditLog(),
            () => new ConciergeSafetySettings { Strictness = SafetyStrictness.Off });

        var chunks = await CollectAsync(decorator.StreamAsync(new List<ChatTurn> { new("user", "hi") }));
        Assert.Equal("hello world", string.Concat(chunks));
        Assert.Equal(0, filter.CallCount);
    }

    [Fact]
    public async Task Decorator_refuses_inbound_user_turn_when_strict_match()
    {
        var inner = new FakeChatRuntime("inner response that never streams");
        var audit = new InMemoryAuditLog();
        var decorator = new ContentFilterChatRuntimeDecorator(
            inner,
            new ConciergeContentFilter(() => SafetyStrictness.Strict),
            new ConciergeRefusalPolicy(),
            audit,
            () => new ConciergeSafetySettings { Strictness = SafetyStrictness.Strict });

        var chunks = await CollectAsync(decorator.StreamAsync(new List<ChatTurn>
        {
            new("user", "I want to kill the boss"),
        }));

        // Refusal stub, never the inner response.
        Assert.Single(chunks);
        Assert.Contains("can't help", chunks[0], StringComparison.OrdinalIgnoreCase);
        Assert.False(inner.WasInvoked);

        // Audit log captured the inbound refusal.
        Assert.Single(audit.Entries);
        Assert.Equal(SafetyVerdict.Refuse, audit.Entries[0].Verdict);
        Assert.Equal("user-turn", audit.Entries[0].Action);
    }

    [Fact]
    public async Task Decorator_allows_clean_turn_through_and_logs_two_entries()
    {
        var inner = new FakeChatRuntime("a friendly response with no issues");
        var audit = new InMemoryAuditLog();
        var decorator = new ContentFilterChatRuntimeDecorator(
            inner,
            new ConciergeContentFilter(() => SafetyStrictness.Strict),
            new ConciergeRefusalPolicy(),
            audit,
            () => new ConciergeSafetySettings { Strictness = SafetyStrictness.Strict });

        var chunks = await CollectAsync(decorator.StreamAsync(new List<ChatTurn>
        {
            new("user", "plan my birthday"),
        }));

        Assert.Equal("a friendly response with no issues", string.Concat(chunks));
        Assert.True(inner.WasInvoked);

        // One user-turn entry + one model-reply entry.
        Assert.Equal(2, audit.Entries.Count);
        Assert.Equal("user-turn", audit.Entries[0].Action);
        Assert.Equal("model-reply", audit.Entries[1].Action);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> source)
    {
        var list = new List<string>();
        await foreach (var chunk in source) list.Add(chunk);
        return list;
    }

    private sealed class FakeChatRuntime : IChatRuntime
    {
        private readonly string _response;
        public bool WasInvoked { get; private set; }
        public FakeChatRuntime(string response) { _response = response; }

        public string Id => "fake";
        public string EngineLabel => "Fake";
        public bool IsReady => true;
        public string StatusMessage => "ok";

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            WasInvoked = true;
            await Task.Yield();
            yield return _response;
        }
    }

    private sealed class TrackingFilter : IContentFilter
    {
        public int CallCount { get; private set; }
        public string BackendId => "tracking";
        public ValueTask<SafetyFinding> ClassifyAsync(string text, CancellationToken ct = default)
        {
            CallCount++;
            return ValueTask.FromResult(new SafetyFinding(SafetyVerdict.Allow, "ok", "", 1f));
        }
    }

    private sealed class InMemoryAuditLog : ISafetyAuditLog
    {
        public List<SafetyAuditEntry> Entries { get; } = new();
        public string BackendId => "memory";
        public ValueTask LogAsync(SafetyAuditEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }
        public ValueTask<IReadOnlyList<SafetyAuditEntry>> ReadAsync(string? userId, int limit = 100, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SafetyAuditEntry>>(Entries.AsReadOnly());
    }
}
