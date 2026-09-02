using Concierge.Shared.Structured;

namespace Concierge.Tests;

/// <summary>
/// What structured output must do (parity feature 49): turn a model's prose into a validated
/// object, or say clearly that it could not.
/// </summary>
/// <remarks>
/// This is the highest-value guard when the model is small. A 0.6B model asked for JSON will
/// wrap it in prose, fence it in markdown, or add a trailing comma. Parsing that leniently
/// is the difference between a feature that works on a phone and one that only works in a
/// demo — but a value that cannot be recovered must fail loudly, never be guessed.
/// </remarks>
public sealed class StructuredOutputTests
{
    private sealed record Answer(string Name, int Age);

    private readonly IStructuredOutputParser _parser = new LenientJsonOutputParser();

    [Fact]
    public void Clean_json_is_read()
    {
        var result = _parser.Parse<Answer>("""{"name":"Lebo","age":9}""");

        Assert.True(result.Success);
        Assert.Equal("Lebo", result.Value!.Name);
    }

    [Fact]
    public void Json_in_a_markdown_fence_is_read()
    {
        var result = _parser.Parse<Answer>("""
            ```json
            {"name":"Lebo","age":9}
            ```
            """);

        Assert.True(result.Success);
        Assert.Equal(9, result.Value!.Age);
    }

    [Fact]
    public void Json_in_an_unlabelled_fence_is_read()
    {
        var result = _parser.Parse<Answer>("```\n{\"name\":\"Lebo\",\"age\":9}\n```");

        Assert.True(result.Success);
    }

    [Fact]
    public void Json_with_prose_around_it_is_read()
    {
        var result = _parser.Parse<Answer>("""
            Sure! Here is the answer you asked for:
            {"name":"Lebo","age":9}
            Let me know if you need anything else.
            """);

        Assert.True(result.Success);
        Assert.Equal("Lebo", result.Value!.Name);
    }

    [Fact]
    public void A_trailing_comma_is_tolerated()
    {
        var result = _parser.Parse<Answer>("""{"name":"Lebo","age":9,}""");

        Assert.True(result.Success);
    }

    [Fact]
    public void A_comment_is_tolerated()
    {
        var result = _parser.Parse<Answer>("""
            {
              // the child's name
              "name":"Lebo",
              "age":9
            }
            """);

        Assert.True(result.Success);
    }

    [Fact]
    public void Nothing_resembling_an_object_fails()
    {
        var result = _parser.Parse<Answer>("I'm afraid I can't help with that.");

        Assert.False(result.Success);
        Assert.Null(result.Value);
    }

    [Fact]
    public void A_failure_says_what_was_wrong()
    {
        var result = _parser.Parse<Answer>("not json at all");

        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void A_failure_keeps_what_the_model_actually_said()
    {
        // Without the raw text there is no way to tell a refusal from a formatting slip.
        var result = _parser.Parse<Answer>("I'm afraid I can't help with that.");

        Assert.Contains("can't help", result.RawOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Broken_json_fails_rather_than_being_guessed()
    {
        var result = _parser.Parse<Answer>("""{"name":"Lebo","age":}""");

        Assert.False(result.Success);
    }

    [Fact]
    public void Empty_output_fails()
    {
        Assert.False(_parser.Parse<Answer>("   ").Success);
    }

    [Fact]
    public void The_first_object_wins_when_the_model_offers_several()
    {
        var result = _parser.Parse<Answer>("""
            {"name":"First","age":1}
            {"name":"Second","age":2}
            """);

        Assert.Equal("First", result.Value!.Name);
    }
}
