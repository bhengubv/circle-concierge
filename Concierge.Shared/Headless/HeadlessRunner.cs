using System.Text;
using Concierge.Shared.Chat;

namespace Concierge.Shared.Headless;

/// <summary>What one headless run produced.</summary>
/// <param name="Success">Whether the task was answered.</param>
/// <param name="Output">The answer.</param>
/// <param name="Error">Why there is no answer, when there is none.</param>
public sealed record HeadlessResult(bool Success, string Output, string? Error);

/// <summary>
/// Runs one task to completion and returns the answer, with no interface.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes the product scriptable and what makes <c>Concierge.Cli</c> a real
/// host rather than a status printer. It is also the shape a scheduled task needs: something
/// that can be handed a sentence at three in the morning and produce an answer nobody is
/// waiting to read.
/// </para>
/// <para>
/// Every failure is a returned result, never an exception. The caller is a script or a
/// background job, and a thrown exception there becomes a non-zero exit code with no
/// explanation.
/// </para>
/// </remarks>
public sealed class HeadlessRunner
{
    private readonly IChatRuntime _runtime;
    private readonly string? _persona;

    /// <param name="runtime">The engine to ask.</param>
    /// <param name="persona">Optional system prompt sent ahead of the task.</param>
    public HeadlessRunner(IChatRuntime runtime, string? persona = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _persona = string.IsNullOrWhiteSpace(persona) ? null : persona;
    }

    /// <summary>Answer one task.</summary>
    public async Task<HeadlessResult> RunAsync(string task, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            return new HeadlessResult(false, string.Empty, "There is no task to run.");
        }

        if (!_runtime.IsReady)
        {
            // Better a clear refusal than a run that blocks a script for as long as a model
            // takes to load, or streams the status line back as if it were an answer.
            return new HeadlessResult(false, string.Empty, _runtime.StatusMessage);
        }

        var turns = new List<ChatTurn>();
        if (_persona is not null)
        {
            turns.Add(new ChatTurn("system", _persona));
        }

        turns.Add(new ChatTurn("user", task.Trim()));

        try
        {
            var answer = new StringBuilder();
            await foreach (var chunk in _runtime.StreamAsync(turns, cancellationToken).ConfigureAwait(false))
            {
                answer.Append(chunk);
            }

            return new HeadlessResult(true, answer.ToString(), null);
        }
        catch (OperationCanceledException)
        {
            return new HeadlessResult(false, string.Empty, "The run was stopped.");
        }
        catch (Exception exception)
        {
            return new HeadlessResult(false, string.Empty, exception.Message);
        }
    }
}
