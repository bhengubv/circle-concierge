using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// When something happens, and for how long.
///
/// A shot carried a single <c>seconds</c> number, which can only answer "how long
/// is this on screen". Diffusion Studio separates the traits, and the difference
/// shows the moment anybody asks for something ordinary — start this a beat after
/// the last one, skip the first four seconds, play it at half speed. With one
/// number none of those can be said at all.
///
/// The tests that matter are the ones about a clumsy sentence: what lands in these
/// properties comes from a model or a person, and "a couple of seconds" does not
/// parse. Falling back is the difference between a wonky shot and a broken canvas.
/// </summary>
public sealed class DesignTimingTests
{
    private static DesignNode Shot(params (string Key, string Value)[] props)
        => DesignNode.New(DesignNodeKind.Frame, null, props);

    [Fact]
    public void A_shot_with_nothing_set_takes_the_default_and_starts_at_once()
    {
        var timing = DesignTiming.Of(Shot(), defaultSeconds: 4);

        Assert.Equal(4, timing.Seconds);
        Assert.Equal(0, timing.DelaySeconds);
        Assert.Equal(0, timing.TrimSeconds);
        Assert.Equal(1.0, timing.Rate);
        Assert.Equal(4, timing.TotalSeconds);
    }

    /// <summary>
    /// The whole reason for the change: a document that says only `seconds` — every
    /// document that exists today — must behave exactly as it did.
    /// </summary>
    [Fact]
    public void A_shot_that_only_says_seconds_is_unchanged()
    {
        var timing = DesignTiming.Of(Shot(("seconds", "9")), defaultSeconds: 4);

        Assert.Equal(9, timing.Seconds);
        Assert.Equal(9, timing.TotalSeconds);
    }

    [Fact]
    public void Waiting_before_a_shot_starts_counts_towards_how_long_it_takes()
    {
        var timing = DesignTiming.Of(Shot(("seconds", "5"), ("delay", "2")), defaultSeconds: 4);

        Assert.Equal(5, timing.Seconds);
        Assert.Equal(2, timing.DelaySeconds);
        Assert.Equal(7, timing.TotalSeconds);
    }

    [Fact]
    public void Half_speed_takes_twice_as_long()
    {
        var timing = DesignTiming.Of(Shot(("seconds", "6"), ("rate", "0.5")), defaultSeconds: 4);

        Assert.Equal(0.5, timing.Rate);
        Assert.Equal(12, timing.TotalSeconds);
    }

    [Fact]
    public void Double_speed_takes_half_as_long()
        => Assert.Equal(3, DesignTiming.Of(Shot(("seconds", "6"), ("rate", "2")), 4).TotalSeconds);

    /// <summary>
    /// Rounded up, because a shot needing 2.4 seconds gets 3. Being a fraction
    /// generous is invisible; being a fraction short is a truncated word.
    /// </summary>
    [Fact]
    public void A_shot_that_does_not_divide_evenly_is_rounded_up()
        => Assert.Equal(3, DesignTiming.Of(Shot(("seconds", "5"), ("rate", "2")), 4).TotalSeconds);

    [Fact]
    public void Trimming_reads_back_and_does_not_change_how_long_the_shot_is()
    {
        var timing = DesignTiming.Of(Shot(("seconds", "5"), ("trim", "12")), defaultSeconds: 4);

        Assert.Equal(12, timing.TrimSeconds);
        Assert.Equal(5, timing.TotalSeconds);
    }

    /// <summary>
    /// What a person actually types. None of this parses, and none of it may
    /// throw — a sentence a model got slightly wrong must not break the canvas.
    /// </summary>
    [Theory]
    [InlineData("a couple")]
    [InlineData("")]
    [InlineData("two point five")]
    [InlineData("NaN")]
    [InlineData("∞")]
    public void Something_that_is_not_a_number_falls_back_rather_than_failing(string written)
    {
        var timing = DesignTiming.Of(Shot(("seconds", written), ("rate", written)), defaultSeconds: 4);

        Assert.Equal(4, timing.Seconds);
        Assert.Equal(1.0, timing.Rate);
    }

    [Theory]
    [InlineData("0", 1)]
    [InlineData("-30", 1)]
    [InlineData("99999", 120)]
    public void A_length_nobody_meant_is_brought_back_into_range(string written, int expected)
        => Assert.Equal(expected, DesignTiming.Of(Shot(("seconds", written)), 4).Seconds);

    [Theory]
    [InlineData("0", 0.25)]
    [InlineData("-1", 0.25)]
    [InlineData("100", 4.0)]
    public void A_speed_nobody_meant_is_brought_back_into_range(string written, double expected)
        => Assert.Equal(expected, DesignTiming.Of(Shot(("rate", written)), 4).Rate);

    /// <summary>
    /// A rate of zero would divide by zero on the way to a total. Clamping to a
    /// quarter is what stops the arithmetic, but the assertion worth keeping is
    /// that a finite number comes out.
    /// </summary>
    [Fact]
    public void A_speed_of_zero_does_not_produce_an_infinite_shot()
    {
        var timing = DesignTiming.Of(Shot(("seconds", "4"), ("rate", "0")), defaultSeconds: 4);

        Assert.True(timing.TotalSeconds is > 0 and < 1000);
    }
}
