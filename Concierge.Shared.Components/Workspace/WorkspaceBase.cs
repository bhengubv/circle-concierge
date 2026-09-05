// The workspace, without a shape.
//
// Every form factor runs this: the conversation list, the send loop, the tool
// loop, approvals, skills, drafts and the MAUI WebView workarounds. None of it
// knows what it looks like, and none of it is duplicated per form factor —
// three recipes rendering different markup must still behave identically,
// which only holds if the behaviour lives in exactly one place.
//
// The views are Workspace/Desktop, Workspace/Handheld and Workspace/Wearable.
// Each is markup and a stylesheet and nothing else; Pages/Chat.razor picks one.
//
// This is a move, not a rewrite. Every member below came out of
// Pages/Chat.razor.cs unchanged except for its visibility: a view is a derived
// class rather than the other half of a partial, so what was private had to
// become protected for the markup to reach it.
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using System.Text;
using System.Text.Json.Nodes;
using Concierge.Shared.Chat;
using Concierge.Shared.Tools;

namespace Concierge.Shared.Components.Workspace;

public abstract class WorkspaceBase : ComponentBase, IDisposable
{
    // These were @inject directives in Chat.razor. A view cannot inject for its
    // base, so they move here — same services, same names, same lifetimes.
    [Inject] protected IConversationStore Store { get; set; } = default!;
    [Inject] protected IEnumerable<IChatRuntime> Runtimes { get; set; } = default!;
    [Inject] protected IEnumerable<Concierge.Shared.Media.IImageRuntime> ImageRuntimes { get; set; } = default!;
    [Inject] protected IAgentToolRegistry Tools { get; set; } = default!;
    [Inject] protected Concierge.Shared.Tools.IToolCallScheduler ToolScheduler { get; set; } = default!;
    [Inject] protected Concierge.Shared.Tools.IRepeatToolReminder RepeatReminder { get; set; } = default!;
    [Inject] protected Concierge.Shared.Context.IToolResultPruner ResultPruner { get; set; } = default!;
    [Inject] protected Concierge.Shared.Context.ICompactionEngine Compaction { get; set; } = default!;
    [Inject] protected Concierge.Shared.ConciergeToolLoopOptions LoopOptions { get; set; } = default!;
    [Inject] protected Concierge.Shared.Skills.ISkillRuntime SkillRuntime { get; set; } = default!;
    [Inject] protected ISkillCatalogService SkillCatalog { get; set; } = default!;
    [Inject] protected IConciergeStateService State { get; set; } = default!;
    [Inject] protected ILlmRuntimeService LlmRuntime { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;
    [Inject] protected IJSRuntime JS { get; set; } = default!;
    [Inject] protected Concierge.Shared.Session.ISessionState SessionState { get; set; } = default!;
    [Inject] protected Concierge.Shared.Tools.IToolApprovalService Approval { get; set; } = default!;


    [Parameter] public Guid? ConversationId { get; set; }

    // Was a constant. The right ceiling differs between a phone on battery and a desktop,
    // and Concierge already varies the model by device state, so this belongs with the
    // other budgets rather than nailed to the page.
    protected int MaxToolIterations => LoopOptions.MaxToolIterations;
    protected const long MaxAttachmentBytes = 256 * 1024; // 256 KB — text-only attachments in v1
    protected const string OwnerId = "local";

    protected List<Conversation> _conversations = new();
    protected Conversation? _active;
    protected string _composerText = string.Empty;
    protected bool _streaming;
    protected string _streamingBuffer = string.Empty;
    protected CancellationTokenSource? _streamCts;
    protected int _toolLoopIteration;

    protected List<IChatRuntime> _orderedRuntimes = new();
    protected IChatRuntime? _activeRuntime;
    protected string _systemPromptDraft = string.Empty;
    protected bool _includeToolCatalog = true;
    protected bool _executeToolCalls = true;

    protected readonly List<TextAttachment> _pendingAttachments = new();

    /// <summary>Pictures lifted out of the attachments for the turn being sent.
    /// Separate from the text blocks because they travel beside the prompt
    /// rather than inside it.</summary>
    protected readonly List<ChatImage> _pendingImages = new();
    protected bool _recording;

    /// <summary>
    /// Currently-activated skill ids for this conversation. Their SKILL.md
    /// bodies get prepended to the system prompt on every send. Stack any
    /// number — Concierge composes them in <c>ISkillRuntime.ComposeSystemPrompt</c>.
    /// </summary>
    protected readonly HashSet<string> _activeSkillIds = new(StringComparer.OrdinalIgnoreCase);
    // ── Model download ─────────────────────────────────────────────────
    // The on-device engine reports a missing model rather than fetching it; these carry the
    // person's answer back. Runtimes that ship with their model implement none of this and
    // PendingModel stays null.

    protected bool _downloading;
    protected double _downloadProgress;
    protected string? _downloadDetail;
    protected CancellationTokenSource? _downloadCancellation;
    protected bool _gone;

    protected PendingModelDownload? PendingModel
        => (_activeRuntime as IModelDownloadRequired)?.PendingDownload;

    protected async Task AcceptDownload()
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

    protected void CancelDownload()
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
        StopWatchingApprovals();
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

    protected bool _showSkillPicker;
    protected string _skillFilter = string.Empty;

    // Named the two icons sitting right beside it and pointed at a emoji that
    // is no longer there. The field still doubles as the status line for
    // attachments and transcription; only the resting text changed.
    protected string _composerHint = "Shift + Enter for a new line";

    // _active is deliberately not required. Sending from the empty screen
    // creates the conversation; requiring one to exist first is what forced a
    // separate "Start a chat" button into the design.
    protected bool CanSend
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

        // What you had, before anything is drawn.
        //
        // Skills matter most here and are the least visible: one composes into
        // the system prompt before every turn, so losing it across a restart
        // silently changes how the assistant answers with nothing on screen to
        // explain why. The thread you were in and the text you had typed are
        // restored for the ordinary reason.
        //
        // Not restored: an interrupted run. A partial reply is recovered into
        // the thread by RecoverDraftAsync, and that is as far as it goes — a
        // run that stopped while asking permission must not resume itself.
        _session = await SessionState.LoadAsync();

        WatchApprovals();

        foreach (var skillId in _session.Skills)
        {
            _activeSkillIds.Add(skillId);
        }

        await RefreshSidebarAsync();

        // Land where you left. Only from the bare workspace — an explicit
        // /chat/{id} is a deliberate destination and must not be overridden —
        // and only if that thread still exists, since it may have been deleted
        // from History since.
        if (ConversationId is null
            && _session.LastConversationId is { } last
            && _conversations.Any(c => c.Id == last))
        {
            Nav.NavigateTo($"chat/{last}");
        }

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

    protected void OnLocationChanged(object? sender, Microsoft.AspNetCore.Components.Routing.LocationChangedEventArgs e)
        => _ = InvokeAsync(async () =>
        {
            // Every link in the drawer is an anchor, so one line here closes
            // it for all of them — a thread, a room, the queue, the brand.
            // Leaving it open would cover the thing it was used to reach.
            _drawerOpen = false;

            await ConsumeHomeHandoffAsync();
            StateHasChanged();
        });

    /// <summary>The query string most recently acted on. Without this the same
    /// handoff fires repeatedly, because OnParametersSetAsync runs on every
    /// parameter change and SendAsync causes re-renders of its own.</summary>
    protected string? _handledHandoff;

    protected async Task ConsumeHomeHandoffAsync()
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

            // A half-typed question survives the app closing. Only restored
            // into an empty composer: whatever is being typed now wins over
            // what was typed before.
            if (string.IsNullOrEmpty(_composerText))
            {
                _composerText = _session.UnsentFor(id);
            }

            // Not awaited, deliberately. Saving where you are is disk I/O in
            // the middle of a render, and awaiting it here left the component
            // unfinished when the next interaction arrived — the same
            // async-in-render trap the handoff hit. Losing one save is
            // nothing; a half-rendered workspace is not.
            _ = RememberSessionAsync();
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

    protected async Task SaveSystemPromptAsync()
    {
        if (_active is null)
        {
            return;
        }
        _active.SystemPrompt = _systemPromptDraft;
        await Task.CompletedTask;
    }

    protected void ToggleSkillPicker()
    {
        _showSkillPicker = !_showSkillPicker;

        // The picker is a panel over everything. Leaving the drawer open
        // underneath it means dismissing two things to get back to the work.
        _drawerOpen = false;
    }

    /// <summary>Opens settings, and closes the drawer it was pressed in.</summary>
    protected void OpenSettings()
    {
        _settingsOpen = true;
        _drawerOpen = false;
    }

    protected void ToggleSkill(string id)
    {
        if (!_activeSkillIds.Add(id))
        {
            _activeSkillIds.Remove(id);
        }

        _ = RememberSessionAsync();
    }

    protected void DeactivateSkill(string id)
    {
        _activeSkillIds.Remove(id);
        _ = RememberSessionAsync();
    }

    // The product's local-first identity: CircleAI is the default LLM.
    // Cloud providers exist as escape hatches when the user explicitly
    // picks one in Settings or here. Saved choice lives in localStorage
    // under this key so the page remembers across launches.
    protected const string DefaultProviderId = "circleai";
    protected const string ProviderPreferenceStorageKey = "concierge-provider-id";

    protected async Task<IChatRuntime?> ChooseActiveRuntimeAsync()
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

    protected async Task OnProviderChanged(ChangeEventArgs args)
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

    protected async Task RefreshSidebarAsync()
    {
        _conversations = (await Store.ListAsync(OwnerId)).ToList();
    }

    protected async Task StartNewAsync()
    {
        var conversation = await Store.StartAsync(OwnerId);
        await RefreshSidebarAsync();
        Nav.NavigateTo($"chat/{conversation.Id}");
    }

    protected async Task OnAttachmentSelected(InputFileChangeEventArgs args)
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

    protected void RemoveAttachment(TextAttachment attachment)
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
    protected async Task ToggleMicAsync()
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
    protected async Task SpeakAsync(string text)
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

    protected async Task SendAsync()
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

        // Attachments split by what they actually are, which the previous
        // version did not do: everything went through Encoding.UTF8.GetString,
        // pictures included, so the camera hand-off sent a few hundred
        // kilobytes of decoded JPEG and asked what was in the picture.
        // Content decides, not the file name: a PNG called notes.txt is
        // still a PNG.
        var attachmentBlocks = new List<string>();
        _pendingImages.Clear();

        foreach (var attachment in _pendingAttachments)
        {
            var mediaType = Concierge.Shared.Attachments.AttachmentKind.ImageMediaType(attachment.Bytes);

            if (mediaType is not null)
            {
                _pendingImages.Add(new ChatImage(attachment.FileName, mediaType, attachment.Bytes));
                continue;
            }

            if (!Concierge.Shared.Attachments.AttachmentKind.LooksLikeText(attachment.Bytes))
            {
                // Not text, and not a picture this understands. Naming it beats
                // pasting its bytes into the prompt.
                attachmentBlocks.Add($"[{attachment.FileName} was attached, but it is not text or a picture this can read.]");
                continue;
            }

            attachmentBlocks.Add($"```file name=\"{attachment.FileName}\"\n{Encoding.UTF8.GetString(attachment.Bytes).TrimEnd()}\n```");
        }

        // A runtime that cannot see is told so, rather than handed pictures it
        // will ignore. The model that ships with Concierge runs on the device
        // and is text-only, so this is the common case, not the edge one.
        if (_pendingImages.Count > 0 && _activeRuntime is not IVisionCapableRuntime)
        {
            var names = string.Join(", ", _pendingImages.Select(i => i.FileName));
            attachmentBlocks.Add($"[{names} attached, but {_activeRuntime?.EngineLabel ?? "this model"} cannot look at pictures.]");
            _pendingImages.Clear();
        }

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
    protected async Task<string?> StreamOnceAsync(CancellationToken cancellationToken)
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

        // Pictures ride on the last user turn, which is the one they were
        // attached to. Done here rather than in the store because the
        // transcript keeps what was said, and an image is not a message — it
        // is something handed over with one.
        if (_pendingImages.Count > 0)
        {
            var lastUser = turns.FindLastIndex(t =>
                string.Equals(t.Role, "user", StringComparison.OrdinalIgnoreCase));

            if (lastUser >= 0)
            {
                turns[lastUser] = turns[lastUser] with { Images = _pendingImages.ToArray() };
            }
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

    protected static string FormatRole(string role) => role switch
    {
        "user" => "You",
        "assistant" => "Assistant",
        "system" => "System",
        _ => role
    };

    protected static bool LooksLikeImagePrompt(string text)
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

    protected sealed record TextAttachment(string FileName, byte[] Bytes);

    protected sealed record TranscriptResponse(string? Text, string? Language);

    // ── The workspace ─────────────────────────────────────────────────────
    // Added when the UI was rebuilt as one screen. Threads, skills, approvals
    // and the runtime all render in the sidebar beside the thread, so the
    // things they used to be separate pages for are handled here.

    /// <summary>Tool chips are collapsed until asked. Claude Design renders a
    /// tool call as one grey pill with a chevron; the output is behind it.</summary>
    protected readonly HashSet<Guid> _openTools = new();

    protected void ToggleTool(Guid id)
    {
        if (!_openTools.Remove(id))
        {
            _openTools.Add(id);
        }
    }

    /// <summary>Decisions on pending approvals, in memory. Persistence is a
    /// layered concern; what matters here is that answering one removes it from
    /// the thread immediately rather than leaving it sitting there.</summary>
    protected readonly Dictionary<string, bool> _decided = new(StringComparer.Ordinal);

    protected void Decide(string id, bool allowed)
    {
        _decided[id] = allowed;
        StateHasChanged();
    }

    /// <summary>Risk as a dot, and nothing else. No filled cards, no coloured
    /// badges — a dot and the word beside it is the whole status vocabulary.</summary>
    /// <summary>The same dot, for the risk a tool call actually carries.</summary>
    protected static string RiskDot(Concierge.Shared.ConciergeToolRisk risk) => ApprovalRisk.SeverityOf(risk) switch
    {
        ApprovalSeverity.Danger => "dot-danger",
        ApprovalSeverity.Caution => "dot-waiting",
        ApprovalSeverity.Settled => "dot-done",
        _ => "dot-idle",
    };

    protected static string RiskDot(string? risk) => ApprovalRisk.SeverityOf(risk) switch
    {
        ApprovalSeverity.Danger => "dot-danger",
        ApprovalSeverity.Caution => "dot-waiting",
        ApprovalSeverity.Settled => "dot-done",
        _ => "dot-idle",
    };

    /// <summary>
    /// What a risk level means in reach. The wording lives in
    /// Concierge.Shared.ApprovalRisk because the Wear OS head asks the same
    /// question and must not answer it differently.
    /// </summary>
    protected static string ReachOf(string? risk) => ApprovalRisk.ReachOf(risk);

    /// <summary>"4m", "3h", then a date. Past a day a duration stops being
    /// useful and a date starts.</summary>
    protected static string Ago(DateTimeOffset at)
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

    /// <summary>
    /// Folded by default. The rooms are destinations you visit occasionally;
    /// the threads above them are the work.
    /// </summary>

    /// <summary>
    /// Whether every thread is listed. Starts false so the four sidebar groups
    /// fit the window without a scrollbar.
    /// </summary>
    protected bool _allThreads;

    /// <summary>
    /// Which sidebar group is unfolded — one at a time, deliberately.
    ///
    /// Threads and Approvals both opened until Rooms made a fourth group: four
    /// headers plus two open bodies overflowed the sidebar by 67px in a 479px
    /// window, which put Rooms below the fold and Skills behind the runtime
    /// footer. Scrollbars are hidden app-wide so nothing said so, and capping
    /// the thread list recovered 3px because the "show all" control replaces
    /// the row it hides. An accordion cannot outgrow the space it has, which
    /// is the same conclusion the settings panel reached.
    ///
    /// Threads opens on arrival because it is the work. A folded group still
    /// shows its count, so Approvals waiting on you is visible without being
    /// unfolded.
    /// </summary>
    /// <summary>
    /// Whether the sidebar is showing as a drawer. Only meaningful below
    /// 760px, where the sidebar is a fixed overlay rather than a column;
    /// above it the class means nothing and this stays false.
    ///
    /// Below that breakpoint there was previously no navigation at all — no
    /// thread, no room, no approval queue — because the sidebar was
    /// display:none and the bottom tab bar that used to stand in for it had
    /// been deleted.
    /// </summary>
    protected bool _drawerOpen;

    protected void OpenDrawer() => _drawerOpen = true;

    protected void CloseDrawer() => _drawerOpen = false;

    protected string? _openGroup = "threads";

    protected bool IsGroupOpen(string key) => string.Equals(_openGroup, key, StringComparison.Ordinal);

    protected void ToggleGroup(string key) => _openGroup = IsGroupOpen(key) ? null : key;

    /// <summary>
    /// The rooms, in the order somebody new to Concierge would want them:
    /// what it is, then what it can do to your machine, then what it costs.
    /// Product leads because it is the directory of the rest; leaving it out
    /// left its route reachable from nowhere.
    /// </summary>
    protected static readonly (string Name, string Href)[] RoomLinks =
    [
        ("Product", "product"),
        ("Engineering", "engineering"),
        ("Beyond Code", "beyond"),
        ("Business APIs", "business-apis"),
        ("Roadmap", "roadmap"),
        ("Release", "release"),
        ("Pricing", "pricing")
    ];

    /// <summary>
    /// Things you go and use, as opposed to rooms you go and read. Their own
    /// group rather than six more entries under Rooms: ten links in one
    /// unfolded group overflowed the sidebar, which is the defect the
    /// accordion was introduced to stop.
    /// </summary>
    protected static readonly (string Name, string Href)[] ToolLinks =
    [
        ("History", "history"),
        ("Diagrams", "diagrams"),
        ("Images", "images")
    ];

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
    protected static readonly object _voiceGate = new();
    protected static HttpClient? _voiceClient;
    protected static string? _voiceBase;

    protected static HttpClient VoiceClient(string baseUri)
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

    // ── Where you were ────────────────────────────────────────────────────

    /// <summary>Last read from disk. Held so a restore does not have to re-read
    /// the file every time a conversation opens.</summary>
    // ── What is actually waiting ──────────────────────────────────────────

    /// <summary>
    /// The real approval queue.
    ///
    /// It used to come from ConciergeSnapshot.Approvals, which held two
    /// hardcoded entries — so the sidebar permanently said two were waiting and
    /// the room offered Allow on requests that had never been made. In a
    /// product whose whole claim is that it asks before it acts, that is the
    /// worst possible place for a lie.
    ///
    /// This is the same queue the inline prompt in the thread reads, and the
    /// same subscription pattern: the service raises PendingChanged from
    /// whichever thread ran the tool call, so redraws are marshalled back.
    /// </summary>
    protected IReadOnlyList<Concierge.Shared.Tools.PendingApproval> _pendingApprovals = [];

    private Concierge.Shared.Tools.InteractiveToolApprovalService? _approver;

    private void WatchApprovals()
    {
        // May be the fail-closed default, in which case there is no queue to
        // show and nothing to subscribe to.
        _approver = Approval as Concierge.Shared.Tools.InteractiveToolApprovalService;
        if (_approver is null)
        {
            return;
        }

        _approver.PendingChanged += OnPendingApprovalsChanged;
        _pendingApprovals = _approver.Pending;
    }

    private void OnPendingApprovalsChanged(object? sender, EventArgs e)
    {
        _pendingApprovals = _approver!.Pending;
        _ = InvokeAsync(StateHasChanged);
    }

    private void StopWatchingApprovals()
    {
        if (_approver is not null)
        {
            _approver.PendingChanged -= OnPendingApprovalsChanged;
        }
    }

    /// <summary>Answers one, from the sidebar or the room.</summary>
    protected void DecideApproval(Guid id, bool allowed)
    {
        _approver?.Answer(id, allowed
            ? Concierge.Shared.Tools.ToolApprovalDecision.Allowed
            : Concierge.Shared.Tools.ToolApprovalDecision.Denied);

        _pendingApprovals = _approver?.Pending ?? [];
    }

    protected Concierge.Shared.Session.SessionSnapshot _session =
        Concierge.Shared.Session.SessionSnapshot.Empty;

    /// <summary>
    /// Saves the thread you are in, the skills you have on, and anything typed
    /// and not sent.
    ///
    /// Unsent text is kept per conversation: two threads each holding a
    /// half-written question must not overwrite each other. A thread whose
    /// composer is empty is dropped from the map rather than stored blank, so
    /// the file does not grow one entry per thread ever opened.
    /// </summary>
    /// <summary>When the composer last persisted. A file write per keystroke
    /// is neither needed nor kind to a disk.</summary>
    private DateTimeOffset _composerSavedAt = DateTimeOffset.MinValue;

    private static readonly TimeSpan ComposerSaveInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Called after every keystroke in the composer, and saves at most every
    /// couple of seconds. Losing the last two seconds of typing to a crash is
    /// a fair trade for not writing a file on every character.
    /// </summary>
    protected async Task OnComposerChangedAsync()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _composerSavedAt < ComposerSaveInterval)
        {
            return;
        }

        _composerSavedAt = now;
        await RememberSessionAsync();
    }

    protected async Task RememberSessionAsync()
    {
        var unsent = new Dictionary<string, string>(_session.Unsent, StringComparer.Ordinal);

        if (_active is not null)
        {
            var key = _active.Id.ToString("N");

            if (string.IsNullOrWhiteSpace(_composerText))
            {
                unsent.Remove(key);
            }
            else
            {
                unsent[key] = _composerText;
            }
        }

        _session = new Concierge.Shared.Session.SessionSnapshot(
            LastConversationId: _active?.Id,
            ActiveSkillIds: _activeSkillIds.ToArray(),
            UnsentText: unsent);

        await SessionState.SaveAsync(_session);
    }

    protected static string DraftDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Concierge",
        "drafts");

    protected static string DraftPath(Guid conversationId)
        => Path.Combine(DraftDirectory, $"{conversationId:N}.partial");

    protected static async Task WriteDraftAsync(Guid conversationId, string text)
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

    protected static void DeleteDraft(Guid conversationId)
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
    protected async Task RecoverDraftAsync(Guid conversationId)
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

    /// <summary>Settings is a panel over the work, not a place you go. Opened
    /// from the runtime row at the foot of the sidebar.</summary>
    protected bool _settingsOpen;
}
