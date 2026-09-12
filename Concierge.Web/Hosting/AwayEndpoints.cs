using Concierge.Shared.Away;

namespace Concierge.Web.Hosting;

/// <summary>
/// The door a device that is not here comes in through.
///
/// Guarded by the API key middleware that already wraps everything —
/// <c>X-Concierge-Key</c>, constant-time, a passthrough until <c>CONCIERGE_API_KEY</c> is
/// set. **Setting it matters more for these than for anything else here**: transcribing a
/// file is one thing, and running a turn with the whole tool catalogue in reach is another.
/// </summary>
public static class AwayEndpoints
{
    public static IEndpointRouteBuilder MapConciergeAway(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/away/say", async (
            SaidBody body,
            IAway away,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(body.Text))
            {
                return Results.BadRequest(new AwayAnswer(false, string.Empty, "Nothing was said."));
            }

            // The time comes from the device, not from here. A sentence said on a train and
            // delivered forty minutes later is still a sentence said on a train, and
            // stamping it on arrival would quietly throw that away — which is the one thing
            // the context is for.
            var context = new AwayContext(
                body.At ?? DateTimeOffset.UtcNow,
                string.IsNullOrWhiteSpace(body.Device) ? "device" : body.Device,
                body.Latitude,
                body.Longitude,
                body.Motion,
                body.HeartRate,
                body.AmbientLux);

            return Results.Json(await away
                .SayAsync(new AwaySaid(body.Text, context), cancellationToken)
                .ConfigureAwait(false));
        });

        app.MapGet("/api/away/waiting", async (IAway away, CancellationToken cancellationToken) =>
            Results.Json(await away.WaitingAsync(cancellationToken).ConfigureAwait(false)));

        app.MapPost("/api/away/answer", async (
            AnswerBody body,
            IAway away,
            CancellationToken cancellationToken) =>
        {
            var answered = await away.AnswerAsync(body.Id, body.Allowed, cancellationToken).ConfigureAwait(false);

            // False means it was no longer waiting — a turn that gave up while somebody
            // walked to the kitchen. Said plainly, because a face that reported "allowed"
            // for a call that never ran would be this repository's signature defect, on a
            // screen that has room for one sentence.
            return Results.Json(new { answered });
        });

        return app;
    }

    /// <param name="At">When the device heard it. Its clock, not this one's.</param>
    public sealed record SaidBody(
        string? Text,
        string? Device,
        DateTimeOffset? At,
        double? Latitude,
        double? Longitude,
        string? Motion,
        int? HeartRate,
        double? AmbientLux);

    public sealed record AnswerBody(Guid Id, bool Allowed);
}
