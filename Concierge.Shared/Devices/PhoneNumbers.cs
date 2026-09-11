namespace Concierge.Shared.Devices;

/// <summary>
/// Whether a string is a phone number, or something else wearing one.
///
/// In Shared rather than beside the two capabilities that use it, for the reason
/// `WatchFace` is: a decision worth making carefully is a decision worth testing,
/// and nothing in the MAUI head can be tested from this machine. The capabilities
/// stay thin and this holds the part that has to be right.
///
/// **What it refuses is the point.** `*` and `#` make a USSD or MMI code, and some
/// Android diallers act on one the moment it arrives rather than waiting to be
/// dialled — including codes that wipe a handset. A model writing a number out of
/// a sentence somebody sent it must not be able to reach that, and an approval card
/// showing `*2767*3855#` would look like a short harmless string to anybody who has
/// not met one before.
///
/// So this is a list of what is allowed rather than a list of what is not. A
/// denylist here would have to guess every dangerous sequence; the allowlist only
/// has to know what a phone number looks like, and phone numbers are not a moving
/// target.
/// </summary>
public static class PhoneNumbers
{
    /// <summary>The shortest thing anybody actually dials.</summary>
    private const int Fewest = 3;

    /// <summary>
    /// The longest a real number is. E.164 says fifteen digits including the
    /// country code, and anything longer is not a number somebody is trying to
    /// reach.
    /// </summary>
    private const int Most = 15;

    /// <summary>Whether this can be handed to a dialler.</summary>
    public static bool Dialable(string? number)
    {
        if (string.IsNullOrWhiteSpace(number))
        {
            return false;
        }

        var digits = 0;

        foreach (var c in number)
        {
            if (char.IsAsciiDigit(c))
            {
                digits++;
                continue;
            }

            // The punctuation real numbers are written with, and nothing else.
            // Notably absent: * and #, which are what a code is made of.
            if (c is '+' or '-' or ' ' or '(' or ')' or '.')
            {
                continue;
            }

            return false;
        }

        // A plus belongs at the front or not at all — "1+234" is not a number, and
        // letting it through means the dialler decides what it meant.
        if (number.LastIndexOf('+') > number.IndexOf('+'))
        {
            return false;
        }

        if (number.IndexOf('+') > 0)
        {
            return false;
        }

        return digits is >= Fewest and <= Most;
    }
}
