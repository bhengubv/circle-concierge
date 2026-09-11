namespace Concierge.Shared.Sandboxing;

/// <summary>How strong a boundary a platform can actually give.</summary>
public enum SandboxStrength
{
    /// <summary>
    /// None. The code runs with everything the app can reach. Named rather than implied,
    /// because a caller has to be able to refuse it.
    /// </summary>
    None = 0,

    /// <summary>
    /// A separate process with resource limits — it can be killed and capped, but it can
    /// still reach what the app can reach.
    /// </summary>
    Process = 1,

    /// <summary>A separate process the operating system also confines to a directory.</summary>
    Confined = 2,
}

/// <summary>What a platform can offer, and what it cannot.</summary>
/// <param name="Strength">The strongest boundary available here.</param>
/// <param name="Mechanism">What provides it, for a diagnostic to report.</param>
/// <param name="Explanation">Why it is not stronger, in plain words.</param>
public sealed record SandboxCapability(SandboxStrength Strength, string Mechanism, string Explanation);

/// <summary>
/// The boundary a model-written program runs inside.
/// </summary>
/// <remarks>
/// <para>
/// DeepSeek Harness runs model-written programs in a worker thread and says plainly that the
/// isolation is "not a security claim". That is a reasonable position on a developer's own
/// machine. It is not one to inherit on a device holding someone else's conversations, so the
/// boundary here is a first-class thing that a caller must obtain rather than assume.
/// </para>
/// <para>
/// The honest part is <see cref="SandboxStrength.None"/>. Some platforms cannot confine
/// anything, and saying so is what lets a caller decline to run rather than believe it is
/// protected.
/// </para>
/// </remarks>
public interface ICodeSandbox
{
    /// <summary>What this sandbox can actually enforce.</summary>
    SandboxCapability Capability { get; }

    /// <summary>Prepare a process to run confined. Called before the process is started.</summary>
    void Prepare(System.Diagnostics.ProcessStartInfo startInfo, string workspaceRoot);

    /// <summary>Confine a process that has already started.</summary>
    void Confine(System.Diagnostics.Process process);
}

/// <summary>
/// No boundary at all, said out loud.
/// </summary>
/// <remarks>
/// Exists so that "unconfined" is a decision a caller makes rather than a silence it mistakes
/// for safety. A runtime handed this will refuse to run unless the caller has explicitly
/// accepted it.
/// </remarks>
public sealed class UnconfinedSandbox : ICodeSandbox
{
    /// <inheritdoc />
    public SandboxCapability Capability { get; } = new(
        SandboxStrength.None,
        "none",
        "This platform provides no way to confine a child process, so a program would run with everything the app can reach.");

    /// <inheritdoc />
    public void Prepare(System.Diagnostics.ProcessStartInfo startInfo, string workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        startInfo.WorkingDirectory = workspaceRoot;
    }

    /// <inheritdoc />
    public void Confine(System.Diagnostics.Process process)
    {
        // Nothing to do, and saying nothing about it is the point of naming this class.
    }
}

/// <summary>
/// Chooses the strongest boundary the current platform can provide.
/// </summary>
/// <remarks>
/// The differences are real and worth stating plainly rather than papering over:
/// Windows has job objects; Linux has process namespaces and resource limits, and Landlock for
/// the filesystem half that is not in place; macOS has its sandbox; Android runs a child under
/// the app's own user id; and iOS forbids spawning children at all.
/// </remarks>
public static class CodeSandbox
{
    /// <summary>The best available boundary here.</summary>
    public static ICodeSandbox ForCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsJobObjectSandbox();
        }

        // Android is Linux and is deliberately not this. A child there runs under
        // the app's own user id, and wrapping it in unshare would report a boundary
        // that the platform does not actually give — the exact thing this file is
        // written to prevent.
        if (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
        {
            return new LinuxNamespaceSandbox();
        }

        // Mac Catalyst reports as macOS and is the desktop head, so it gets the same
        // boundary. iOS does not reach here at all — it forbids child processes.
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            return new MacSandbox();
        }

        return new UnconfinedSandbox();
    }

    /// <summary>What this platform can do, without building a sandbox to ask.</summary>
    public static SandboxCapability DescribeCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsJobObjectSandbox().Capability;
        }

        if (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
        {
            return new LinuxNamespaceSandbox().Capability;
        }

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            return new MacSandbox().Capability;
        }

        if (OperatingSystem.IsAndroid())
        {
            return new SandboxCapability(
                SandboxStrength.None,
                "none",
                "A child process on Android runs under the app's own user id, so it reaches everything the app reaches.");
        }

        if (OperatingSystem.IsIOS())
        {
            return new SandboxCapability(
                SandboxStrength.None,
                "none",
                "iOS does not permit an app to start a child process at all.");
        }

        return new UnconfinedSandbox().Capability;
    }
}
