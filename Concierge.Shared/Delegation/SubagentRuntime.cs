using System.Collections.Concurrent;

namespace Concierge.Shared.Delegation;

/// <summary>What came back from a delegated job.</summary>
/// <param name="Success">Whether the child finished the work.</param>
/// <param name="Output">What it produced.</param>
/// <param name="Error">Why it did not, when it did not.</param>
/// <param name="ProviderName">Where it ran.</param>
public sealed record SubagentResult(bool Success, string Output, string? Error, string ProviderName);

/// <summary>
/// Somewhere a self-contained job can be run. Implementations decide where that is.
/// </summary>
/// <remarks>
/// The interface is deliberately narrow — one text task in, one text answer out — because
/// the implementations vary so widely. A child chat runtime in this process, a second model
/// on the same device, and a peer phone reached over the mesh all fit behind it, and no
/// caller has to know which it got.
/// </remarks>
public interface ISubagentProvider
{
    /// <summary>Run one self-contained task and return what it produced.</summary>
    Task<string> RunAsync(string task, CancellationToken cancellationToken = default);
}

/// <summary>
/// Hands a job to a named provider and brings the answer back.
/// </summary>
/// <remarks>
/// Failures come back as results, never as exceptions. A delegated job that dies must not
/// take the conversation that asked for it down too — the parent needs to be able to tell
/// the user what happened and offer something else.
/// </remarks>
public interface ISubagentRuntime
{
    /// <summary>The providers currently registered.</summary>
    IReadOnlyCollection<string> Providers { get; }

    /// <summary>Register somewhere jobs can be sent.</summary>
    void RegisterProvider(string name, ISubagentProvider provider);

    /// <summary>Send one job to a named provider.</summary>
    Task<SubagentResult> DelegateAsync(string providerName, string task, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class SubagentRuntime : ISubagentRuntime
{
    private readonly ConcurrentDictionary<string, ISubagentProvider> _providers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public IReadOnlyCollection<string> Providers => _providers.Keys.ToList();

    /// <inheritdoc />
    public void RegisterProvider(string name, ISubagentProvider provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(provider);
        _providers[name] = provider;
    }

    /// <inheritdoc />
    public async Task<SubagentResult> DelegateAsync(
        string providerName,
        string task,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            return new SubagentResult(false, string.Empty, "There is no task to delegate.", providerName);
        }

        if (!_providers.TryGetValue(providerName, out var provider))
        {
            return new SubagentResult(
                false,
                string.Empty,
                $"No subagent provider named '{providerName}' is registered.",
                providerName);
        }

        try
        {
            var output = await provider.RunAsync(task.Trim(), cancellationToken).ConfigureAwait(false);
            return new SubagentResult(true, output, null, providerName);
        }
        catch (OperationCanceledException)
        {
            return new SubagentResult(false, string.Empty, "The delegated job was stopped.", providerName);
        }
        catch (Exception exception)
        {
            // A child that dies is a failed job, not a failed conversation. The parent
            // still has a user waiting who needs to be told something.
            return new SubagentResult(false, string.Empty, exception.Message, providerName);
        }
    }
}

/// <summary>
/// Repeats a round of work until it reports itself finished or the budget runs out.
/// </summary>
/// <remarks>
/// The budget is not optional. A model deciding for itself when a job is done will sometimes
/// decide never, and an unbounded loop on a phone is a flat battery.
/// </remarks>
public sealed class IterativeRunner
{
    private readonly int _maxRounds;

    /// <param name="maxRounds">How many rounds may run before the loop gives up.</param>
    public IterativeRunner(int maxRounds = 8)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRounds, 1);
        _maxRounds = maxRounds;
    }

    /// <summary>
    /// Run rounds until <paramref name="round"/> returns true or the budget is spent, and
    /// report how many rounds ran.
    /// </summary>
    /// <remarks>
    /// A throwing round ends the loop rather than being retried: a structural failure will
    /// reproduce every time, and repeating it only spends the budget before telling anyone.
    /// </remarks>
    public async Task<int> RunUntilDoneAsync(
        Func<CancellationToken, Task<bool>> round,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(round);

        for (var completed = 1; completed <= _maxRounds; completed++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await round(cancellationToken).ConfigureAwait(false))
            {
                return completed;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        return _maxRounds;
    }
}
