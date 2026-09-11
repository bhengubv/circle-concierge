using System.Runtime.CompilerServices;
using Concierge.Shared.Chat;

namespace Concierge.Tests;

/// <summary>
/// Which order the providers are asked in.
///
/// Failover decided whether a runtime *may* be asked and said nothing about order,
/// so the chain was whatever order the alternatives happened to arrive in — every
/// turn, forever. A provider that failed thirty seconds ago was tried first again,
/// and the one that had been answering all afternoon waited behind it.
///
/// Antra resolves rather than lists: tiers, rotation within a tier, and a
/// rate-limited source demoted to the back rather than dropped. The test that
/// matters most is the last one — demoted, never removed. A chain that quietly
/// gets shorter is a product that quietly gets less able.
/// </summary>
public sealed class RuntimeResolverTests
{
    private sealed class Stub(string id) : IChatRuntime
    {
        public string Id => id;
        public string EngineLabel => id;
        public bool IsReady => true;
        public string StatusMessage => "ready";

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return id;
        }
    }

    private static (RuntimeResolver Resolver, Action<TimeSpan> Advance) Clocked()
    {
        var now = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        var resolver = new RuntimeResolver(() => now);
        return (resolver, span => now = now.Add(span));
    }

    [Fact]
    public void With_nothing_to_order_it_returns_nothing()
    {
        Assert.Empty(new RuntimeResolver().Order(null));
        Assert.Empty(new RuntimeResolver().Order([]));
    }

    [Fact]
    public void Untouched_providers_keep_the_order_they_arrived_in()
    {
        var (resolver, _) = Clocked();
        var order = resolver.Order([new Stub("a"), new Stub("b"), new Stub("c")]);

        Assert.Equal(["a", "b", "c"], order.Select(r => r.Id));
    }

    /// <summary>
    /// The point of rotation: spread the load rather than hammering whichever
    /// happens to be first in the list.
    /// </summary>
    [Fact]
    public void Healthy_providers_are_started_from_a_different_place_each_turn()
    {
        var (resolver, _) = Clocked();
        IReadOnlyList<IChatRuntime> all = [new Stub("a"), new Stub("b"), new Stub("c")];

        Assert.Equal(["a", "b", "c"], resolver.Order(all).Select(r => r.Id));
        Assert.Equal(["b", "c", "a"], resolver.Order(all).Select(r => r.Id));
        Assert.Equal(["c", "a", "b"], resolver.Order(all).Select(r => r.Id));
        Assert.Equal(["a", "b", "c"], resolver.Order(all).Select(r => r.Id));
    }

    [Fact]
    public void One_provider_is_not_rotated_and_does_not_move_the_turn_on()
    {
        var (resolver, _) = Clocked();
        var single = new Stub("only");

        Assert.Equal(["only"], resolver.Order([single]).Select(r => r.Id));
        Assert.Equal(["only"], resolver.Order([single]).Select(r => r.Id));
    }

    /// <summary>
    /// Demoted, not dropped. Antra's actual insight, and the one that keeps a bad
    /// thirty seconds from costing a provider for the rest of the session.
    /// </summary>
    [Fact]
    public void A_provider_that_just_failed_goes_to_the_back_and_stays_reachable()
    {
        var (resolver, _) = Clocked();
        var a = new Stub("a");
        IReadOnlyList<IChatRuntime> all = [a, new Stub("b"), new Stub("c")];

        resolver.RecordFailure(a);
        var order = resolver.Order(all);

        Assert.Equal("a", order[^1].Id);
        Assert.Equal(3, order.Count);
        Assert.Contains(order, r => r.Id == "a");
    }

    [Fact]
    public void After_the_cooldown_a_failure_is_forgotten()
    {
        var (resolver, advance) = Clocked();
        var a = new Stub("a");

        resolver.RecordFailure(a);
        Assert.True(resolver.IsCoolingDown(a));

        advance(RuntimeResolver.Cooldown + TimeSpan.FromSeconds(1));

        Assert.False(resolver.IsCoolingDown(a));
        Assert.Equal("a", resolver.Order([a, new Stub("b")])[0].Id);
    }

    [Fact]
    public void Answering_clears_a_previous_failure_immediately()
    {
        var (resolver, _) = Clocked();
        var a = new Stub("a");

        resolver.RecordFailure(a);
        Assert.True(resolver.IsCoolingDown(a));

        resolver.RecordSuccess(a);

        Assert.False(resolver.IsCoolingDown(a));
    }

    /// <summary>
    /// When everything is in cooldown there is no good option, only a least-bad
    /// one: the provider that broke longest ago is the likeliest to have
    /// recovered.
    /// </summary>
    [Fact]
    public void When_everything_is_cooling_the_one_that_broke_longest_ago_goes_first()
    {
        var (resolver, advance) = Clocked();
        var a = new Stub("a");
        var b = new Stub("b");

        resolver.RecordFailure(a);
        advance(TimeSpan.FromSeconds(30));
        resolver.RecordFailure(b);

        var order = resolver.Order([b, a]);

        Assert.Equal(["a", "b"], order.Select(r => r.Id));
    }

    [Fact]
    public void A_cooling_provider_never_jumps_ahead_of_a_healthy_one()
    {
        var (resolver, _) = Clocked();
        var broken = new Stub("broken");
        var fine = new Stub("fine");

        resolver.RecordFailure(broken);

        Assert.Equal(["fine", "broken"], resolver.Order([broken, fine]).Select(r => r.Id));
    }
}
