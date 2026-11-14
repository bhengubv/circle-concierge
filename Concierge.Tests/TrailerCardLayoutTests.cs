using Concierge.Shared.Design;

namespace Concierge.Tests;

public sealed class TrailerCardLayoutTests
{
    [Fact]
    public void A_source_passage_is_wrapped_into_readable_lines_before_rendering()
    {
        var words = "The lantern leads Mara to clue number 1 in the city.";
        var wrapped = FfmpegMediaExport.WrapCardText(words);

        Assert.True(wrapped.Split('\n').Length >= 2);
        Assert.All(wrapped.Split('\n'), line => Assert.InRange(line.Length, 1, 22));
        Assert.Equal(words, wrapped.Replace("\n", " "));
    }

    [Fact]
    public void Long_unbroken_source_text_cannot_escape_the_card_width()
    {
        var wrapped = FfmpegMediaExport.WrapCardText(new string('W', 70));
        Assert.All(wrapped.Split('\n'), line => Assert.InRange(line.Length, 1, 22));
    }
}
