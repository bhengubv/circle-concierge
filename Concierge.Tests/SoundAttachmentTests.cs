using Concierge.Shared.Design;

namespace Concierge.Tests;

public sealed class SoundAttachmentTests
{
    [Fact]
    public void Recognises_audio_by_content_instead_of_filename()
    {
        var wav = new byte[44];
        "RIFF"u8.CopyTo(wav);
        "WAVE"u8.CopyTo(wav.AsSpan(8));
        Assert.Equal("audio/wav", SoundAttachment.MediaType(wav));

        var m4a = new byte[16];
        "ftyp"u8.CopyTo(m4a.AsSpan(4));
        Assert.Equal("audio/m4a", SoundAttachment.MediaType(m4a));

        Assert.Null(SoundAttachment.MediaType("not audio at all"u8));
        Assert.Null(SoundAttachment.MediaType(ReadOnlySpan<byte>.Empty));
    }
}
