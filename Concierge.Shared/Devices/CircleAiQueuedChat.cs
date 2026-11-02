using CircleAI.Assistant.Requests;
using CircleAI.Linking;
using System.Diagnostics;

namespace Concierge.Shared.Devices;

/// <summary>Follows one service-owned ask; observation never resubmits it.</summary>
public static class CircleAiQueuedChat
{
    internal const int StatusWaitMilliseconds = 250;

    public static bool IsSupported(LinkRowsReply hello)
    {
        if (!hello.Ok) return false;
        var (protocol, _, _, _, verbs) = LinkAskRows.ReadHello(hello.Rows);
        return protocol >= 2 && verbs.Contains(nameof(LinkVerb.AskSubmit))
            && verbs.Contains(nameof(LinkVerb.AskStatus));
    }

    public static async Task<CircleAiServiceChatReply> SendAsync(
        Func<LinkVerbRequest, CancellationToken, Task<LinkRowsReply>> execute,
        string requestId, string sessionId, string message, string question, bool agentic,
        Action<string>? progress, CancellationToken cancellationToken, Action<string>? answerChunk = null,
        bool background = false)
    {
        string? askId = null;
        var answerOffset = 0;
        var answerLength = 0;
        var answer = new System.Text.StringBuilder();
        try
        {
            var observation = Stopwatch.StartNew();
            var response = await execute(new(LinkVerb.AskSubmit, Args: new Dictionary<string, string>
            {
                [LinkAskRows.ArgRequestId] = requestId,
                [LinkAskRows.ArgSession] = sessionId,
                [LinkAskRows.ArgMessage] = message,
                // The service's shortcuts classify ArgQuestion. Do not let a
                // weather word in earlier conversation context replace an
                // unrelated follow-up before the model sees it.
                [LinkAskRows.ArgQuestion] = question,
                [LinkAskRows.ArgLane] = background ? "Background" : "Foreground",
                [LinkAskRows.ArgAgentic] = agentic ? "1" : "0",
                [LinkAskRows.ArgSideEffectFree] = agentic ? "0" : "1"
            }), cancellationToken);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!response.Ok) return new(false, null, response.Error ?? "Circle AI could not read the request.");
                var ask = response.Rows.Select(LinkAskRows.ReadAsk).FirstOrDefault(a => a is not null);
                if (ask is null || string.IsNullOrWhiteSpace(ask.Id))
                    return new(false, null, "Circle AI returned no request record.");
                if (askId is not null && ask.Id != askId)
                    return new(false, null, "Circle AI returned a different request record.");
                askId = ask.Id;
                progress?.Invoke(DescribeProgress(ask));
                answerLength = Math.Max(answerLength, ask.TextLength);
                if (ask.TextOffset == answerOffset && !string.IsNullOrEmpty(ask.Text))
                {
                    answer.Append(ask.Text);
                    answerOffset += ask.Text.Length;
                    answerChunk?.Invoke(ask.Text);
                }
                if (ask.State == AskState.Succeeded)
                    return answerOffset < answerLength
                        ? await ContinueFinalTextAsync(execute, askId, answer.ToString(), answerOffset, answerLength, answerChunk, cancellationToken)
                        : answer.Length == 0
                        ? new(false, null, "Circle AI completed the request without an answer.")
                        : new(true, answer.ToString(), null);
                if (ask.IsFinal) return new(false, null,
                    string.IsNullOrWhiteSpace(ask.Sentence) ? $"Circle AI request {ask.StateName}." : ask.Sentence);

                // Always request the whole answer, so no fragment can be lost or doubled.
                // There is no preparation deadline and no second AskSubmit on a poll.
                // Long polling already waits at the service. Only throttle fast replies;
                // do not add another quarter second after a blocking observation.
                var pause = PollDelay(observation.Elapsed);
                if (pause > TimeSpan.Zero) await Task.Delay(pause, cancellationToken);
                observation.Restart();
                response = await execute(new(LinkVerb.AskStatus, Query: askId, Args: new Dictionary<string, string>
                {
                    [LinkAskRows.ArgSince] = answerOffset.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    [LinkAskRows.ArgAfterSeq] = ask.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    [LinkAskRows.ArgWaitMs] = StatusWaitMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (askId is not null)
            {
                // A person's Stop withdraws this ask, not downloads or other clients' work.
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await execute(new(LinkVerb.AskCancel, Query: askId), stop.Token); }
                catch (Exception) { /* Caller still observes cancellation; no automatic resubmission. */ }
            }
            throw;
        }
    }

    private static async Task<CircleAiServiceChatReply> ContinueFinalTextAsync(
        Func<LinkVerbRequest, CancellationToken, Task<LinkRowsReply>> execute, string askId,
        string existing, int offset, int total, Action<string>? answerChunk, CancellationToken cancellationToken)
    {
        var answer = new System.Text.StringBuilder(existing);
        while (offset < total)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await execute(new(LinkVerb.AskStatus, Query: askId, Args: new Dictionary<string, string>
            {
                [LinkAskRows.ArgSince] = offset.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [LinkAskRows.ArgAfterSeq] = "0",
                [LinkAskRows.ArgWaitMs] = StatusWaitMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }), cancellationToken);
            if (!response.Ok) return new(false, null, response.Error ?? "Circle AI’s completed answer could not be read.");
            var ask = response.Rows.Select(LinkAskRows.ReadAsk).FirstOrDefault(item => item is not null);
            if (ask is null || ask.IsFinal && ask.State != AskState.Succeeded)
                return new(false, null, "Circle AI’s answer could not be fully recovered.");
            if (ask.TextOffset == offset && !string.IsNullOrEmpty(ask.Text))
            {
                answer.Append(ask.Text);
                offset += ask.Text.Length;
                answerChunk?.Invoke(ask.Text);
            }
            else return new(false, null, "Circle AI could not return the remaining answer text.");
            total = Math.Max(total, ask.TextLength);
        }
        return answer.Length == 0
            ? new(false, null, "Circle AI completed the request without an answer.")
            : new(true, answer.ToString(), null);
    }

    internal static TimeSpan PollDelay(TimeSpan elapsed)
        => elapsed < TimeSpan.FromMilliseconds(StatusWaitMilliseconds)
            ? TimeSpan.FromMilliseconds(StatusWaitMilliseconds) - elapsed : TimeSpan.Zero;

    internal static string DescribeProgress(LinkAskView ask)
    {
        // Preserve a useful service explanation. Older services return only a
        // state word, which is not a sentence a person can act on or understand.
        if (!string.IsNullOrWhiteSpace(ask.Sentence)
            && !string.Equals(ask.Sentence, ask.StateName, StringComparison.OrdinalIgnoreCase))
            return ask.Sentence;

        return ask.State switch
        {
            AskState.Queued => "Circle AI has your question and is waiting to start.",
            AskState.WaitingForSetup => "Circle AI is preparing to answer.",
            AskState.WaitingForModel => "Circle AI is preparing a model.",
            AskState.WaitingForPermission => string.IsNullOrWhiteSpace(ask.NeedsPermission)
                ? "Circle AI is waiting for a permission before it can answer."
                : $"Circle AI is waiting for permission: {ask.NeedsPermission}.",
            AskState.Running => "Circle AI is processing your question.",
            AskState.Paused => "Circle AI paused this question and will resume it when the device allows.",
            AskState.Succeeded => "Circle AI has finished the answer.",
            _ => string.IsNullOrWhiteSpace(ask.Sentence) ? ask.StateName : ask.Sentence
        };
    }
}
