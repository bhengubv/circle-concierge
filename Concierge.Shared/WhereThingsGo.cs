namespace Concierge.Shared;

/// <summary>
/// The folders a person already looks in, on a platform that has them.
///
/// **Four places asked the operating system for one of these and used the answer
/// unchecked.** `Environment.GetFolderPath` returns an empty string rather than
/// throwing when a platform has no such folder — which is the ordinary case on
/// Android, where an app has its own sandbox and no Music folder of its own.
/// `Path.Combine("", "Concierge", "Footage")` is then a *relative* path, so a clip
/// somebody downloaded landed in whatever the working directory happened to be and
/// the answer still read "Saved to Concierge\Footage\clip.mp4".
///
/// One of the five call sites already had the fallback and said in its own comment
/// why it was there — "not every platform has those, and a saved file nobody can
/// find is the same as no saved file". The other four were written afterwards
/// without it. That is this repository's signature defect wearing a different
/// coat: a screen, or an answer, asserting something untrue.
///
/// Found by reading the handheld against the desktop rather than by somebody
/// losing a file.
/// </summary>
public static class WhereThingsGo
{
    /// <summary>Music, or the home folder where there is no music folder.</summary>
    public static string Music => Or(Environment.SpecialFolder.MyMusic);

    /// <summary>Video, or the home folder where there is none.</summary>
    public static string Video => Or(Environment.SpecialFolder.MyVideos);

    /// <summary>Pictures, or the home folder where there is none.</summary>
    public static string Pictures => Or(Environment.SpecialFolder.MyPictures);

    /// <summary>Documents, or the home folder where there is none.</summary>
    public static string Documents => Or(Environment.SpecialFolder.MyDocuments);

    /// <summary>
    /// The named folder, or the home folder.
    ///
    /// `Personal` is the last resort rather than another guess: .NET maps it to the
    /// user's home directory on every platform this runs on, and on Android that is
    /// the app's own writable sandbox. It is somewhere a file genuinely lands.
    ///
    /// It can itself be empty on a platform nobody here has met, and an empty
    /// string is returned rather than invented over — a caller writing a relative
    /// path is at least writing it somewhere it can be found from, and making one
    /// up would be the same class of mistake in the other direction.
    /// </summary>
    private static string Or(Environment.SpecialFolder wanted)
    {
        var folder = Environment.GetFolderPath(wanted);

        return string.IsNullOrWhiteSpace(folder)
            ? Environment.GetFolderPath(Environment.SpecialFolder.Personal)
            : folder;
    }
}
