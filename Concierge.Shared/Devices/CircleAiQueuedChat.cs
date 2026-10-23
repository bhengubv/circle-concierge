using CircleAI.Assistant.Requests;
using CircleAI.Linking;

namespace Concierge.Shared.Devices;

/// <summary>Follows one service-owned ask; observation never resubmits it.</summary>
public static class CircleAiQueuedChat
{
    public static bool IsSupported(LinkRowsReply hello)
    {
        if (!hello.Ok) return false;
        var (protocol, _, _, _, verbs) = LinkAskRows.ReadHello(hello.Rows);
        return protocol >= 2 && verbs.Contains(nameof(LinkVerb.AskSubmit))
            && verbs.Contains(nameof(LinkVerb.AskStatus));
    }

    public static async Task<CircleAiServiceChatReply> SendAsync(
        Func<LinkVerbRequest, CancellationToken, Task<LinkRowsReply>> execute,
        string requestId, string sessionId, string message, bool agentic,
        Action<string>? progress, CancellationToken cancellationToken)
    {
        string? askId = null;
        try
        {
            var response = await execute(new(LinkVerb.AskSubmit, Args: new Dictionary<string, string>
            {
                [LinkAskRows.ArgRequestId] = requestId,
                [LinkAskRows.ArgSession] = sessionId,
                [LinkAskRows.ArgMessage] = message,
                [LinkAskRows.ArgQuestion] = message,
                [LinkAskRows.ArgLane] = "Foreground",
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
                progress?.Invoke(string.IsNullOrWhiteSpace(ask.Sentence) ? ask.StateName : ask.Sentence);
                if (ask.State == AskState.Succeeded)
                    return string.IsNullOrWhiteSpace(ask.Text)
                        ? new(false, null, "Circle AI completed the request without an answer.")
                        : new(true, ask.Text, null);
                if (ask.IsFinal) return new(false, null,
                    string.IsNullOrWhiteSpace(ask.Sentence) ? $"Circle AI request {ask.StateName}." : ask.Sentence);

                // Always request the whole answer, so no fragment can be lost or doubled.
                // There is no preparation deadline and no second AskSubmit on a poll.
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                response = await execute(new(LinkVerb.AskStatus, Query: askId, Args: new Dictionary<string, string>
                {
                    [LinkAskRows.ArgSince] = "0",
                    [LinkAskRows.ArgAfterSeq] = ask.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    [LinkAskRows.ArgWaitMs] = "1000"
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
}
