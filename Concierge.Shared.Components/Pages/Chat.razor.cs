// Code-behind for Chat. The markup this came from was deleted: none of the
// screens looked anything like the design they are meant to look like, so the
// UI is being rebuilt rather than edited. This is the logic that survived.
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using System.Text;
using System.Text.Json.Nodes;
using Concierge.Shared.Chat;
using Concierge.Shared.Tools;

namespace Concierge.Shared.Components.Pages;

public partial class Chat
{

    [Parameter] public Guid? ConversationId { get; set; }

    // Was a constant. The right ceiling differs between a phone on battery and a desktop,
    // and Concierge already varies the model by device state, so this belongs with the
    // other budgets rather than nailed to the page.
    private int MaxToolIterations => LoopOptions.MaxToolIterations;
    private const long MaxAttachmentBytes = 256 * 1024; // 256 KB — text-only attachments in v1
    private const string OwnerId = "local";

    private List<Conversation> _conversations = new();
    private Conversation? _active;
    private string _composerText = string.Empty;
    private bool _streaming;
    private string _streamingBuffer = string.Empty;
    private CancellationTokenSource? _streamCts;
    private int _toolLoopIteration;

    private List<IChatRuntime> _orderedRuntimes = new();
    private IChatRuntime? _activeRuntime;
    private string _systemPromptDraft = string.Empty;
    private bool _includeToolCatalog = true;
    private bool _executeToolCalls = true;

    private readonly List<TextAttachment> _pendingAttachments = new();
    private bool _recording;

    /// <summary>
    /// Currently-activated skill ids for this conversation. Their SKILL.md
    /// bodies get prepended to the system prompt on every send. Stack any
    /// number — Concierge composes them in <c>ISkillRuntime.ComposeSystemPrompt</c>.
    /// </summary>
    private readonly HashSet<string> _activeSkillIds = new(StringComparer.OrdinalIgnoreCase);
    // ── Model download ─────────────────────────────────────────────────
    // The on-device engine reports a missing model rather than fetching it; these carry the
    // person's answer back. Runtimes that ship with their model implement none of this and
    // PendingModel stays null.

    private bool _downloading;
    private double _downloadProgress;
    private string? _downloadDetail;
    private CancellationTokenSource? _downloadCancellation;
    private bool _gone;

    private PendingModelDownload? PendingModel
        => (_activeRuntime as IModelDownloadRequired)?.PendingDownload;

    private async Task AcceptDownload()
    {
        if (_activeRuntime is not IModelDownloadRequired runtime || _downloading || _gone)
        {
            return;
        }

        var stopping = new CancellationTokenSource();

        _downloading = true;
        _downloadProgress = 0;
        _downloadDetail = null;
        _downloadCancellation = stopping;

        // Progress<T> posts its callbacks, so these arrive off the renderer — InvokeAsync, not
        // a direct StateHasChanged. They can also arrive after the page is gone, which is what
        // _gone is for: rendering a disposed component throws.
        var progress = new Progress<ModelDownloadProgress>(report =>
        {
            if (_gone)
            {
                return;
            }

            _downloadProgress = report.Ratio;
            _downloadDetail = report.Description;
            _ = InvokeAsync(StateHasChanged);
        });

        try
        {
            await runtime.AcceptDownloadAsync(progress, stopping.Token);
        }
        finally
        {
            _downloading = false;
            _downloadCancellation = null;

            // Disposed only after it is unreachable — CancelDownload runs on the renderer, but
            // cancelling a disposed source throws, and the earlier version disposed it here
            // while the field still pointed at it.
            stopping.Dispose();

            if (!_gone)
            {
                StateHasChanged();
            }
        }
    }

    private void CancelDownload()
    {
        // Not disposed here: whoever started it owns it and disposes it when it returns.
        try
        {
            _downloadCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It finished between the click and this line.
        }
    }

    /// <summary>
    /// Leaving the page stops a download that is running. It is not background work the person
    /// asked for anywhere else, and left alone it holds this component alive through the
    /// progress callback for as long as the transfer takes.
    /// </summary>
    public void Dispose()
    {
        _gone = true;
        Nav.LocationChanged -= OnLocationChanged;
        CancelDownload();

        // A generation still streaming when the page goes away used to be left
        // running: the component it wrote into is gone, and the native MNN call
        // keeps going on a pool thread -- which faulted with an access violation
        // in mnn_llm_generate_stream_text and took the process with it. Hard to
        // reach while every destination sat behind a menu; with a tab bar,
        // leaving mid-answer is one tap.
        //
        // Cancelling is the renderer's responsibility either way. It is not on
        // its own a guarantee: the token is only observed between fragments, so
        // a fault inside the blocking P/Invoke is still possible and the native
        // handle's lifetime is CircleAI.Inference's to fix.
        try
        {
            _streamCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down by the streaming path. Nothing to stop.
        }
        finally
        {
            _streamCts?.Dispose();
            _streamCts = null;
        }
    }

    private bool _showSkillPicker;
    private string _skillFilter = string.Empty;

    // Named the two icons sitting right beside it and pointed at a emoji that
    // is no longer there. The field still doubles as the status line for
    // attachments and transcription; only the resting text changed.
    private string _composerHint = "Shift + Enter for a new line";

    // _active is deliberately not required. Sending from the empty screen
    // creates the conversation; requiring one to exist first is what forced a
    // separate "Start a chat" button into the design.
    private bool CanSend
        => !_streaming
           && (!string.IsNullOrWhiteSpace(_composerText) || _pendingAttachments.Count > 0)
           && (_activeRuntime?.IsReady ?? false);

    protected override async Task OnInitializedAsync()
    {
        _orderedRuntimes = Runtimes
            .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            // CircleAI first by construction — it's the philosophy of the
            // product (local-first, private, no API key needed). Ready ones
            // next, then alphabetical for stable order.
            .OrderByDescending(r => string.Equals(r.Id, DefaultProviderId, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(r => r.IsReady)
            .ThenBy(r => r.EngineLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _activeRuntime = await ChooseActiveRuntimeAsync();

        await RefreshSidebarAsync();

        // Home → Chat handoff:
        //   ?q=...     — prefill the composer with the typed prompt and send.
        //   ?image=1   — pull the captured base64 JPEG from sessionStorage,
        //                wrap as an attachment, send "What is this?" + image.
        //   ?voice=1   — voice was captured on Home; transcript will be in
        //                sessionStorage already (the Home mic flow stores it).
        //   ?demo=1    — script a single "show me what you can do" turn.
        // LocationChanged, not OnParametersSetAsync. This component owns both
        // "/" and "/chat", and moving between two @page routes on the SAME
        // component does not re-run OnInitializedAsync and — verified on the
        // running app — does not reliably re-run OnParametersSetAsync either.
        // The composer stayed empty and no conversation was created, so every
        // query-string handoff was dropped. LocationChanged always fires.
        Nav.LocationChanged += OnLocationChanged;

        await ConsumeHomeHandoffAsync();
    }

    private void OnLocationChanged(object? sender, Microsoft.AspNetCore.Components.Routing.LocationChangedEventArgs e)
        => _ = InvokeAsync(async () =>
        {
            await ConsumeHomeHandoffAsync();
            StateHasChanged();
        });

    /// <summary>The query string most recently acted on. Without this the same
    /// handoff fires repeatedly, because OnParametersSetAsync runs on every
    /// parameter change and SendAsync causes re-renders of its own.</summary>
    private string? _handledHandoff;

    private async Task ConsumeHomeHandoffAsync()
    {
        try
        {
            var uri = new Uri(Nav.Uri);
            if (string.IsNullOrEmpty(uri.Query) || string.Equals(uri.Query, _handledHandoff, StringComparison.Ordinal))
            {
                return;
            }

            // Claimed before anything is awaited: SendAsync below re-renders,
            // which comes back through here.
            _handledHandoff = uri.Query;

            var q = System.Web.HttpUtility.ParseQueryString(uri.Query);

            var prefill = q["q"];
            if (!string.IsNullOrWhiteSpace(prefill))
            {
                _active ??= await Store.StartAsync(OwnerId);
                _composerText = prefill;
                StateHasChanged();
                if (_activeRuntime?.IsReady == true)
                {
                    // Runtime is ready — send immediately, normal flow.
                    await SendAsync();
                }
                else
                {
                    // Runtime is still warming up (CircleAI loading the
                    // local model on first launch). Save the user's prompt
                    // to the conversation NOW so it doesn't disappear, and
                    // let the user see it. When they hit Send again — or as
                    // soon as the runtime becomes ready — the response flow
                    // takes over. Without this guard the entire prompt is
                    // silently dropped because CanSend gates on IsReady.
                    await Store.AppendEventAsync(_active.Id, ConversationEventType.UserMessage, prefill);
                    _active = await Store.GetAsync(_active.Id);
                    _composerText = string.Empty;
                    await RefreshSidebarAsync();
                    StateHasChanged();
                }
                return;
            }

            if (q["image"] == "1")
            {
                var b64 = await JS.InvokeAsync<string?>("sessionStorage.getItem", "concierge-pending-image");
                await JS.InvokeVoidAsync("sessionStorage.removeItem", "concierge-pending-image");
                if (!string.IsNullOrEmpty(b64))
                {
                    _active ??= await Store.StartAsync(OwnerId, title: "Picture chat");
                    var bytes = Convert.FromBase64String(b64);
                    _pendingAttachments.Add(new TextAttachment(
                        $"picture-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jpg",
                        bytes));
                    _composerText = "What's in this picture?";
                    StateHasChanged();
                    await SendAsync();
                }
                return;
            }

            if (q["demo"] == "1")
            {
                _active ??= await Store.StartAsync(OwnerId, title: "A quick demo");
                _composerText = "Tell me a tiny 3-line haiku about a friendly bellhop helping a guest in a hotel lobby.";
                StateHasChanged();
                await SendAsync();
                return;
            }
        }
        catch
        {
            // Handoff is best-effort — never block the chat from rendering.
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (ConversationId is { } id)
        {
            // Before showing the conversation: if the last generation was cut
            // off, its partial answer is still on disk. Put it back.
            await RecoverDraftAsync(id);

            _active = await Store.GetAsync(id);
            _systemPromptDraft = _active?.SystemPrompt ?? string.Empty;
        }
        else
        {
            _active = null;
            _systemPromptDraft = string.Empty;
        }

        // The handoff also runs here, not only on first initialisation.
        //
        // This component now owns "/" as well as "/chat", so moving between
        // them is a parameter change rather than a new component — and
        // OnInitializedAsync does not run a second time. Everything that
        // arrives by query string (?q=, ?voice=1, ?image=1, ?demo=1) was
        // therefore silently dropped for anyone already on the workspace,
        // which is every case now that the workspace is the front door.
        await ConsumeHomeHandoffAsync();
    }

    private async Task SaveSystemPromptAsync()
    {
        if (_active is null)
        {
            return;
        }
        _active.SystemPrompt = _systemPromptDraft;
        await Task.CompletedTask;
    }

    private void ToggleSkillPicker() => _showSkillPicker = !_showSkillPicker;

    private void ToggleSkill(string id)
    {
        if (!_activeSkillIds.Add(id))
        {
            _activeSkillIds.Remove(id);
        }
    }

    private void DeactivateSkill(string id) => _activeSkillIds.Remove(id);

    // The product's local-first identity: CircleAI is the default LLM.
    // Cloud providers exist as escape hatches when the user explicitly
    // picks one in Settings or here. Saved choice lives in localStorage
    // under this key so the page remembers across launches.
    private const string DefaultProviderId = "circleai";
    private const string ProviderPreferenceStorageKey = "concierge-provider-id";

    private async Task<IChatRuntime?> ChooseActiveRuntimeAsync()
    {
        // 1. User's saved preference (set in Settings or via the picker
        //    on this page). Only honor it if the runtime is actually
        //    registered — otherwise the dropdown shows a phantom option.
        string? savedId = null;
        try
        {
            savedId = await JS.InvokeAsync<string?>("localStorage.getItem", ProviderPreferenceStorageKey);
        }
        catch { /* localStorage may not be available during prerender. */ }

        if (!string.IsNullOrWhiteSpace(savedId))
        {
            var saved = _orderedRuntimes.FirstOrDefault(r => string.Equals(r.Id, savedId, StringComparison.OrdinalIgnoreCase));
            if (saved is not null) return saved;
        }

        // 2. CircleAI — the philosophical default. We don't gate on IsReady
        //    here because CircleAI loads asynchronously in the background;
        //    showing it as the picked engine (with its own "loading…" status)
        //    is more honest than silently demoting to a cloud provider.
        var circleai = _orderedRuntimes.FirstOrDefault(r => string.Equals(r.Id, DefaultProviderId, StringComparison.OrdinalIgnoreCase));
        if (circleai is not null) return circleai;

        // 3. Whatever is actually ready.
        return _orderedRuntimes.FirstOrDefault(r => r.IsReady)
            ?? _orderedRuntimes.FirstOrDefault();
    }

    private async Task OnProviderChanged(ChangeEventArgs args)
    {
        var id = args.Value?.ToString();
        if (string.IsNullOrEmpty(id))
        {
            return;
        }
        _activeRuntime = _orderedRuntimes.FirstOrDefault(r => r.Id == id) ?? _activeRuntime;
        // Persist so the next launch lands on the same provider. Settings
        // page reads/writes the same key.
        try
        {
            await JS.InvokeVoidAsync("localStorage.setItem", ProviderPreferenceStorageKey, id);
        }
        catch { /* best-effort */ }
    }

    private async Task RefreshSidebarAsync()
    {
        _conversations = (await Store.ListAsync(OwnerId)).ToList();
    }

    private async Task StartNewAsync()
    {
        var conversation = await Store.StartAsync(OwnerId);
        await RefreshSidebarAsync();
        Nav.NavigateTo($"chat/{conversation.Id}");
    }

    private async Task OnAttachmentSelected(InputFileChangeEventArgs args)
    {
        foreach (var file in args.GetMultipleFiles(8))
        {
            if (file.Size > MaxAttachmentBytes)
            {
                _composerHint = $"{file.Name} skipped — over {MaxAttachmentBytes / 1024} KB. Text attachments only in v1.";
                continue;
            }

            using var stream = file.OpenReadStream(MaxAttachmentBytes);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            _pendingAttachments.Add(new TextAttachment(file.Name, ms.ToArray()));
            _composerHint = "Attachment ready. Text contents will quote into the next message.";
        }
    }

    private void RemoveAttachment(TextAttachment attachment)
    {
        _pendingAttachments.Remove(attachment);
        _composerHint = _pendingAttachments.Count == 0
            ? "Shift + Enter for newline · drag a text file or use 🎙 for voice."
            : $"{_pendingAttachments.Count} attachment(s) ready.";
    }

    /// <summary>
    /// Toggles mic capture via the JS interop module that wraps MediaRecorder. The module
    /// returns a base64 webm blob which we POST to <c>/api/voice/transcribe</c>; the
    /// resulting text is appended to the composer so the user can edit before send.
    /// </summary>
    private async Task ToggleMicAsync()
    {
        try
        {
            if (!_recording)
            {
                await JS.InvokeVoidAsync("conciergeVoice.start");
                _recording = true;
                _composerHint = "Recording… tap the mic again to stop.";
            }
            else
            {
                var base64 = await JS.InvokeAsync<string?>("conciergeVoice.stop");
                _recording = false;
                if (string.IsNullOrEmpty(base64))
                {
                    _composerHint = "No audio captured.";
                    return;
                }

                _composerHint = "Transcribing…";
                var http = VoiceClient(Nav.BaseUri);
                using var response = await http.PostAsJsonAsync("api/voice/transcribe", new { audioBase64 = base64, fileName = "capture.webm" });
                if (!response.IsSuccessStatusCode)
                {
                    _composerHint = $"Transcription failed ({(int)response.StatusCode}).";
                    return;
                }
                var transcript = await response.Content.ReadFromJsonAsync<TranscriptResponse>();
                if (!string.IsNullOrWhiteSpace(transcript?.Text))
                {
                    _composerText = string.IsNullOrWhiteSpace(_composerText)
                        ? transcript.Text
                        : _composerText.TrimEnd() + "\n" + transcript.Text;
                    _composerHint = "Transcribed — edit and send when ready.";
                }
                else
                {
                    _composerHint = "Whisper returned empty text.";
                }
            }
        }
        catch (JSException ex)
        {
            _composerHint = $"Mic error: {ex.Message}";
            _recording = false;
        }
    }

    /// <summary>
    /// Synthesises an assistant turn back to MP3 via the OpenAI voice endpoint (or whichever
    /// IVoiceRuntime is wired) and plays it through the page's hidden audio element.
    /// </summary>
    private async Task SpeakAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            var http = VoiceClient(Nav.BaseUri);
            using var response = await http.PostAsJsonAsync("api/voice/speak", new { text });
            if (!response.IsSuccessStatusCode)
            {
                return;
            }
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0)
            {
                return;
            }
            await JS.InvokeVoidAsync("conciergeVoice.play", Convert.ToBase64String(bytes), "audio/mpeg");
        }
        catch (JSException)
        {
            // Audio element missing or autoplay blocked — non-fatal.
        }
    }

    private async Task SendAsync()
    {
        if (!CanSend || _activeRuntime is null)
        {
            return;
        }

        // First message on a fresh screen. Created here rather than by
        // StartNewAsync so there is no navigation in the middle of a send:
        // NavigateTo is unreliable from a touch handler in MAUI's WebView, and
        // the URL can catch up once the conversation has a title.
        if (_active is null)
        {
            _active = await Store.StartAsync(OwnerId);
            await RefreshSidebarAsync();
        }

        var input = _composerText.Trim();
        _composerText = string.Empty;

        // Inline image generation — if the prompt clearly asks for an image
        // ("draw / make a picture / generate an image / paint / illustrate"),
        // and an IImageRuntime is wired, route to it and append the result
        // as an assistant message. Falls through to chat if no runtime is
        // ready (so the user gets a normal text answer instead of silence).
        if (LooksLikeImagePrompt(input))
        {
            var imageRuntime = ImageRuntimes
                .FirstOrDefault(r => r.IsReady && r.Id != "null");
            if (imageRuntime is not null)
            {
                await Store.AppendEventAsync(_active.Id, ConversationEventType.UserMessage, input);
                _active = await Store.GetAsync(_active.Id);
                StateHasChanged();
                try
                {
                    var artifacts = await imageRuntime.GenerateAsync(
                        new Concierge.Shared.Media.ImageGenerationRequest(input));
                    var first = artifacts.FirstOrDefault();
                    if (first is not null)
                    {
                        var img = !string.IsNullOrEmpty(first.Url)
                            ? $"![generated]({first.Url})"
                            : first.Bytes is { Length: > 0 }
                                ? $"![generated](data:{first.MimeType};base64,{Convert.ToBase64String(first.Bytes)})"
                                : "[empty image artifact]";
                        await Store.AppendEventAsync(_active.Id, ConversationEventType.AssistantMessage,
                            $"Here's what I came up with:\n\n{img}",
                            imageRuntime.EngineLabel);
                        _active = await Store.GetAsync(_active.Id);
                        await RefreshSidebarAsync();
                        StateHasChanged();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    await Store.AppendEventAsync(_active.Id, ConversationEventType.AssistantMessage,
                        $"I tried to draw that, but the image runtime returned an error: {ex.Message}",
                        imageRuntime.EngineLabel);
                    _active = await Store.GetAsync(_active.Id);
                    StateHasChanged();
                    return;
                }
                // No artifact returned — fall through to chat so the user
                // gets a description even if generation failed silently.
            }
        }

        // Attachments are inlined as fenced code blocks ahead of the prompt so the LLM sees
        // them in context. Binary / image attachments are a v2 feature — text-only here.
        var attachmentBlocks = _pendingAttachments
            .Select(a => $"```file name=\"{a.FileName}\"\n{Encoding.UTF8.GetString(a.Bytes).TrimEnd()}\n```")
            .ToList();
        _pendingAttachments.Clear();

        var fullPrompt = string.Join("\n\n", attachmentBlocks.Append(input).Where(s => !string.IsNullOrEmpty(s)));

        await Store.AppendEventAsync(_active.Id, ConversationEventType.UserMessage, fullPrompt);
        _active = await Store.GetAsync(_active.Id);
        await RefreshSidebarAsync();
        StateHasChanged();

        _streaming = true;
        _streamingBuffer = string.Empty;
        _toolLoopIteration = 0;
        // The previous source is cancelled AND disposed before it is replaced.
        // It used to be only cancelled, so every message left one behind — each
        // holding a wait handle and its token registrations — and a long
        // conversation leaked one per turn.
        var previous = _streamCts;
        _streamCts = new CancellationTokenSource();
        if (previous is not null)
        {
            try
            {
                previous.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already torn down elsewhere.
            }
            finally
            {
                previous.Dispose();
            }
        }

        try
        {
            // Outer loop drives the tool-call cycle: stream → persist → if the assistant
            // emitted ```tool-call``` blocks AND the user opted in, execute them, append the
            // synthetic user turn with the results, and stream again. Cap at MaxToolIterations
            // so a buggy reply can't pin the chat in a tight loop.
            for (var iteration = 0; iteration <= MaxToolIterations; iteration++)
            {
                _toolLoopIteration = iteration;
                StateHasChanged();

                var assistantText = await StreamOnceAsync(_streamCts.Token);
                if (assistantText is null)
                {
                    // Cancelled or empty — caller has already persisted whatever it could.
                    break;
                }

                if (!_executeToolCalls || Tools.Tools.Count == 0)
                {
                    break;
                }

                var calls = ToolCallProtocol.Extract(assistantText);
                if (calls.Count == 0)
                {
                    break;
                }

                if (iteration == MaxToolIterations)
                {
                    await Store.AppendEventAsync(_active!.Id, ConversationEventType.ToolResult,
                        $"The tool loop stopped after {MaxToolIterations} rounds without finishing.");
                    break;
                }

                // Each call is recorded before it runs, so a turn abandoned half way still
                // leaves a log that says what was asked for.
                foreach (var call in calls)
                {
                    await Store.AppendEventAsync(
                        _active!.Id,
                        ConversationEventType.ToolCall,
                        call.Name + " " + (call.Arguments?.ToJsonString() ?? "{}"));
                }

                // The scheduler overlaps read-only calls, runs the rest alone, and returns a
                // result for every call — including ones a cancellation stopped.
                var planned = calls
                    .Select(call => new PlannedToolCall(call.Name, call.Arguments))
                    .ToList();

                var outcomes = await ToolScheduler.ExecuteAsync(planned, _streamCts.Token);

                var results = new List<(string ToolName, AgentToolResult Result)>();
                foreach (var outcome in outcomes)
                {
                    // Oversized output is trimmed once here, rather than paid for on every
                    // later request in this conversation.
                    var pruned = ResultPruner.Prune(outcome.Result.Output);
                    var result = pruned.WasPruned
                        ? new AgentToolResult(outcome.Result.Success, pruned.Text, outcome.Result.FailureMessage)
                        : outcome.Result;

                    results.Add((outcome.ToolName, result));

                    await Store.AppendEventAsync(
                        _active!.Id,
                        ConversationEventType.ToolResult,
                        ToolCallProtocol.FormatResult(outcome.ToolName, result));

                    // A model going round in circles is told so, rather than left to spend
                    // the whole iteration budget discovering it.
                    var reminder = RepeatReminder.Observe(outcome.ToolName, outcome.Result.Output);
                    if (reminder is not null)
                    {
                        await Store.AppendEventAsync(_active.Id, ConversationEventType.UserMessage, reminder);
                    }
                }

                _active = await Store.GetAsync(_active!.Id);
                StateHasChanged();
            }
        }
        finally
        {
            if (_active is not null)
            {
                _active = await Store.GetAsync(_active.Id);
                await RefreshSidebarAsync();
            }
            _streaming = false;
            _streamingBuffer = string.Empty;
            _toolLoopIteration = 0;
            StateHasChanged();
        }
    }

    /// <summary>
    /// Streams one assistant turn over the active conversation history. Returns the
    /// assembled assistant text (also persisted to the store) or <c>null</c> if the
    /// stream was cancelled or the runtime emitted nothing.
    /// </summary>
    private async Task<string?> StreamOnceAsync(CancellationToken cancellationToken)
    {
        if (_active is null || _activeRuntime is null)
        {
            return null;
        }

        // Derived from the log rather than read off the message rows: the log is what is
        // true, and it is the only place a tool result is distinguishable from something a
        // person typed.
        var turns = (await Store.DeriveMessagesAsync(_active.Id, cancellationToken)).ToList();

        // Compact before spending a request, not after one is refused for length. On the
        // smallest on-device model this is the difference between a conversation that keeps
        // going and one that stops after a handful of turns.
        var compacted = await Compaction.CompactIfNeededAsync(
            turns, LoopOptions.ContextBudgetTokens, cancellationToken);

        if (compacted.WasCompacted)
        {
            turns = compacted.Turns.ToList();
            await Store.AppendEventAsync(
                _active.Id,
                ConversationEventType.CompactionReplace,
                $"Summarised the earlier part of this conversation, reclaiming about {compacted.TokensReclaimed} tokens.");
        }

        var systemParts = new List<string>();

        // ── Active skills first ────────────────────────────────────────────
        // Skills come BEFORE custom system prompt + tool catalog so the
        // assistant reads the specialist guidance as its primary identity for
        // the turn, then layers user-specific overrides on top.
        if (_activeSkillIds.Count > 0)
        {
            var skillPrompt = SkillRuntime.ComposeSystemPrompt(_activeSkillIds);
            if (!string.IsNullOrWhiteSpace(skillPrompt))
            {
                systemParts.Add(skillPrompt);
            }
        }

        var persisted = _active.SystemPrompt;
        var systemText = !string.IsNullOrWhiteSpace(persisted) ? persisted : _systemPromptDraft;
        if (!string.IsNullOrWhiteSpace(systemText))
        {
            systemParts.Add(systemText);
        }
        if (_includeToolCatalog && Tools.Tools.Count > 0)
        {
            systemParts.Add(Tools.BuildSystemPromptAddendum());
        }
        if (systemParts.Count > 0)
        {
            turns.Insert(0, new ChatTurn("system", string.Join("\n\n", systemParts)));
        }

        var buffer = new StringBuilder();
        _streamingBuffer = string.Empty;
        var cancelled = false;

        // The reply was written down only after the last token arrived, so
        // anything that stopped the process mid-generation lost all of it. Not
        // hypothetical: the demo conversations on this machine each hold a
        // question and no answer, because generation died before reaching the
        // single append at the end of this method.
        //
        // A checkpoint file carries the partial text while it exists only in
        // memory. Throttled, because a write per token is hundreds of writes
        // for one reply and the point is durability, not a transcript.
        var lastCheckpoint = DateTimeOffset.UtcNow;
        var checkpointedLength = 0;

        try
        {
            await foreach (var chunk in _activeRuntime.StreamAsync(turns, cancellationToken))
            {
                buffer.Append(chunk);
                _streamingBuffer = buffer.ToString();
                StateHasChanged();

                var now = DateTimeOffset.UtcNow;
                if (buffer.Length - checkpointedLength >= 240
                    || (now - lastCheckpoint).TotalMilliseconds >= 1000)
                {
                    await WriteDraftAsync(_active.Id, _streamingBuffer);
                    lastCheckpoint = now;
                    checkpointedLength = buffer.Length;
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            buffer.AppendLine();
            buffer.Append("[stream error: ").Append(ex.Message).Append(']');
            _streamingBuffer = buffer.ToString();
        }

        var assistantText = buffer.ToString();
        if (cancelled)
        {
            if (assistantText.Length > 0)
            {
                await Store.AppendEventAsync(_active.Id, ConversationEventType.AssistantMessage,
                    assistantText + "\n[stream cancelled before completion]",
                    _activeRuntime.EngineLabel);
            }

            // Whatever happened, the text is in the log now, so the checkpoint
            // has nothing left to protect.
            DeleteDraft(_active.Id);
            return null;
        }

        if (!string.IsNullOrWhiteSpace(assistantText))
        {
            await Store.AppendEventAsync(_active.Id, ConversationEventType.AssistantMessage, assistantText, _activeRuntime.EngineLabel);
            DeleteDraft(_active.Id);
            _active = await Store.GetAsync(_active.Id);
            return assistantText;
        }

        DeleteDraft(_active.Id);
        return null;
    }

    private static string FormatRole(string role) => role switch
    {
        "user" => "You",
        "assistant" => "Assistant",
        "system" => "System",
        _ => role
    };

    private static bool LooksLikeImagePrompt(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.ToLowerInvariant();
        ReadOnlySpan<string> triggers =
        [
            "draw me", "draw a", "draw an", "draw the",
            "make a picture", "make me a picture", "make a drawing",
            "paint", "illustrate",
            "generate an image", "generate a picture",
            "create an image", "create a picture",
            "show me a picture of", "show me an image of",
            "render a", "render an",
        ];
        foreach (var trig in triggers)
        {
            if (t.Contains(trig, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private sealed record TextAttachment(string FileName, byte[] Bytes);

    private sealed record TranscriptResponse(string? Text, string? Language);

    // ── The workspace ─────────────────────────────────────────────────────
    // Added when the UI was rebuilt as one screen. Threads, skills, approvals
    // and the runtime all render in the sidebar beside the thread, so the
    // things they used to be separate pages for are handled here.

    /// <summary>Tool chips are collapsed until asked. Claude Design renders a
    /// tool call as one grey pill with a chevron; the output is behind it.</summary>
    private readonly HashSet<Guid> _openTools = new();

    private void ToggleTool(Guid id)
    {
        if (!_openTools.Remove(id))
        {
            _openTools.Add(id);
        }
    }

    /// <summary>Decisions on pending approvals, in memory. Persistence is a
    /// layered concern; what matters here is that answering one removes it from
    /// the thread immediately rather than leaving it sitting there.</summary>
    private readonly Dictionary<string, bool> _decided = new(StringComparer.Ordinal);

    private void Decide(string id, bool allowed)
    {
        _decided[id] = allowed;
        StateHasChanged();
    }

    /// <summary>Risk as a dot, and nothing else. No filled cards, no coloured
    /// badges — a dot and the word beside it is the whole status vocabulary.</summary>
    private static string RiskDot(string? risk) => (risk ?? string.Empty).ToLowerInvariant() switch
    {
        "high" or "critical" => "dot-danger",
        "medium" => "dot-waiting",
        "low" => "dot-done",
        _ => "dot-idle",
    };

    /// <summary>What a risk level means in reach. Derived from the level rather
    /// than written per item, because the queue carries no scope field and a
    /// specific-sounding sentence that is not backed by data is worse than a
    /// general one that is true.</summary>
    private static string ReachOf(string? risk) => (risk ?? string.Empty).ToLowerInvariant() switch
    {
        "high" or "critical" => "It can reach files and tools outside this conversation",
        "medium" => "It can change things inside this workspace",
        _ => "It stays inside this conversation",
    };

    /// <summary>"4m", "3h", then a date. Past a day a duration stops being
    /// useful and a date starts.</summary>
    private static string Ago(DateTimeOffset at)
    {
        var span = DateTimeOffset.UtcNow - at.ToUniversalTime();
        if (span.TotalMinutes < 1) return "now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h";
        return at.ToLocalTime().ToString("d MMM");
    }

    /// <summary>Which sidebar groups are folded open. Threads and Approvals
    /// start open because they are what is happening; Skills is a catalogue of
    /// sixty-nine and stays shut until asked for. Folding is what keeps the
    /// sidebar inside the window without a scrollbar.</summary>
    private bool _openThreads = true;
    private bool _openApprovals = true;
    private bool _openSkills;

    /// <summary>
    /// One HttpClient for the voice endpoints, for the life of the process.
    ///
    /// Both callers used to do `using var http = new HttpClient(...)` per call.
    /// Disposing an HttpClient does not release its socket — it sits in
    /// TIME_WAIT for around four minutes — so dictating repeatedly walks
    /// through the ephemeral port range and eventually cannot open another.
    ///
    /// Lazy and keyed on the base address because Nav.BaseUri is not known
    /// until the component runs, and differs between the MAUI host and the web
    /// host sharing this component.
    /// </summary>
    private static readonly object _voiceGate = new();
    private static HttpClient? _voiceClient;
    private static string? _voiceBase;

    private static HttpClient VoiceClient(string baseUri)
    {
        lock (_voiceGate)
        {
            if (_voiceClient is null || !string.Equals(_voiceBase, baseUri, StringComparison.Ordinal))
            {
                _voiceClient?.Dispose();
                _voiceClient = new HttpClient { BaseAddress = new Uri(baseUri) };
                _voiceBase = baseUri;
            }

            return _voiceClient;
        }
    }

    // ── Crash-safe partial replies ────────────────────────────────────────
    //
    // A reply is one append at the end of generation, so until the last token
    // lands it exists only in memory and on screen. Anything that kills the
    // process before then loses the whole answer after the person has watched
    // it being written — and there is a known access violation in the native
    // inference call that does exactly that.
    //
    // The checkpoint is a plain file per conversation rather than a row,
    // because the thing being defended against is the process dying: a file
    // that has been written survives what an in-flight transaction does not,
    // and it needs no change to the event log's schema.

    private static string DraftDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Concierge",
        "drafts");

    private static string DraftPath(Guid conversationId)
        => Path.Combine(DraftDirectory, $"{conversationId:N}.partial");

    private static async Task WriteDraftAsync(Guid conversationId, string text)
    {
        try
        {
            Directory.CreateDirectory(DraftDirectory);
            await File.WriteAllTextAsync(DraftPath(conversationId), text);
        }
        catch
        {
            // A checkpoint that cannot be written must never interrupt the reply
            // still arriving. Losing durability is bad; losing the generation to
            // a disk error is worse.
        }
    }

    private static void DeleteDraft(Guid conversationId)
    {
        try
        {
            var path = DraftPath(conversationId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Left behind, and picked up by recovery on next open. Harmless.
        }
    }

    /// <summary>
    /// A draft still on disk when a conversation opens means the last
    /// generation never finished. The text goes into the log where it belongs,
    /// marked, so a truncated answer is never mistaken for a whole one.
    /// </summary>
    private async Task RecoverDraftAsync(Guid conversationId)
    {
        try
        {
            var path = DraftPath(conversationId);
            if (!File.Exists(path))
            {
                return;
            }

            var partial = await File.ReadAllTextAsync(path);
            File.Delete(path);

            if (string.IsNullOrWhiteSpace(partial))
            {
                return;
            }

            await Store.AppendEventAsync(
                conversationId,
                ConversationEventType.AssistantMessage,
                partial + "\n\n[recovered — this answer was interrupted before it finished]",
                _activeRuntime?.EngineLabel ?? "recovered");

            _active = await Store.GetAsync(conversationId);
        }
        catch
        {
            // Recovery is best effort; the conversation must still open.
        }
    }
}
