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

            var (picture, refused) = PictureIn(body);

            if (refused is not null)
            {
                return Results.BadRequest(new AwayAnswer(false, string.Empty, refused));
            }

            return Results.Json(await away
                .SayAsync(new AwaySaid(body.Text, context, picture), cancellationToken)
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

        // What changed last, and taking it back. **The screen a watch was always for**: a
        // canvas does not belong on a 192dp face, but glancing at what changed and saying
        // "no, not like that" fits a wrist better than anything else does.
        app.MapGet("/api/away/change", async (IAway away, CancellationToken cancellationToken) =>
        {
            var change = await away.LastChangeAsync(cancellationToken).ConfigureAwait(false);

            // Nothing has changed, which is a fact rather than a failure — a wrist glancing at
            // an untouched design should be told so, not shown an error.
            return change is null ? Results.NoContent() : Results.Json(change);
        });

        app.MapPost("/api/away/undo", async (IAway away, CancellationToken cancellationToken) =>
        {
            var change = await away.UndoAsync(cancellationToken).ConfigureAwait(false);

            return change is null ? Results.NoContent() : Results.Json(change);
        });

        return app;
    }

    /// <summary>
    /// How big a picture may be.
    ///
    /// The paperclip's own cap, and the same reasoning: base64 is a third larger than the
    /// file, and this one crosses a network a watch or a phone is paying for. A phone camera
    /// makes far more than this, so the device is expected to send something smaller rather
    /// than the raw frame — and it is told so rather than left wondering.
    /// </summary>
    public const int BiggestPicture = 4 * 1024 * 1024;

    /// <summary>
    /// The picture, if there is one, with its kind read from the bytes.
    ///
    /// **Never from what the caller said it was.** A file called photo.jpg that is not a JPEG
    /// would otherwise be handed to a model as one, and the same helper already guards the
    /// paperclip and the vision path — one answer to "is this a picture", not a third.
    /// </summary>
    private static (AwayPicture? Picture, string? Refused) PictureIn(SaidBody body)
    {
        if (string.IsNullOrWhiteSpace(body.PictureBase64))
        {
            return (null, null);
        }

        byte[] bytes;

        try
        {
            bytes = Convert.FromBase64String(body.PictureBase64);
        }
        catch (FormatException)
        {
            return (null, "The picture was not valid base64.");
        }

        if (bytes.Length == 0)
        {
            return (null, "The picture was empty.");
        }

        if (bytes.Length > BiggestPicture)
        {
            return (null, $"That picture is {bytes.Length / (1024 * 1024)} MB. Send one under "
                + $"{BiggestPicture / (1024 * 1024)} MB.");
        }

        if (Concierge.Shared.Attachments.AttachmentKind.ImageMediaType(bytes) is not { } kind)
        {
            return (null, "Those bytes are not a picture this can read.");
        }

        return (new AwayPicture(
            string.IsNullOrWhiteSpace(body.PictureName) ? "picture" : body.PictureName, kind, bytes), null);
    }

    /// <param name="At">When the device heard it. Its clock, not this one's.</param>
    /// <param name="PictureBase64">What the device was looking at, when it has an eye.</param>
    public sealed record SaidBody(
        string? Text,
        string? Device,
        DateTimeOffset? At,
        double? Latitude,
        double? Longitude,
        string? Motion,
        int? HeartRate,
        double? AmbientLux,
        string? PictureBase64 = null,
        string? PictureName = null);

    public sealed record AnswerBody(Guid Id, bool Allowed);
}
