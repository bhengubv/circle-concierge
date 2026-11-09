using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Concierge.Shared.Tools;

namespace Concierge.DeveloperStudio;

/// <summary>Runs a token-protected VS Code workbench on the Windows loopback interface via WSL.</summary>
internal sealed class WindowsDeveloperStudio : IDeveloperStudio, IAsyncDisposable
{
    public event EventHandler? Closed;
    private const string Version = "openvscode-server-v1.109.5";
    private const string ArchiveSha256 = "b433bf4f0227321a7014d8460d10a8f958adc0f45aa79bd889e84e65e8f88363";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false });
    private Process? _server;
    private Guid? _projectId;
    private Uri? _url;
    private string? _tokenFile;
    private string? _pidFile;
    private string? _wslTokenFile;
    private string? _wslPidFile;
    private ContentPage? _editorPage;

    public async Task<Uri> OpenAsync(Guid projectId, string projectName, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Developer Studio currently needs Windows with WSL.");
        if (projectId == Guid.Empty)
            throw new ArgumentException("Select a Project first.", nameof(projectId));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_projectId == projectId && _url is not null && _server?.HasExited == false)
            {
                if (_editorPage is null) await ShowEditorAsync(_url, projectName);
                return _url;
            }
            await StopServerAsync();

            var folder = Path.Combine(FileSystem.AppDataDirectory, "projects", projectId.ToString("N"), "source");
            Directory.CreateDirectory(folder);
            var readme = Path.Combine(folder, "README.md");
            if (!File.Exists(readme))
                await File.WriteAllTextAsync(readme, $"# {projectName.Trim()}\n\nSource files for this Concierge Project.\n", cancellationToken);

            await EnsureInstalledAsync(cancellationToken);
            var workspace = (await RunWslAsync(["-e", "wslpath", "-a", folder], cancellationToken, "workspace path")).Trim();
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            _tokenFile = Path.Combine(FileSystem.AppDataDirectory, "developer-studio.token");
            _pidFile = Path.Combine(FileSystem.AppDataDirectory, "developer-studio.pid");
            await File.WriteAllTextAsync(_tokenFile, token + "\n", cancellationToken);
            var wslTokenFile = (await RunWslAsync(["-e", "wslpath", "-a", _tokenFile], cancellationToken, "token path")).Trim();
            var wslPidFile = (await RunWslAsync(["-e", "wslpath", "-a", _pidFile], cancellationToken, "process path")).Trim();
            _wslTokenFile = wslTokenFile;
            _wslPidFile = wslPidFile;
            await CleanUpRecordedServerAsync(cancellationToken);

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var command = $"root=\"$HOME/.local/share/concierge/developer-studio/{Version}-linux-x64\"; echo $$ > \"$4\"; exec \"$root/node\" \"$root/out/server-main.js\" --host 127.0.0.1 --port \"$1\" --connection-token-file \"$2\" --disable-telemetry --folder \"$3\"";
            var info = new ProcessStartInfo("wsl.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in new[] { "-e", "bash", "-lc", command, "--", port.ToString(), wslTokenFile, workspace, wslPidFile })
                info.ArgumentList.Add(arg);
            _server = Process.Start(info) ?? throw new InvalidOperationException("Developer Studio could not start.");
            _server.OutputDataReceived += (_, _) => { }; // The server prints its secret URL; never log it.
            _server.ErrorDataReceived += (_, _) => { };
            _server.BeginOutputReadLine();
            _server.BeginErrorReadLine();

            var url = new Uri($"http://127.0.0.1:{port}/?folder={Uri.EscapeDataString(workspace)}&tkn={token}");
            for (var attempt = 0; attempt < 60; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_server.HasExited)
                    throw new InvalidOperationException("Developer Studio stopped before it was ready.");
                try
                {
                    using var response = await _http.GetAsync(url, cancellationToken);
                    if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK)
                    {
                        _projectId = projectId;
                        _url = url;
                        await ShowEditorAsync(url, projectName);
                        return url;
                    }
                }
                catch (HttpRequestException) { }
                await Task.Delay(500, cancellationToken);
            }
            throw new TimeoutException("Developer Studio did not become ready within 30 seconds.");
        }
        catch
        {
            await StopServerAsync();
            throw;
        }
        finally { _gate.Release(); }
    }

    private static async Task EnsureInstalledAsync(CancellationToken cancellationToken)
    {
        // The optional runtime is downloaded only when the person opens the editor.
        // Verify the pinned release before extracting; no upstream source is copied into Concierge.
        var script = $"""
            set -euo pipefail
            if [ "$(uname -m)" != "x86_64" ]; then echo "Developer Studio currently requires x64 WSL." >&2; exit 1; fi
            root="$HOME/.local/share/concierge/developer-studio"
            binary="$root/{Version}-linux-x64/bin/openvscode-server"
            if [ -x "$binary" ]; then exit 0; fi
            mkdir -p "$root"
            archive="$root/{Version}.tar.gz"
            curl -fsSL --retry 2 -o "$archive" "https://github.com/gitpod-io/openvscode-server/releases/download/{Version}/{Version}-linux-x64.tar.gz"
            echo "{ArchiveSha256}  $archive" | sha256sum -c - >/dev/null
            tar -xzf "$archive" -C "$root"
            rm "$archive"
            test -x "$binary"
            """;
        await RunWslAsync(["-e", "bash", "-s"], cancellationToken, "runtime installation", script);
    }

    private static async Task<string> RunWslAsync(IEnumerable<string> arguments, CancellationToken cancellationToken, string step, string? standardInput = null)
    {
        var info = new ProcessStartInfo("wsl.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("WSL could not start.");
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
            process.StandardInput.Close();
        }
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Developer Studio {step} failed (exit {process.ExitCode}): {(await error).Trim()} {(await output).Trim()}");
        return await output;
    }

    public async Task CloseAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_editorPage is not null)
            {
                var page = _editorPage;
                _editorPage = null;
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    var host = Application.Current?.Windows.FirstOrDefault()?.Page;
                    if (host?.Navigation.ModalStack.LastOrDefault() == page)
                        await host.Navigation.PopModalAsync();
                });
            }
            await StopServerAsync();
            Closed?.Invoke(this, EventArgs.Empty);
        }
        finally { _gate.Release(); }
    }

    private async Task ShowEditorAsync(Uri url, string projectName)
    {
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var host = Application.Current?.Windows.FirstOrDefault()?.Page
                ?? throw new InvalidOperationException("Concierge's window is unavailable.");
            var back = new Button
            {
                Text = "← Project",
                HorizontalOptions = LayoutOptions.Start,
                BackgroundColor = Colors.Transparent,
                TextColor = Colors.White,
            };
            back.Clicked += async (_, _) => await CloseAsync();
            var title = new Label
            {
                Text = projectName,
                FontSize = 18,
                FontAttributes = FontAttributes.Bold,
                VerticalTextAlignment = TextAlignment.Center,
                LineBreakMode = LineBreakMode.TailTruncation,
                TextColor = Colors.White,
            };
            var toolbar = new HorizontalStackLayout
            {
                Spacing = 12,
                Padding = new Thickness(12, 6),
                BackgroundColor = Color.FromArgb("#087CC1"),
            };
            toolbar.Children.Add(back);
            toolbar.Children.Add(title);
            var layout = new Grid { RowDefinitions = new RowDefinitionCollection { new(GridLength.Auto), new(GridLength.Star) } };
            layout.Children.Add(toolbar);
            var browser = new WebView { Source = url.ToString() };
            Grid.SetRow(browser, 1);
            layout.Children.Add(browser);
            _editorPage = new ContentPage { Content = layout, Title = "Developer Studio" };
            await host.Navigation.PushModalAsync(_editorPage);
        });
    }

    private async Task StopServerAsync()
    {
        if (_server is { HasExited: false })
            _server.Kill(entireProcessTree: true);
        _server?.Dispose();
        _server = null;
        _projectId = null;
        _url = null;
        try { await CleanUpRecordedServerAsync(CancellationToken.None); }
        catch { /* WSL may already be stopped; the PID file is checked on the next launch. */ }
        if (_tokenFile is not null && File.Exists(_tokenFile)) File.Delete(_tokenFile);
        _tokenFile = null;
        _pidFile = null;
        _wslTokenFile = null;
        _wslPidFile = null;
    }

    private async Task CleanUpRecordedServerAsync(CancellationToken cancellationToken)
    {
        if (_wslPidFile is null || _wslTokenFile is null) return;
        const string script = """
            pid=$(cat "$1" 2>/dev/null || true)
            if [[ "$pid" =~ ^[0-9]+$ ]] && [ -r "/proc/$pid/cmdline" ] &&
               tr '\0' ' ' < "/proc/$pid/cmdline" | grep -Fq -- "$2"; then
                kill "$pid" 2>/dev/null || true
            fi
            rm -f "$1"
            """;
        await RunWslAsync(["-e", "bash", "-s", "--", _wslPidFile, _wslTokenFile], cancellationToken,
            "old editor cleanup", script);
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _http.Dispose();
        _gate.Dispose();
    }
}
