using Concierge.Shared.Devices;

namespace Concierge.Tests;

/// <summary>
/// Whether a string is a phone number, or something else wearing one.
///
/// This is the check standing in front of the two capabilities that reach another
/// person. Neither of them sends anything — one opens the messaging app with the
/// message written, the other opens the dialler with the number in it — so a
/// person presses the button either way. This is the gate before that.
///
/// **What it refuses matters more than what it allows.** `*` and `#` make a USSD
/// or MMI code, and some Android diallers act on one the moment it arrives rather
/// than waiting to be dialled — including codes that wipe a handset. A model
/// writing a number out of a sentence somebody sent it must not be able to reach
/// that, and an approval card showing `*2767*3855#` looks like a short harmless
/// string to anybody who has not met one before.
/// </summary>
public sealed class PhoneNumberTests
{
    [Theory]
    [InlineData("+27 82 555 1234")]
    [InlineData("+442071234567")]
    [InlineData("0821234567")]
    [InlineData("(020) 7123-4567")]
    [InlineData("555.1234")]
    [InlineData("911")]
    public void A_number_somebody_would_actually_dial_is_allowed(string number)
        => Assert.True(PhoneNumbers.Dialable(number), number);

    /// <summary>
    /// The ones this exists for. Each is a real code shape, and a dialler handed
    /// one may act rather than wait.
    /// </summary>
    [Theory]
    [InlineData("*2767*3855#")]
    [InlineData("*#06#")]
    [InlineData("##7780#")]
    [InlineData("*21*1234567#")]
    public void A_code_dressed_as_a_number_is_refused(string code)
        => Assert.False(PhoneNumbers.Dialable(code), code);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("mum")]
    [InlineData("tel:0821234567")]
    [InlineData("0821234567; rm -rf /")]
    [InlineData("<script>alert(1)</script>")]
    public void Anything_that_is_not_a_number_is_refused(string? notANumber)
        => Assert.False(PhoneNumbers.Dialable(notANumber));

    /// <summary>
    /// Too short to reach anybody, or too long to be real. E.164 stops at fifteen
    /// digits, and something longer is not a number somebody is trying to call.
    /// </summary>
    [Theory]
    [InlineData("12")]
    [InlineData("1234567890123456")]
    public void A_number_that_is_the_wrong_length_is_refused(string number)
        => Assert.False(PhoneNumbers.Dialable(number), number);

    /// <summary>
    /// A plus means a country code and belongs at the front. "1+234" is not a
    /// number, and letting it through means the dialler decides what it meant —
    /// which is exactly the kind of guessing this check exists to stop.
    /// </summary>
    [Theory]
    [InlineData("1+2345678")]
    [InlineData("+27+821234567")]
    public void A_plus_anywhere_but_the_front_is_refused(string number)
        => Assert.False(PhoneNumbers.Dialable(number), number);

    /// <summary>
    /// An allowlist, not a denylist, and deliberately. A denylist would have to
    /// guess every dangerous sequence somebody might find; this only has to know
    /// what a phone number looks like, and that is not a moving target. The test
    /// that holds it: anything outside the allowed set is out, whatever it is.
    /// </summary>
    [Fact]
    public void Nothing_outside_the_characters_a_number_is_written_with_gets_through()
    {
        foreach (var odd in "*#,;@/\\%$&!?'\"<>|=_~^[]{}")
        {
            Assert.False(PhoneNumbers.Dialable($"082123456{odd}"), $"'{odd}' got through.");
        }
    }
}
