using System.Runtime.CompilerServices;

namespace Concierge.Shared.Chat;

/// <summary>What happened, for the surface that has to say so.</summary>
/// <param name="Runtime">The runtime that produced the reply.</param>
/// <param name="FellBackFrom">
/// The ones that failed first, in order, or empty if the first choice answered.
/// </param>
public sealed record FailoverOutcome(IChatRuntime Runtime, IReadOnlyList<string> FellBackFrom);

/// <summary>
/// Trying the next provider when one falls over.
///
/// Concierge had none of this: three cloud runtimes and a local one, and whichever
/// was selected was the only one that would ever be asked. A provider having a bad
/// afternoon was a dead turn — a stream error pasted into the thread and nothing
/// else attempted, even with two other configured providers sitting idle.
///
/// Three rules, and each of them is a decision rather than an implementation detail.
///
/// **Never off the device.** A local runtime failing must not quietly continue on
/// somebody else's computer. Concierge's whole claim is that it works on your own
/// machine, and a failover that silently posts the conversation to a cloud provider
/// would break that promise at exactly the moment nobody is watching — during an
/// error. So a runtime that does not leave the device only ever falls back to
/// another that does not either, which today means it does not fall back at all.
/// Making that configurable is a real feature and it needs a person to agree to it,
/// out loud, in Settings.
///
/// **Only past a failure, never past a refusal.** A model that declines to answer
/// has answered. Asking the next provider until one agrees is not resilience, it is
/// shopping for a yes, and it would quietly defeat every safety judgement any of
/// them make.
///
/// **Only before the first token.** Once text has been shown, the reply is
/// underway. Switching provider mid-sentence and continuing from a different model
/// produces something neither of them said, spliced at an invisible seam. A stream
/// that dies after emitting anything keeps what it emitted and stops.
/// </summary>
public static class RuntimeFailover
{
    /// <summary>
    /// Streams from <paramref name="first"/>, moving to the next eligible runtime if
    /// it fails before producing anything.
    ///
    /// The outcome is reported through <paramref name="onOutcome"/> rather than
    /// returned, because this is an async stream and the caller needs to know which
    /// runtime answered in order to label the message it stores. A reply attributed
    /// to the wrong engine is worse than no label: the transcript is the record of
    /// what happened, and it would be wrong.
    /// </summary>
    public static async IAsyncEnumerable<string> StreamAsync(
        IChatRuntime first,
        IReadOnlyList<IChatRuntime> alternatives,
        IReadOnlyList<ChatTurn> messages,
        Action<FailoverOutcome>? onOutcome = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(first);

        var chain = Chain(first, alternatives ?? []);
        var failed = new List<string>();

        for (var i = 0; i < chain.Count; i++)
        {
            var runtime = chain[i];
            var last = i == chain.Count - 1;
            var produced = false;

            var enumerator = runtime.StreamAsync(messages, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            var broke = false;

            // try/finally rather than try/catch around the yield: C# forbids
            // yielding from inside a try that has a catch, so each MoveNextAsync is
            // wrapped on its own and the yield happens after it. The alternative —
            // buffering the whole reply so it can be wrapped — would throw away
            // streaming, which is most of what makes a slow model bearable.
            try
            {
                while (true)
                {
                    string chunk;

                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        {
                            break;
                        }

                        chunk = enumerator.Current;
                    }
                    catch (OperationCanceledException)
                    {
                        // Somebody pressed stop. That is not a provider failing, and
                        // asking the next one would be the opposite of what they
                        // asked for.
                        throw;
                    }
                    catch (Exception) when (!produced && !last)
                    {
                        broke = true;
                        break;
                    }

                    produced = true;
                    yield return chunk;
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            if (broke)
            {
                failed.Add(runtime.EngineLabel);
                continue;
            }

            // Either it produced something, or it finished cleanly with nothing to
            // say — which is an answer, not a failure, and not a reason to go and
            // ask somebody else the same question.
            onOutcome?.Invoke(new FailoverOutcome(runtime, failed));
            yield break;
        }
    }

    /// <summary>
    /// The order things will be tried in: the chosen runtime, then every other one
    /// that is ready, is not the same provider, and does not take the conversation
    /// somewhere the chosen one would not have.
    /// </summary>
    public static IReadOnlyList<IChatRuntime> Chain(
        IChatRuntime first, IReadOnlyList<IChatRuntime> alternatives)
    {
        var chain = new List<IChatRuntime> { first };

        foreach (var candidate in alternatives)
        {
            if (candidate is null || ReferenceEquals(candidate, first))
            {
                continue;
            }

            if (string.Equals(candidate.Id, first.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The rule that matters. A local runtime never falls back to a remote
            // one; a remote one may fall back to another remote one, because the
            // conversation was already leaving the device when the person chose it.
            if (candidate.LeavesDevice && !first.LeavesDevice)
            {
                continue;
            }

            if (!candidate.IsReady)
            {
                continue;
            }

            chain.Add(candidate);
        }

        return chain;
    }
}
