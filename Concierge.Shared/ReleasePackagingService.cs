namespace Concierge.Shared;

public sealed record ReleasePackageTarget(
    string Id,
    string Name,
    string TargetFramework,
    bool RequiresOwnerCredential,
    string EvidenceNeeded);

public interface IReleasePackagingService
{
    IReadOnlyList<ReleasePackageTarget> GetTargets();
}

public sealed class ReleasePackagingService : IReleasePackagingService
{
    private static readonly IReadOnlyList<ReleasePackageTarget> Targets =
    [
        new("web", "Web", "net10.0", false, "Published web artifact, checksum, smoke test, and deployment target."),
        new("windows", "Windows", "net10.0-windows10.0.19041.0", true, "MSIX or installer artifact, certificate status, checksum, and smoke test."),
        new("android", "Android", "net10.0-android", true, "APK/AAB artifact, keystore status, store checks, and device smoke test."),
        new("ios", "iOS", "net10.0-ios", true, "Archive artifact, Apple signing profile, store checks, and simulator/device evidence."),
        new("macos", "macOS", "net10.0-maccatalyst", true, "App bundle, certificate, notarization evidence, and smoke test."),
        new("linux", "Linux", "net10.0", true, "Package artifact, repo credential state, checksum, and install test.")
    ];

    public IReadOnlyList<ReleasePackageTarget> GetTargets()
    {
        return Targets;
    }
}
