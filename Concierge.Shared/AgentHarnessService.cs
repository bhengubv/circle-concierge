using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Concierge.Shared;

public interface IAgentHarnessService
{
    string WorkspaceRoot { get; }

    IReadOnlyList<ConciergeToolDefinition> GetTools();

    IReadOnlyList<ConciergeRunLog> GetRunLogs();

    Task<ConciergeToolResult> ReadFileAsync(string relativePath, CancellationToken cancellationToken = default);

    Task<ConciergeToolResult> ReadFileWindowAsync(string relativePath, int offsetLines, int maxLines, CancellationToken cancellationToken = default);

    Task<ConciergeToolResult> ListFilesAsync(string? relativePath, string? pattern, CancellationToken cancellationToken = default);

    Task<ConciergeToolResult> SearchTextAsync(string query, string? relativePath, string? pattern, CancellationToken cancellationToken = default);

    Task<FileWritePreview> PreviewWriteFileAsync(string relativePath, string content, CancellationToken cancellationToken = default);

    Task<FileWritePreview> PreviewEditFileAsync(string relativePath, string find, string replace, CancellationToken cancellationToken = default);

    Task<ConciergeToolResult> EditFileAsync(string relativePath, string find, string replace, bool approved, CancellationToken cancellationToken = default);

    Task<ConciergeToolResult> WriteFileAsync(string relativePath, string content, bool approved, CancellationToken cancellationToken = default);

    /// <summary>
    /// What confines a command on this machine. On the interface because a surface
    /// that reports what Concierge can do needs to be able to ask, and because a
    /// platform that can confine nothing has to be able to say so.
    /// </summary>
    Sandboxing.SandboxCapability Confinement { get; }

    Task<ConciergeToolResult> RunCommandAsync(string command, bool approved, CancellationToken cancellationToken = default);

    Task<ConciergeRunLog> RunGoalAsync(string goal, IReadOnlyList<string> commands, bool approved, CancellationToken cancellationToken = default);
}

public sealed class AgentHarnessService : IAgentHarnessService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] DestructiveMarkers = [" rm ", " del ", " rmdir ", " remove-item ", " format ", " shutdown ", " reset --hard"];
    private static readonly string[] DeniedPathSegments = [".git", ".ssh", ".aws", ".azure", "node_modules", "bin", "obj"];
    private static readonly HashSet<string> ProtectedExactFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "secrets.json",
        "id_rsa",
        "id_dsa",
        "id_ecdsa",
        "id_ed25519",
        "id_rsa.pub",
        "id_ed25519.pub",
        "authorized_keys",
        "known_hosts"
    };
    private static readonly string[] ProtectedFileSuffixes =
    [
        ".pfx",
        ".pem",
        ".p12",
        ".key",
        ".keystore",
        ".jks",
        ".asc",
        ".crt",
        ".cer"
    ];
    private static readonly HashSet<string> AllowedExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet",
        "git",
        "rg",
        "cmd",
        "pwsh",
        "powershell"
    };

    private readonly object _gate = new();
    private readonly List<ConciergeRunLog> _logs;
    private readonly IAgentRunLogPublisher _publisher;
    private readonly IToolTimeoutPolicy _timeouts;

    public AgentHarnessService()
        : this(LocateWorkspaceRoot(), null)
    {
    }

    public AgentHarnessService(string workspaceRoot)
        : this(workspaceRoot, null)
    {
    }

    /// <summary>
    /// Work inside a workspace the host chose. Preferred over the root-discovering
    /// constructors, which fall back to the process's current directory when no solution
    /// file is found — an accident on any machine that is not a developer's.
    /// </summary>
    public AgentHarnessService(ConciergeWorkspace workspace, IAgentRunLogPublisher? publisher = null, IToolTimeoutPolicy? timeouts = null)
        : this((workspace ?? throw new ArgumentNullException(nameof(workspace))).Root, publisher, timeouts)
    {
    }

    public AgentHarnessService(string workspaceRoot, IAgentRunLogPublisher? publisher)
        : this(workspaceRoot, publisher, null)
    {
    }

    /// <param name="workspaceRoot">The trusted root every path is resolved against.</param>
    /// <param name="publisher">Where finished run logs are relayed, or null for nowhere.</param>
    /// <param name="timeouts">
    /// How long a command may run. Null keeps <see cref="ToolTimeoutPolicy.Default"/>, which
    /// is the five-minute ceiling this class enforced before the policy was extracted.
    /// </param>
    public AgentHarnessService(string workspaceRoot, IAgentRunLogPublisher? publisher, IToolTimeoutPolicy? timeouts)
    {
        WorkspaceRoot = Path.GetFullPath(workspaceRoot);
        _publisher = publisher ?? new NullAgentRunLogPublisher();
        _timeouts = timeouts ?? ToolTimeoutPolicy.Default;
        Directory.CreateDirectory(LogDirectory);
        _logs = LoadLogs().ToList();
    }

    /// <summary>
    /// Walks up from <see cref="AppContext.BaseDirectory"/> looking for the Concierge solution
    /// marker (<c>Concierge.slnx</c>). Falls back to the current directory when not found.
    /// Exposed so DI factories can resolve the workspace root without reflecting into private state.
    /// </summary>
    public static string LocateDefaultWorkspaceRoot() => LocateWorkspaceRoot();

    public string WorkspaceRoot { get; }

    /// <summary>
    /// A fresh boundary for the next command. A property rather than a field
    /// because each command gets its own job object: the caps are per command, and
    /// closing one must not kill another command's processes.
    /// </summary>
    private Sandboxing.ICodeSandbox _sandbox => Sandboxing.CodeSandbox.ForCurrentPlatform();

    /// <summary>What a command is confined by on this machine, for Engineering to report.</summary>
    public Sandboxing.SandboxCapability Confinement => Sandboxing.CodeSandbox.DescribeCurrentPlatform();

    private string LogDirectory => Path.Combine(WorkspaceRoot, ".concierge-artifacts", "agent-runs");

    private string LogPath => Path.Combine(LogDirectory, "runs.json");

    public IReadOnlyList<ConciergeToolDefinition> GetTools()
    {
        return
        [
            new("read_file", "Read file", "Read a file inside the trusted workspace.", true, false, true, ConciergeToolRisk.Low),
            new("write_file", "Write file", "Create or replace a file only after an explicit approval.", false, true, false, ConciergeToolRisk.High),
            new("shell", "Run command", "Run an allowlisted command without invoking a shell by default.", false, false, false, ConciergeToolRisk.High),
            new("list_files", "List files", "List workspace files through the trusted root.", true, false, true, ConciergeToolRisk.Low),
            new("grep", "Search text", "Search text with ripgrep when available.", true, false, true, ConciergeToolRisk.Low)
        ];
    }

    public IReadOnlyList<ConciergeRunLog> GetRunLogs()
    {
        lock (_gate)
        {
            return _logs.OrderByDescending(log => log.CreatedAt).ToList();
        }
    }

    public async Task<ConciergeToolResult> ReadFileAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var fullPath = ResolveTrustedPath(relativePath);
        if (!File.Exists(fullPath))
        {
            return Result("read_file", ConciergeToolOutcome.Failed, $"File not found: {relativePath}", string.Empty, started);
        }

        var content = await File.ReadAllTextAsync(fullPath, cancellationToken);
        return Result("read_file", ConciergeToolOutcome.Succeeded, $"Read {relativePath}.", content, started);
    }

    /// <summary>
    /// Reads a slice of a file by line, so a large file can be examined without loading it
    /// into a context window that cannot hold it. Offsets before the start are clamped;
    /// a window past the end is empty rather than an error.
    /// </summary>
    public async Task<ConciergeToolResult> ReadFileWindowAsync(string relativePath, int offsetLines, int maxLines, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var fullPath = ResolveTrustedPath(relativePath);
        if (!File.Exists(fullPath))
        {
            return Result("read_file_window", ConciergeToolOutcome.Failed, $"File not found: {relativePath}", string.Empty, started);
        }

        var lines = await File.ReadAllLinesAsync(fullPath, cancellationToken);
        var offset = Math.Clamp(offsetLines, 0, lines.Length);
        var take = Math.Max(0, Math.Min(maxLines, lines.Length - offset));
        // Plain newlines: the window is read by a model and by the diff view, neither of
        // which wants carriage returns, whatever the file itself uses.
        var window = string.Join('\n', lines.Skip(offset).Take(take));
        var summary = $"Read lines {offset + 1}-{offset + take} of {lines.Length} in {relativePath}.";
        return Result("read_file_window", ConciergeToolOutcome.Succeeded, summary, window, started);
    }

    /// <summary>
    /// How many files one listing may name, and how many matches one search may return.
    ///
    /// A repository this size answers "list everything" with tens of thousands of paths,
    /// which is not an answer — it is a context window spent before the work starts. The
    /// cap is stated in the result so a model narrowing its pattern knows it needs to.
    /// </summary>
    private const int MaxListed = 300;

    private const int MaxMatches = 200;

    /// <summary>
    /// Names the files under a folder.
    ///
    /// The gap this closes: the model was given read_file and no way to discover a path,
    /// so it could only open a file somebody had already named to it. The Engineering
    /// room listed a list_files tool for months; there was never one behind it.
    ///
    /// Denied areas are skipped rather than refused. A listing of the repository root
    /// that throws because .git exists is useless, and a person asking what is in a
    /// folder is not asking to be told about the folder they cannot see.
    /// </summary>
    public Task<ConciergeToolResult> ListFilesAsync(
        string? relativePath, string? pattern, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var folder = string.IsNullOrWhiteSpace(relativePath) ? "." : relativePath;

        string root;
        try
        {
            root = ResolveTrustedPath(folder);
        }
        catch (InvalidOperationException problem)
        {
            return Task.FromResult(Result("list_files", ConciergeToolOutcome.Failed, problem.Message, string.Empty, started));
        }

        if (!Directory.Exists(root))
        {
            return Task.FromResult(Result(
                "list_files", ConciergeToolOutcome.Failed, $"Folder not found: {folder}", string.Empty, started));
        }

        var found = new List<string>();
        var truncated = Walk(root, pattern, cancellationToken, MaxListed, path =>
        {
            found.Add(Relative(path));
            return true;
        });

        var summary = truncated
            ? $"First {found.Count} files under {folder} — there are more; narrow the pattern."
            : found.Count == 1 ? $"1 file under {folder}." : $"{found.Count} files under {folder}.";

        return Task.FromResult(Result(
            "list_files", ConciergeToolOutcome.Succeeded, summary, string.Join('\n', found), started));
    }

    /// <summary>
    /// Finds a string across files, with the path and line number of each hit.
    ///
    /// Plain text rather than a regular expression: a model searching a repository is
    /// almost always looking for an identifier, and an accidental regex — a dot, a
    /// bracket, a plus in a symbol name — either matches nothing or matches everything,
    /// with no way to tell which from the result.
    ///
    /// Binary files are skipped by looking for a null byte in the first few kilobytes,
    /// which is what every other tool does and is right often enough.
    /// </summary>
    public Task<ConciergeToolResult> SearchTextAsync(
        string query, string? relativePath, string? pattern, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;

        if (string.IsNullOrEmpty(query))
        {
            return Task.FromResult(Result(
                "search_text", ConciergeToolOutcome.Failed, "Nothing to search for.", string.Empty, started));
        }

        var folder = string.IsNullOrWhiteSpace(relativePath) ? "." : relativePath;

        string root;
        try
        {
            root = ResolveTrustedPath(folder);
        }
        catch (InvalidOperationException problem)
        {
            return Task.FromResult(Result("search_text", ConciergeToolOutcome.Failed, problem.Message, string.Empty, started));
        }

        if (!Directory.Exists(root))
        {
            return Task.FromResult(Result(
                "search_text", ConciergeToolOutcome.Failed, $"Folder not found: {folder}", string.Empty, started));
        }

        var hits = new List<string>();
        var filesWithHits = 0;

        var truncated = Walk(root, pattern, cancellationToken, int.MaxValue, path =>
        {
            if (hits.Count >= MaxMatches)
            {
                return false;
            }

            string[] lines;
            try
            {
                if (LooksBinary(path))
                {
                    return true;
                }

                lines = File.ReadAllLines(path);
            }
            catch (IOException)
            {
                // Locked, or vanished between the walk and the read. One unreadable
                // file is not a reason to fail a search across a thousand others.
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }

            var any = false;

            for (var i = 0; i < lines.Length && hits.Count < MaxMatches; i++)
            {
                if (lines[i].Contains(query, StringComparison.Ordinal))
                {
                    hits.Add($"{Relative(path)}:{i + 1}: {lines[i].Trim()}");
                    any = true;
                }
            }

            if (any)
            {
                filesWithHits++;
            }

            return true;
        });

        var summary = hits.Count == 0
            ? $"No match for \"{query}\" under {folder}."
            : truncated || hits.Count >= MaxMatches
                ? $"First {hits.Count} matches for \"{query}\" in {filesWithHits} files — there are more."
                : $"{hits.Count} matches for \"{query}\" in {filesWithHits} files.";

        return Task.FromResult(Result(
            "search_text", ConciergeToolOutcome.Succeeded, summary, string.Join('\n', hits), started));
    }

    /// <summary>
    /// Walks files under a folder, skipping anything the path guard would refuse and
    /// stopping when the visitor says to or the cap is reached. Returns whether it
    /// stopped early, so the caller can say so rather than silently truncating.
    /// </summary>
    private bool Walk(
        string root, string? pattern, CancellationToken cancellationToken, int cap, Func<string, bool> visit)
    {
        var glob = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern;
        var seen = 0;

        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(root, glob, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                // A symlinked folder can point anywhere, including outside the
                // workspace, and following one would walk straight out of it.
                AttributesToSkip = FileAttributes.ReparsePoint,
            });
        }
        catch (ArgumentException)
        {
            // An unusable pattern. Nothing to walk, and the caller reports a count of 0.
            return false;
        }

        foreach (var path in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsReachable(path))
            {
                continue;
            }

            if (seen >= cap)
            {
                return true;
            }

            seen++;

            if (!visit(path))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a file is one the guard would let a tool open.
    ///
    /// The same rules read_file enforces, asked as a question rather than as an
    /// exception — a listing skips what it cannot show, where a read refuses it. Both
    /// go through <see cref="RejectProtectedPath"/> so there is one answer to "may this
    /// be touched", not two that can drift apart.
    /// </summary>
    private bool IsReachable(string fullPath)
    {
        try
        {
            RejectProtectedPath(Path.GetFullPath(WorkspaceRoot), fullPath);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private string Relative(string fullPath)
        => Path.GetRelativePath(WorkspaceRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// A null byte in the first few kilobytes. Crude, and what every other search tool
    /// does — the alternative is printing a page of a PNG into a conversation.
    /// </summary>
    private static bool LooksBinary(string path)
    {
        using var stream = File.OpenRead(path);

        Span<byte> head = stackalloc byte[4096];
        var read = stream.Read(head);

        return head[..read].IndexOf((byte)0) >= 0;
    }

    /// <summary>
    /// Shows the diff a literal edit would produce, without applying it. Same approval
    /// contract as a write: the person decides against the change, not its description.
    /// </summary>
    public async Task<FileWritePreview> PreviewEditFileAsync(string relativePath, string find, string replace, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveTrustedPath(relativePath);
        var existing = File.Exists(fullPath) ? await File.ReadAllTextAsync(fullPath, cancellationToken) : string.Empty;
        var proposed = ApplyEdit(existing, find, replace, out _);
        return new FileWritePreview(relativePath, proposed ?? existing, BuildDiffPreview(existing, proposed ?? existing), RequiresApproval: true);
    }

    /// <summary>
    /// Replaces one exact occurrence of <paramref name="find"/>. Text that is absent, or
    /// present more than once, fails rather than guessing which line was meant — a wrong
    /// guess corrupts the file silently, and the model can retry with more context.
    /// </summary>
    public async Task<ConciergeToolResult> EditFileAsync(string relativePath, string find, string replace, bool approved, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var fullPath = ResolveTrustedPath(relativePath);
        if (!approved)
        {
            return Result("edit_file", ConciergeToolOutcome.ApprovalRequired, "File edit requires approval.", string.Empty, started);
        }

        if (!File.Exists(fullPath))
        {
            return Result("edit_file", ConciergeToolOutcome.Failed, $"File not found: {relativePath}", string.Empty, started);
        }

        var existing = await File.ReadAllTextAsync(fullPath, cancellationToken);
        var edited = ApplyEdit(existing, find, replace, out var occurrences);
        if (edited is null)
        {
            var reason = occurrences == 0
                ? $"Text to replace was not found in {relativePath}."
                : $"Text to replace appears {occurrences} times in {relativePath}; include more surrounding text to identify one.";
            return Result("edit_file", ConciergeToolOutcome.Failed, reason, string.Empty, started);
        }

        await File.WriteAllTextAsync(fullPath, edited, cancellationToken);
        return Result("edit_file", ConciergeToolOutcome.Succeeded, $"Edited {relativePath}.", string.Empty, started);
    }

    /// <summary>
    /// Applies a literal single-occurrence replacement, or returns null with the occurrence
    /// count when the edit is not uniquely determined.
    /// </summary>
    private static string? ApplyEdit(string content, string find, string replace, out int occurrences)
    {
        occurrences = 0;
        if (string.IsNullOrEmpty(find))
        {
            return null;
        }

        var index = content.IndexOf(find, StringComparison.Ordinal);
        while (index >= 0)
        {
            occurrences++;
            if (occurrences > 1)
            {
                return null;
            }

            index = content.IndexOf(find, index + find.Length, StringComparison.Ordinal);
        }

        if (occurrences != 1)
        {
            return null;
        }

        var at = content.IndexOf(find, StringComparison.Ordinal);
        return string.Concat(content.AsSpan(0, at), replace, content.AsSpan(at + find.Length));
    }

    public async Task<FileWritePreview> PreviewWriteFileAsync(string relativePath, string content, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveTrustedPath(relativePath);
        var existing = File.Exists(fullPath) ? await File.ReadAllTextAsync(fullPath, cancellationToken) : string.Empty;
        return new FileWritePreview(relativePath, content, BuildDiffPreview(existing, content), RequiresApproval: true);
    }

    public async Task<ConciergeToolResult> WriteFileAsync(string relativePath, string content, bool approved, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        if (!approved)
        {
            return Result("write_file", ConciergeToolOutcome.ApprovalRequired, "File write requires approval.", string.Empty, started);
        }

        var fullPath = ResolveTrustedPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content, cancellationToken);
        return Result("write_file", ConciergeToolOutcome.Succeeded, $"Wrote {relativePath}.", string.Empty, started);
    }

    public async Task<ConciergeToolResult> RunCommandAsync(string command, bool approved, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var validation = ValidateCommand(command, approved);
        if (validation.Outcome != ConciergeToolOutcome.Succeeded)
        {
            return Result("shell", validation.Outcome, validation.Message, string.Empty, started);
        }

        var tokens = TokenizeCommand(command);
        var executable = tokens[0];
        var arguments = tokens.Skip(1).ToList();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeouts.TimeoutFor("shell"));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = WorkspaceRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        // A boundary around the command, built fresh for each one.
        //
        // This existed and was never used. Concierge.Shared.Sandboxing had a
        // complete Windows job-object sandbox — memory cap, process cap, and
        // KILL_ON_JOB_CLOSE — referenced nowhere, while the one method in the
        // product that starts a process started it with nothing at all. The
        // Engineering room says "what Concierge can do to your machine" above a
        // list including run_command, and the answer was: whatever it likes.
        //
        // Three things it buys, in the order they matter:
        //
        //   A command that starts something and exits leaves nothing behind. That
        //   is the failure nobody finds until a phone is warm in a pocket, and
        //   killing the process tree on timeout does not cover it — a command that
        //   exits cleanly was never timed out.
        //
        //   A runaway cannot take the machine with it. Capped memory, capped
        //   process count, so a fork bomb from a model that has misunderstood
        //   something costs one failed tool call.
        //
        //   Per command, not per app. One job shared across every command would
        //   apply one process cap to all of them at once and would kill an
        //   unrelated command when this one finished.
        //
        // Unconfined on platforms that cannot do it, which is a real answer rather
        // than a silence: CodeSandbox.ForCurrentPlatform says what it can enforce,
        // and Engineering reports it.
        // Only some sandboxes hold anything to release — the unconfined one holds
        // nothing, and the interface does not require IDisposable because most
        // platforms will not need it. Disposing what does is what closes the job
        // object, and closing the job object is what kills anything left in it.
        var sandbox = _sandbox;

        try
        {
            sandbox.Prepare(process.StartInfo, WorkspaceRoot);

            // Redirection is set above and Prepare must not quietly undo it: the
            // output of the command is the whole point of running it.
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;

            process.Start();
            sandbox.Confine(process);
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = RedactSecrets($"{await outputTask}{await errorTask}");
            var outcome = process.ExitCode == 0 ? ConciergeToolOutcome.Succeeded : ConciergeToolOutcome.Failed;
            return Result("shell", outcome, $"Command exited with {process.ExitCode}.", output, started, process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return Result("shell", ConciergeToolOutcome.Failed, "Command timed out or was cancelled.", string.Empty, started);
        }
        catch (InvalidOperationException problem)
        {
            // The sandbox refused to build or refused to confine. Failing the call
            // is the only honest answer: the alternative is running the command
            // unconfined after deciding it should not be, which is worse than not
            // running it.
            TryKill(process);
            return Result("shell", ConciergeToolOutcome.Failed, problem.Message, string.Empty, started);
        }
        finally
        {
            (sandbox as IDisposable)?.Dispose();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Process may have already exited between the check and the kill.
        }
    }

    public async Task<ConciergeRunLog> RunGoalAsync(string goal, IReadOnlyList<string> commands, bool approved, CancellationToken cancellationToken = default)
    {
        var results = new List<ConciergeToolResult>();
        foreach (var command in commands)
        {
            results.Add(await RunCommandAsync(command, approved, cancellationToken));
            if (results[^1].Outcome is ConciergeToolOutcome.Denied or ConciergeToolOutcome.ApprovalRequired)
            {
                break;
            }
        }

        var log = new ConciergeRunLog($"run-{Guid.NewGuid():N}", goal, results, DateTimeOffset.UtcNow);
        AddLog(log);
        try
        {
            await _publisher.PublishAsync(log, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Publish failures must not break local run accounting. The local log is the source of truth.
        }

        return log;
    }

    private (ConciergeToolOutcome Outcome, string Message) ValidateCommand(string command, bool approved)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return (ConciergeToolOutcome.Denied, "Command is empty.");
        }

        if (HasUnbalancedQuotes(command) || Regex.IsMatch(command.TrimStart(), "^[A-Za-z_][A-Za-z0-9_]*="))
        {
            return (ConciergeToolOutcome.Denied, "Command syntax is not safe for shell-free execution.");
        }

        if (command.Contains('|') || command.Contains("&&", StringComparison.Ordinal) || command.Contains("||", StringComparison.Ordinal) || command.Contains(';'))
        {
            return (ConciergeToolOutcome.Denied, "Shell chaining is blocked.");
        }

        if (DestructiveMarkers.Any(marker => $" {command} ".Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return approved
                ? (ConciergeToolOutcome.Succeeded, "Approved destructive command.")
                : (ConciergeToolOutcome.ApprovalRequired, "Destructive command requires approval.");
        }

        var executable = TokenizeCommand(command).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(executable) || Path.IsPathFullyQualified(executable) || !AllowedExecutables.Contains(executable))
        {
            return (ConciergeToolOutcome.Denied, $"Executable is not allowlisted: {executable}");
        }

        return approved || IsReadOnlyCommand(command)
            ? (ConciergeToolOutcome.Succeeded, "Allowed.")
            : (ConciergeToolOutcome.ApprovalRequired, "Command execution requires approval.");
    }

    private string ResolveTrustedPath(string relativePath)
    {
        if (Path.IsPathFullyQualified(relativePath))
        {
            throw new InvalidOperationException("Use a workspace-relative path.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(WorkspaceRoot, relativePath));
        var root = Path.GetFullPath(WorkspaceRoot);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!fullPath.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Path escapes the trusted workspace.");
        }

        RejectProtectedPath(root, fullPath);
        return fullPath;
    }

    private static void RejectProtectedPath(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Any(segment => DeniedPathSegments.Contains(segment, StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Path targets a protected workspace area.");
        }

        if (IsProtectedFileName(Path.GetFileName(fullPath)))
        {
            throw new InvalidOperationException("Path targets a protected secret or configuration file.");
        }

        var current = new DirectoryInfo(root);
        foreach (var segment in segments.Where(segment => !string.IsNullOrWhiteSpace(segment)))
        {
            current = new DirectoryInfo(Path.Combine(current.FullName, segment));
            if ((Directory.Exists(current.FullName) || File.Exists(current.FullName))
                && (File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
            {
                throw new InvalidOperationException("Path crosses a symlink or reparse point.");
            }
        }
    }

    private static bool IsProtectedFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        if (ProtectedExactFileNames.Contains(fileName))
        {
            return true;
        }

        foreach (var suffix in ProtectedFileSuffixes)
        {
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // .env, .env.local, .env.production, …
        if (fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
            && (fileName.Length == 4 || fileName[4] == '.'))
        {
            return true;
        }

        // appsettings.json, appsettings.Development.json, appsettings.Production.json, …
        if (fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // secrets.json, secrets.production.json, …
        if (fileName.StartsWith("secrets.", StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsReadOnlyCommand(string command)
    {
        var tokens = TokenizeCommand(command);
        if (tokens.Count == 0)
        {
            return false;
        }

        var head = tokens[0];
        if (head.Equals("rg", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (head.Equals("git", StringComparison.OrdinalIgnoreCase) && tokens.Count >= 2)
        {
            var sub = tokens[1];
            return sub.Equals("status", StringComparison.OrdinalIgnoreCase)
                || sub.Equals("diff", StringComparison.OrdinalIgnoreCase)
                || sub.Equals("log", StringComparison.OrdinalIgnoreCase);
        }

        if (head.Equals("dotnet", StringComparison.OrdinalIgnoreCase) && tokens.Count == 2)
        {
            return tokens[1].Equals("--info", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static IReadOnlyList<string> TokenizeCommand(string command)
    {
        var matches = Regex.Matches(command, "\"([^\"]*)\"|'([^']*)'|\\S+");
        return matches.Select(match => match.Value.Trim('"', '\'')).ToList();
    }

    private static bool HasUnbalancedQuotes(string value)
    {
        return value.Count(character => character == '"') % 2 != 0 || value.Count(character => character == '\'') % 2 != 0;
    }

    private static string BuildDiffPreview(string before, string after)
    {
        var beforeLines = string.IsNullOrEmpty(before)
            ? Array.Empty<string>()
            : before.ReplaceLineEndings("\n").Split('\n');
        var afterLines = string.IsNullOrEmpty(after)
            ? Array.Empty<string>()
            : after.ReplaceLineEndings("\n").Split('\n');

        var builder = new StringBuilder();
        builder.AppendLine("--- before");
        builder.AppendLine("+++ after");

        const int maxEmitted = 40;
        var m = beforeLines.Length;
        var n = afterLines.Length;

        // Suffix-LCS table: lcs[i,j] is the LCS length of beforeLines[i..] and afterLines[j..].
        var lcs = new int[m + 1, n + 1];
        for (var i = m - 1; i >= 0; i--)
        {
            for (var j = n - 1; j >= 0; j--)
            {
                lcs[i, j] = string.Equals(beforeLines[i], afterLines[j], StringComparison.Ordinal)
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var emitted = 0;
        var x = 0;
        var y = 0;
        while (x < m && y < n && emitted < maxEmitted)
        {
            if (string.Equals(beforeLines[x], afterLines[y], StringComparison.Ordinal))
            {
                x++;
                y++;
                continue;
            }

            if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                builder.Append("- ").AppendLine(beforeLines[x]);
                x++;
            }
            else
            {
                builder.Append("+ ").AppendLine(afterLines[y]);
                y++;
            }

            emitted++;
        }

        while (x < m && emitted < maxEmitted)
        {
            builder.Append("- ").AppendLine(beforeLines[x++]);
            emitted++;
        }

        while (y < n && emitted < maxEmitted)
        {
            builder.Append("+ ").AppendLine(afterLines[y++]);
            emitted++;
        }

        return builder.ToString();
    }

    // Both expressions use the non-backtracking engine and a hard timeout so untrusted command
    // output (e.g. a long stream of "sk-ant-" repeats) cannot trigger catastrophic backtracking
    // and stall the agent loop.
    private static readonly TimeSpan RedactionTimeout = TimeSpan.FromSeconds(2);

    private static readonly Regex SecretTokenRegex = new(
        @"(sk-ant-|sk-|pk_live_|sk_live_|rk_live_|pk_test_|sk_test_|whsec_|github_pat_|ghp_|gho_|ghu_|ghs_|ghr_|xoxb-|xoxa-|xoxp-|xoxr-|xapp-|AKIA|ASIA|AIza)[A-Za-z0-9_\-]{8,}",
        RegexOptions.Compiled | RegexOptions.NonBacktracking,
        RedactionTimeout);

    private static readonly Regex PemBlockRegex = new(
        @"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----",
        RegexOptions.Compiled | RegexOptions.NonBacktracking,
        RedactionTimeout);

    private static string RedactSecrets(string value)
    {
        try
        {
            var redacted = SecretTokenRegex.Replace(value, "$1[REDACTED]");
            return PemBlockRegex.Replace(redacted, "-----BEGIN PRIVATE KEY-----[REDACTED]-----END PRIVATE KEY-----");
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathological input took too long to scan — drop the body rather than leak whatever
            // partial match we have or stall the harness.
            return "[REDACTED: secret-scan timeout]";
        }
    }

    private ConciergeToolResult Result(string tool, ConciergeToolOutcome outcome, string summary, string output, DateTimeOffset started, int? exitCode = null)
    {
        return new ConciergeToolResult(tool, outcome, summary, output, started, DateTimeOffset.UtcNow, exitCode);
    }

    private void AddLog(ConciergeRunLog log)
    {
        lock (_gate)
        {
            _logs.Add(log);
            File.WriteAllText(LogPath, JsonSerializer.Serialize(_logs, JsonOptions));
        }
    }

    private IReadOnlyList<ConciergeRunLog> LoadLogs()
    {
        if (!File.Exists(LogPath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ConciergeRunLog>>(File.ReadAllText(LogPath), JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string LocateWorkspaceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Concierge.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
