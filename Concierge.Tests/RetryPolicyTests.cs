using System.Net;
using Concierge.Shared.Resilience;

namespace Concierge.Tests;

/// <summary>
/// What retrying must do (parity feature 37): survive the failures that a phone on a weak
/// link produces constantly, without hiding the ones that mean something.
/// </summary>
/// <remarks>
/// The distinction that matters is retryable versus not. A dropped connection is worth
/// trying again; a rejected API key is not, and retrying it three times only delays telling
/// the user what is actually wrong.
/// </remarks>
public sealed class RetryPolicyTests
{
    private readonly IRetryPolicy _policy = new BoundedRetryPolicy(maxAttempts: 3, delay: TimeSpan.Zero);

    [Fact]
    public async Task Work_that_succeeds_is_not_retried()
    {
        var attempts = 0;

        await _policy.ExecuteAsync(_ =>
        {
            attempts++;
            return Task.FromResult("fine");
        });

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Work_that_succeeds_returns_its_result()
    {
        Assert.Equal("fine", await _policy.ExecuteAsync(_ => Task.FromResult("fine")));
    }

    [Fact]
    public async Task A_transient_failure_is_tried_again()
    {
        var attempts = 0;

        var result = await _policy.ExecuteAsync(_ =>
        {
            attempts++;
            return attempts < 2
                ? throw new HttpRequestException("connection reset")
                : Task.FromResult("recovered");
        });

        Assert.Equal("recovered", result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Retrying_stops_at_the_attempt_limit()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<HttpRequestException>(() => _policy.ExecuteAsync<string>(_ =>
        {
            attempts++;
            throw new HttpRequestException("still down");
        }));

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task The_last_failure_is_what_the_caller_sees()
    {
        var attempts = 0;

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => _policy.ExecuteAsync<string>(_ =>
        {
            attempts++;
            throw new HttpRequestException($"failure {attempts}");
        }));

        Assert.Contains("failure 3", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_key_is_not_retried()
    {
        // Retrying an authentication failure wastes the user's time and tells them nothing.
        var attempts = 0;

        await Assert.ThrowsAsync<HttpRequestException>(() => _policy.ExecuteAsync<string>(_ =>
        {
            attempts++;
            throw new HttpRequestException("unauthorized", inner: null, HttpStatusCode.Unauthorized);
        }));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task A_bad_request_is_not_retried()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<HttpRequestException>(() => _policy.ExecuteAsync<string>(_ =>
        {
            attempts++;
            throw new HttpRequestException("bad request", inner: null, HttpStatusCode.BadRequest);
        }));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Being_rate_limited_is_retried()
    {
        var attempts = 0;

        var result = await _policy.ExecuteAsync(_ =>
        {
            attempts++;
            return attempts < 3
                ? throw new HttpRequestException("slow down", inner: null, HttpStatusCode.TooManyRequests)
                : Task.FromResult("through");
        });

        Assert.Equal("through", result);
    }

    [Fact]
    public async Task A_server_error_is_retried()
    {
        var attempts = 0;

        await _policy.ExecuteAsync(_ =>
        {
            attempts++;
            return attempts < 2
                ? throw new HttpRequestException("server fell over", inner: null, HttpStatusCode.InternalServerError)
                : Task.FromResult("through");
        });

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task A_programming_error_is_not_retried()
    {
        // Only failures that could plausibly be transient are worth repeating; a null
        // reference will fail identically three times.
        var attempts = 0;

        await Assert.ThrowsAsync<NullReferenceException>(() => _policy.ExecuteAsync<string>(_ =>
        {
            attempts++;
            throw new NullReferenceException();
        }));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Cancelling_stops_the_retrying()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _policy.ExecuteAsync<string>(_ =>
        {
            attempts++;
            cancellation.Cancel();
            throw new HttpRequestException("down");
        }, cancellation.Token));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void A_policy_that_never_retries_is_allowed()
    {
        Assert.Equal(1, new BoundedRetryPolicy(maxAttempts: 1, delay: TimeSpan.Zero).MaxAttempts);
    }

    [Fact]
    public void A_policy_with_no_attempts_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BoundedRetryPolicy(maxAttempts: 0, delay: TimeSpan.Zero));
    }
}
