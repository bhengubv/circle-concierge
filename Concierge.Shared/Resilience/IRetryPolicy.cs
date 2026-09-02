using System.Net;

namespace Concierge.Shared.Resilience;

/// <summary>
/// Repeats work that failed for a reason worth repeating.
/// </summary>
/// <remarks>
/// Concierge's users are on the weakest links in the product's market. A dropped request is
/// normal there, and surfacing the first one as an error trains people to distrust the app.
/// </remarks>
public interface IRetryPolicy
{
    /// <summary>How many times the work may be attempted in total.</summary>
    int MaxAttempts { get; }

    /// <summary>Run the work, repeating it while the failure looks transient.</summary>
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default);
}

/// <summary>
/// A fixed number of attempts with a constant delay between them.
/// </summary>
/// <remarks>
/// <para>
/// Only failures that could plausibly succeed next time are repeated: network faults,
/// timeouts, rate limits, and server errors. A rejected key, a malformed request, or a
/// programming error will fail identically every time, and retrying them only delays telling
/// the user what is really wrong.
/// </para>
/// <para>
/// Constant rather than exponential delay, deliberately. On a mesh or a 2G link the useful
/// retry window is short; backing off to thirty seconds means the user has given up first.
/// </para>
/// </remarks>
public sealed class BoundedRetryPolicy : IRetryPolicy
{
    private static readonly HttpStatusCode[] RetryableStatusCodes =
    [
        HttpStatusCode.RequestTimeout,
        HttpStatusCode.TooManyRequests,
        HttpStatusCode.InternalServerError,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout,
    ];

    private readonly TimeSpan _delay;

    /// <param name="maxAttempts">Total attempts, including the first. One means never retry.</param>
    /// <param name="delay">How long to wait between attempts.</param>
    public BoundedRetryPolicy(int maxAttempts = 3, TimeSpan? delay = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        MaxAttempts = maxAttempts;
        _delay = delay ?? TimeSpan.FromSeconds(1);
    }

    /// <inheritdoc />
    public int MaxAttempts { get; }

    /// <inheritdoc />
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await work(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt < MaxAttempts && IsWorthRepeating(exception))
            {
                // The caller cancelling during the attempt outranks the retry: stop now
                // rather than sleeping and trying again on a token that is already dead.
                cancellationToken.ThrowIfCancellationRequested();

                if (_delay > TimeSpan.Zero)
                {
                    await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>Whether this failure could plausibly succeed on another attempt.</summary>
    private static bool IsWorthRepeating(Exception exception) => exception switch
    {
        // A cancelled operation is the caller's decision, not a fault to retry.
        OperationCanceledException => false,
        TimeoutException => true,
        HttpRequestException http => http.StatusCode is null
            || RetryableStatusCodes.Contains(http.StatusCode.Value),
        IOException => true,
        _ => false,
    };
}
