using System.Diagnostics;
using System.Text.Json;
using Concierge.Shared.Tools;

namespace Concierge.DeveloperStudio;

/// <summary>Embeds the separately built Concierge Code host through its versioned process contract.</summary>
internal sealed class ExternalDeveloperStudio : IDeveloperStudio, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _host;
    private ContentPage? _editorPage;
    private Uri? _url;
    private Guid? _projectId;
    private string? _workspaceKind;
    public event EventHandler? Closed;
    public event EventHandler? AskCircleAiRequested;

    internal static string? ExecutablePath
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("CONCIERGE_CODE_PATH");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
            var packaged = Path.Combine(AppContext.BaseDirectory, "ConciergeCode", "ConciergeCode.exe");
            return File.Exists(packaged) ? packaged : null;
        }
    }

    public static bool IsAvailable => OperatingSystem.IsWindows() && ExecutablePath is not null;

    public async Task<IReadOnlySet<string>> GetSupportedWorkspaceKindsAsync(CancellationToken cancellationToken = default)
    {
        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "code" };
        if (ExecutablePath is not { } executable) return supported;
        try
        {
            var json = await RunAsync(executable, cancellationToken, "capabilities");
            using var capabilities = JsonDocument.Parse(json);
            if (capabilities.RootElement.GetProperty("version").GetInt32() != 1) return supported;
            foreach (var workspace in capabilities.RootElement.GetProperty("workspaces").EnumerateArray())
            {
                var kind = workspace.GetProperty("kind").GetString();
                if (ConciergeCodeWorkspaces.Find(kind) is { } known && known.Id != "code" &&
                    workspace.GetProperty("command").GetString() == "studio")
                    supported.Add(known.Id);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { /* An older host can still open Code. */ }
        return supported;
    }

    public Task<Uri> OpenAsync(Guid projectId, string projectName, CancellationToken cancellationToken = default)
        => OpenWorkspaceAsync(projectId, projectName, "code", cancellationToken);

    public async Task<Uri> OpenWorkspaceAsync(Guid projectId, string projectName, string workspaceKind, CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty) throw new ArgumentException("Select a Project first.", nameof(projectId));
        var kind = ConciergeCodeWorkspaces.Find(workspaceKind)
            ?? throw new ArgumentException("Unknown Concierge Code workspace.", nameof(workspaceKind));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_projectId == projectId && _workspaceKind == kind.Id && _url is not null && _host is { HasExited: false })
            {
                if (_editorPage is null) await ShowEditorAsync(_url, projectName);
                return _url;
            }
            await StopHostAsync();
            var executable = ExecutablePath ?? throw new FileNotFoundException("Install Concierge Code to open this Project.");
            ProcessStartInfo start;
            if (kind.Id == "code")
            {
                var folder = Path.Combine(FileSystem.AppDataDirectory, "projects", projectId.ToString("N"), "source");
                Directory.CreateDirectory(folder);
                var readme = Path.Combine(folder, "README.md");
                if (!File.Exists(readme))
                    await File.WriteAllTextAsync(readme, $"# {projectName.Trim()}\n\nSource files for this Concierge Project.\n", cancellationToken);
                start = NewProcess(executable, "serve", "--workspace", folder, "--data-dir", DataDir, "--no-browser");
            }
            else
                start = NewProcess(executable, "studio", "--project", projectId.ToString("D"), "--kind", kind.Id,
                    "--data-dir", FileSystem.AppDataDirectory, "--no-browser");
            _host = Process.Start(start) ?? throw new InvalidOperationException("Concierge Code did not start.");
            using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupTimeout.CancelAfter(TimeSpan.FromMinutes(2));
            var readyLine = await _host.StandardOutput.ReadLineAsync(startupTimeout.Token);
            if (readyLine is null)
                throw new InvalidOperationException($"Concierge Code stopped: {await _host.StandardError.ReadToEndAsync(cancellationToken)}");
            using var ready = JsonDocument.Parse(readyLine);
            var response = ready.RootElement;
            if (response.GetProperty("version").GetInt32() != 1 || response.GetProperty("event").GetString() != "ready")
                throw new InvalidOperationException("Concierge Code returned an unsupported contract.");
            var port = response.GetProperty("port").GetInt32();
            var tokenFile = Path.GetFullPath(response.GetProperty("tokenFile").GetString() ?? "");
            var expectedTokenFolder = Path.GetFullPath(Path.Combine(FileSystem.AppDataDirectory, "projects", projectId.ToString("D"), "studio", "sessions"));
            var expectedCodeToken = Path.GetFullPath(Path.Combine(DataDir, "connection.token"));
            var tokenIsExpected = kind.Id == "code"
                ? string.Equals(tokenFile, expectedCodeToken, StringComparison.OrdinalIgnoreCase)
                : string.Equals(Path.GetDirectoryName(tokenFile), expectedTokenFolder, StringComparison.OrdinalIgnoreCase)
                  && Path.GetExtension(tokenFile).Equals(".token", StringComparison.OrdinalIgnoreCase);
            if (port is < 1 or > 65535 || !tokenIsExpected)
                throw new InvalidOperationException("Concierge Code returned invalid connection details.");
            var token = (await File.ReadAllTextAsync(tokenFile, cancellationToken)).Trim();
            if (token.Length != 64 || !token.All(Uri.IsHexDigit))
                throw new InvalidOperationException("Concierge Code returned an invalid token.");
            var route = kind.Id == "code" ? "/" : response.GetProperty("route").GetString();
            var expectedOutputRoot = Path.GetFullPath(Path.Combine(FileSystem.AppDataDirectory, "projects", projectId.ToString("D"), "studio", "outputs"));
            if (kind.Id != "code" &&
                (!string.Equals(response.GetProperty("kind").GetString(), kind.Id, StringComparison.Ordinal) ||
                 !string.Equals(route, $"/?kind={kind.Id}", StringComparison.Ordinal) ||
                 response.GetProperty("project").GetGuid() != projectId ||
                 !string.Equals(Path.GetFullPath(response.GetProperty("outputRoot").GetString() ?? ""),
                     expectedOutputRoot, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Concierge Code returned the wrong workspace.");
            _url = new Uri($"http://127.0.0.1:{port}{route}{(route!.Contains('?') ? "&" : "?")}tkn={token}");
            _projectId = projectId;
            _workspaceKind = kind.Id;
            await ShowEditorAsync(_url, $"{projectName} · {kind.Title}");
            return _url;
        }
        catch
        {
            await StopHostAsync();
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlySet<string>> GetInstalledExtensionsAsync(CancellationToken cancellationToken = default)
    {
        if (ExecutablePath is not { } executable)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = await RunAsync(executable, cancellationToken, "extensions", "--data-dir", DataDir);
        return (JsonSerializer.Deserialize<string[]>(result) ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task InstallSetupAsync(string setupId, CancellationToken cancellationToken = default)
    {
        _ = ConciergeCodeSetups.Find(setupId);
        var executable = ExecutablePath ?? throw new FileNotFoundException("Install Concierge Code to add coding tools.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_host is { HasExited: false }) throw new InvalidOperationException("Close Concierge Code before installing extensions.");
            await RunAsync(executable, cancellationToken, "install", "--setup", setupId, "--data-dir", DataDir);
        }
        finally { _gate.Release(); }
    }

    private static string DataDir => Path.Combine(FileSystem.AppDataDirectory, "developer-studio");

    private static ProcessStartInfo NewProcess(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    private static async Task<string> RunAsync(string executable, CancellationToken cancellationToken, params string[] arguments)
    {
        using var process = Process.Start(NewProcess(executable, arguments))
            ?? throw new InvalidOperationException("Concierge Code could not start.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Concierge Code failed: {(await error).Trim()}");
        return await output;
    }

    private async Task ShowEditorAsync(Uri url, string projectName)
    {
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var parent = Application.Current?.Windows.FirstOrDefault()?.Page
                ?? throw new InvalidOperationException("Concierge's window is unavailable.");
            var back = new Button { Text = "← Project", TextColor = Colors.White, BackgroundColor = Colors.Transparent };
            back.Clicked += async (_, _) => await CloseAsync();
            var title = new Label
            {
                Text = projectName, TextColor = Colors.White, FontSize = 18,
                FontAttributes = FontAttributes.Bold, VerticalTextAlignment = TextAlignment.Center,
                LineBreakMode = LineBreakMode.TailTruncation,
            };
            var askB = new Button
            {
                Text = "Ask B!", TextColor = Colors.White, CornerRadius = 14,
                BackgroundColor = Color.FromRgba(255, 255, 255, 0.16), MinimumHeightRequest = 44,
            };
            askB.Clicked += async (_, _) =>
            {
                await CloseAsync();
                AskCircleAiRequested?.Invoke(this, EventArgs.Empty);
            };
            var toolbar = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) },
                ColumnSpacing = 12, Padding = new Thickness(12, 6), BackgroundColor = Color.FromArgb("#005EA8"),
            };
            Grid.SetColumn(title, 1);
            Grid.SetColumn(askB, 2);
            toolbar.Children.Add(back);
            toolbar.Children.Add(title);
            toolbar.Children.Add(askB);
            var layout = new Grid { RowDefinitions = new RowDefinitionCollection { new(GridLength.Auto), new(GridLength.Star) } };
            layout.Children.Add(toolbar);
            var browser = new WebView { Source = url.ToString() };
            Grid.SetRow(browser, 1);
            layout.Children.Add(browser);
            _editorPage = new ContentPage { Content = layout, Title = "Concierge Code" };
            await parent.Navigation.PushModalAsync(_editorPage);
        });
    }

    public async Task CloseAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_editorPage is { } page)
            {
                _editorPage = null;
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    var parent = Application.Current?.Windows.FirstOrDefault()?.Page;
                    if (parent?.Navigation.ModalStack.LastOrDefault() == page)
                        await parent.Navigation.PopModalAsync();
                });
            }
            await StopHostAsync();
            Closed?.Invoke(this, EventArgs.Empty);
        }
        finally { _gate.Release(); }
    }

    private async Task StopHostAsync()
    {
        if (_host is { HasExited: false } process)
        {
            try
            {
                await process.StandardInput.WriteLineAsync("stop");
                await process.StandardInput.FlushAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); }
            catch (IOException) { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }
        _host?.Dispose();
        _host = null;
        _url = null;
        _projectId = null;
        _workspaceKind = null;
    }

    public async ValueTask DisposeAsync() => await CloseAsync();
}
