using Concierge.Shared.Design;

namespace Concierge.Tests;

public sealed class ProbeTests
{
    [Fact]
    public void Saying_add_a_box_in_a_room_is_understood()
    {
        var room = DesignDocument.Blank(medium: DesignMedium.Scene);
        var heard = DesignSpeech.Hear(room, "add a box", null);

        Assert.True(heard.Understood, "not understood");
    }

    [Fact]
    public void Saying_add_a_room_is_understood()
    {
        var room = DesignDocument.Blank(medium: DesignMedium.Scene);
        var heard = DesignSpeech.Hear(room, "add a room", null);

        Assert.True(heard.Understood, "not understood");
    }

    [Fact]
    public void Saying_add_a_box_after_a_room_exists_is_understood()
    {
        var room = DesignDocument.Blank(medium: DesignMedium.Scene);
        var first = DesignSpeech.Hear(room, "add a room", null);
        var heard = DesignSpeech.Hear(first.Document, "add a box", null);

        Assert.True(heard.Understood, "not understood");
    }
}
