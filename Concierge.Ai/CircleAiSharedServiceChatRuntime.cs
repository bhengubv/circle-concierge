using System.Runtime.CompilerServices;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Concierge.Shared.Devices;
using Concierge.Shared.Planning;
using Concierge.Shared.Tools;
using Microsoft.Extensions.Hosting;

namespace Concierge.Ai;

/// <summary>Thin service consumer: no model probing, loading, selection, or downloads.</summary>
public sealed class CircleAiSharedServiceChatRuntime(ICircleAiServiceChatLink link)
    : IChatRuntime, IChatRuntimeCapabilitySource, IChatRuntimeStatusEvents, ILlmRuntimeService, IChatRuntimePreparation
{
    private readonly SemaphoreSlim _turn = new(1, 1);
    private DateTimeOffset _observed = DateTimeOffset.MinValue;
    private string _version = "Not checked";
    private ChatRuntimeCapabilityState _state = ChatRuntimeCapabilityState.Preparing;
    private bool _sending;
    private string _stage = "Unknown";
    private bool _acceptsQueuedRequests;
    internal TimeSpan PreparationPollInterval { get; init; } = TimeSpan.FromSeconds(3);
    public bool CanWaitForPreparation { get; private set; }
    public string Id => "circleai";
    public string EngineLabel => "Circle AI Android service";
    public bool LeavesDevice => false;
    public bool IsReady { get; private set; }
    public string StatusMessage { get; private set; } = "Checking Circle AI on this device…";
    public event EventHandler? StatusChanged;
    public ModelDownloadProgress? CurrentDownloadProgress { get; private set; }
    public ChatRuntimeCapabilityCatalog CapabilityCatalog => new(1, Id, _version, _observed,
        [new("conversation", "Conversation", _state, StatusMessage),
         new("visual-input", "Pictures", ChatRuntimeCapabilityState.Unavailable, "The published chat link accepts text, not pictures.")], null);
    public LlmRuntimeSnapshot GetSnapshot() => new("Circle AI service", _version, "This device", false, [], StatusMessage);

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (_sending) return; // Do not let readiness polling compete with the active Binder turn.
        try
        {
            var status = await link.ReadChatStatusAsync(cancellationToken);
            IsReady = status.Ready;
            _acceptsQueuedRequests = status.AcceptsQueuedRequests;
            CanWaitForPreparation = status.CanWaitForPreparation || status.AcceptsQueuedRequests;
            _stage = status.Stage;
            CurrentDownloadProgress = status.PreparationFraction is double fraction
                ? new(fraction, status.Explanation) : null;
            _version = status.Version;
            _state = status.Ready ? ChatRuntimeCapabilityState.Available
                : status.Stage == "NeedsSetup" || status.Stage == "AuthorizationRequired"
                    ? ChatRuntimeCapabilityState.ActionRequired
                    : status.Stage == "Unavailable" ? ChatRuntimeCapabilityState.Unavailable : ChatRuntimeCapabilityState.Preparing;
            StatusMessage = status.Explanation;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            IsReady = false;
            _acceptsQueuedRequests = false;
            CanWaitForPreparation = false;
            CurrentDownloadProgress = null;
            _state = ChatRuntimeCapabilityState.Unavailable;
            StatusMessage = "Circle AI service could not respond on this device.";
        }
        _observed = DateTimeOffset.UtcNow;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _turn.WaitAsync(cancellationToken);
        try
        {
            // Read again at send time: the service may have restarted since the last UI poll.
            // Keep preparation outside the inference call, whose timeout is for answering.
            await RefreshAsync(cancellationToken);
            while (!_acceptsQueuedRequests && !IsReady && _stage != "Installed" && CanWaitForPreparation)
            {
                StatusMessage = "Your question is waiting on this device. " + StatusMessage;
                StatusChanged?.Invoke(this, EventArgs.Empty);
                await Task.Delay(PreparationPollInterval, cancellationToken);
                await RefreshAsync(cancellationToken);
            }
            // Installed means the service can load on demand; requiring an already-loaded
            // brain here would deadlock services that load only when a question arrives.
            if (!_acceptsQueuedRequests && !IsReady && !(_stage == "Installed" && CanWaitForPreparation))
                throw new ChatRuntimeUnavailableException(StatusMessage);
            var conversation = ToolApprovalRunContext.Current?.ConversationId
                ?? throw new InvalidOperationException("A durable conversation is required for Circle AI service.");
            var query = messages.LastOrDefault(m => m.Role == "user" || m.Role == "tool");
            if (query is null || string.IsNullOrWhiteSpace(query.Content))
                throw new InvalidOperationException("There is no query to send to Circle AI.");
            // The published service chat link accepts one user string and owns its
            // session history; it has no system-message field. Preserve only the
            // explicitly selected notebook reference, framed as quoted context for
            // this question. Do not forward the rest of Concierge's internal prompt
            // or pretend unsupported native actions have an approval bridge.
            var text = query.Role == "tool" ? "Concierge tool result:\n" + query.Content : query.Content;
            var systemPrompt = messages.LastOrDefault(message => message.Role == "system")?.Content;
            var notebookContext = systemPrompt is null ? null : NotebookContext.ExtractForService(systemPrompt);
            if (!string.IsNullOrWhiteSpace(notebookContext))
                text = $"Use the following selected notebook excerpts as the source of truth for notebook questions. Do not fill gaps with guesses.\n{notebookContext}\n\nPerson's question: {text}";
            _sending = true;
            CircleAiServiceChatReply reply;
            void Progress(string sentence)
            {
                StatusMessage = sentence;
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }
            var progressSource = link as ICircleAiServiceRequestProgress;
            if (progressSource is not null) progressSource.RequestProgress += Progress;
            try { reply = await link.SendChatAsync(conversation.ToString("D"), text, agentic: false, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                RecordFailure("Circle AI service could not answer on this device.");
                throw new ChatRuntimeUnavailableException(StatusMessage, error);
            }
            finally { if (progressSource is not null) progressSource.RequestProgress -= Progress; }
            if (!reply.Ok || string.IsNullOrWhiteSpace(reply.Text))
            {
                RecordFailure(reply.Error ?? "Circle AI service returned no answer.");
                throw new ChatRuntimeUnavailableException(StatusMessage);
            }
            yield return reply.Text;
        }
        finally { _sending = false; _turn.Release(); }
    }
    private void RecordFailure(string message)
    {
        IsReady = false;
        CanWaitForPreparation = false;
        CurrentDownloadProgress = null;
        _state = ChatRuntimeCapabilityState.Unavailable;
        StatusMessage = message;
        _observed = DateTimeOffset.UtcNow;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class CircleAiSharedServiceChatLoader(CircleAiSharedServiceChatRuntime runtime) : IHostedService
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _polling;
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _polling = Task.Run(async () =>
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await runtime.RefreshAsync(_stop.Token);
                    await Task.Delay(TimeSpan.FromSeconds(10), _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        });
        return Task.CompletedTask;
    }
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stop.Cancel();
        if (_polling is not null) await _polling.WaitAsync(cancellationToken);
    }
}
