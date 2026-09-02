using System.Text.Json;

namespace Concierge.Shared.Structured;

/// <summary>The outcome of trying to read a typed value out of a model's reply.</summary>
/// <param name="Success">Whether a value was recovered.</param>
/// <param name="Value">The value, or null when it was not.</param>
/// <param name="Error">Why it failed, in words a developer can act on.</param>
/// <param name="RawOutput">What the model actually said, kept either way.</param>
public sealed record StructuredOutputResult<T>(bool Success, T? Value, string? Error, string RawOutput);

/// <summary>
/// Recovers a typed value from a model's free text.
/// </summary>
/// <remarks>
/// A caller that needs an object cannot use prose. Small models produce JSON wrapped in
/// explanations, fenced in markdown, or with a trailing comma — all recoverable. What is not
/// recoverable must fail with the raw text attached, because a refusal and a formatting slip
/// need different responses and only the original words tell them apart.
/// </remarks>
public interface IStructuredOutputParser
{
    /// <summary>Try to read a <typeparamref name="T"/> out of what the model said.</summary>
    StructuredOutputResult<T> Parse<T>(string? output);
}

/// <summary>
/// Finds the first JSON object in the text and reads it, tolerating the formatting mistakes
/// small models reliably make.
/// </summary>
/// <remarks>
/// Tolerant about wrapping, strict about content. Comments and trailing commas are allowed
/// because they are presentation slips; a missing value is not, because filling it in would
/// mean inventing data the model never produced.
/// </remarks>
public sealed class LenientJsonOutputParser : IStructuredOutputParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <inheritdoc />
    public StructuredOutputResult<T> Parse<T>(string? output)
    {
        var raw = output ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new StructuredOutputResult<T>(false, default, "The model returned nothing.", raw);
        }

        var candidate = ExtractFirstObject(raw);
        if (candidate is null)
        {
            return new StructuredOutputResult<T>(
                false,
                default,
                "No JSON object was found in the model's reply.",
                raw);
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(candidate, JsonOptions);
            return value is null
                ? new StructuredOutputResult<T>(false, default, "The JSON parsed to nothing.", raw)
                : new StructuredOutputResult<T>(true, value, null, raw);
        }
        catch (JsonException exception)
        {
            return new StructuredOutputResult<T>(false, default, exception.Message, raw);
        }
    }

    /// <summary>
    /// Returns the first balanced <c>{ … }</c> span, ignoring braces inside strings so a
    /// value containing one does not end the object early.
    /// </summary>
    private static string? ExtractFirstObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var index = start; index < text.Length; index++)
        {
            var character = text[index];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (character == '\\' && inString)
            {
                escaped = true;
                continue;
            }

            if (character == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (character == '{')
            {
                depth++;
            }
            else if (character == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text[start..(index + 1)];
                }
            }
        }

        // Unbalanced: the model was cut off mid-object. Better to fail than to guess where
        // it meant to close.
        return null;
    }
}
