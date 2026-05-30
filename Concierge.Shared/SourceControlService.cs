namespace Concierge.Shared;

public sealed record SourceControlStatus(
    bool IsGitRepository,
    string WorkspaceRoot,
    HardeningStatus GateStatus,
    string Evidence);

public interface ISourceControlService
{
    SourceControlStatus GetStatus(string workspaceRoot);
}

public sealed class SourceControlService : ISourceControlService
{
    public SourceControlStatus GetStatus(string workspaceRoot)
    {
        var gitPath = Path.Combine(workspaceRoot, ".git");
        var isRepo = Directory.Exists(gitPath) || File.Exists(gitPath);
        return isRepo
            ? new SourceControlStatus(true, workspaceRoot, HardeningStatus.Ready, ".git metadata is present.")
            : new SourceControlStatus(false, workspaceRoot, HardeningStatus.Blocked, "No .git metadata found, so release tags and history are blocked.");
    }
}
