using Android.Graphics;
using Concierge.Shared.Design;
using Color = Android.Graphics.Color;
using Paint = Android.Graphics.Paint;

namespace Concierge.Platforms.Android;

/// <summary>
/// Android's bundled FFmpeg has no drawtext filter. Draw the same 1280×720 cards
/// with the platform canvas before FFmpeg turns them into timed video shots.
/// </summary>
public sealed class AndroidCardRenderer : ICardRenderer
{
    public Task RenderAsync(string words, DesignLook look, string outputPath,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var bitmap = Bitmap.CreateBitmap(1280, 720, Bitmap.Config.Argb8888!);
        using var canvas = new Canvas(bitmap);
        canvas.DrawColor(Color.ParseColor(look.Ground));

        if (!string.IsNullOrWhiteSpace(words))
        {
            using var paint = new Paint(PaintFlags.AntiAlias)
            {
                Color = Color.ParseColor(look.Ink),
                TextAlign = Paint.Align.Center,
                TextSize = 56
            };
            using var face = Typeface.Create("sans-serif", TypefaceStyle.Normal);
            paint.SetTypeface(face);
            var lines = words.Split('\n');
            var baseline = 360f - ((lines.Length - 1) * 72f / 2f) + 20f;
            foreach (var line in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                canvas.DrawText(line.TrimEnd('\r'), 640f, baseline, paint);
                baseline += 72f;
            }
        }

        using var output = File.Create(outputPath);
        if (!bitmap.Compress(Bitmap.CompressFormat.Png!, 100, output))
            throw new IOException("Android could not render the video card.");
    }, cancellationToken);
}
