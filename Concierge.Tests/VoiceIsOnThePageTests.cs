namespace Concierge.Tests;

/// <summary>
/// The microphone script being on the page at all.
///
/// **Pressing Speak on the desktop app failed at the very first step**, with
/// "Could not find 'conciergeVoice.start' ('conciergeVoice' was undefined)". The module that
/// wraps MediaRecorder was listed in the web head's App.razor from the day it was written and
/// never in the desktop head's index.html — so the microphone had never worked on the app most
/// people run, and the second defect behind it (posting to an endpoint that only the web head
/// maps) was hidden behind the first.
///
/// Read off the files, which is crude and is what is available: no test here loads a page in a
/// browser, and the failure is entirely about what the browser was given.
/// </summary>
public sealed class VoiceIsOnThePageTests
{
    private static string Read(params string[] parts)
        => File.ReadAllText(Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", ".." }.Concat(parts).ToArray())));

    [Fact]
    public void The_desktop_head_loads_the_microphone()
        => Assert.Contains(
            "concierge-voice.js", Read("Concierge", "wwwroot", "index.html"), StringComparison.Ordinal);

    [Fact]
    public void And_so_does_the_web_head()
        => Assert.Contains(
            "concierge-voice.js",
            Read("Concierge.Web", "Components", "App.razor"),
            StringComparison.Ordinal);

    /// <summary>
    /// And the file it asks for exists. A script tag pointing at nothing fails exactly the
    /// way no script tag does, which would have been a quiet way to "fix" this.
    /// </summary>
    [Fact]
    public void And_the_file_is_actually_there()
        => Assert.False(string.IsNullOrWhiteSpace(
            Read("Concierge.Shared.Components", "wwwroot", "concierge-voice.js")));
}
