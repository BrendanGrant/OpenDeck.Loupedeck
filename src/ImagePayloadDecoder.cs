using SkiaSharp;

namespace OpenDeck.Loupedeck;

internal static class ImagePayloadDecoder
{
    public static Rgb565Canvas DecodeToCanvas(string imagePayload, int width, int height)
    {
        var bytes = DecodeBytes(imagePayload);
        using var bitmap = SKBitmap.Decode(bytes) ?? throw new InvalidOperationException("Unable to decode image payload.");
        using var resized = bitmap.Resize(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.High)
            ?? throw new InvalidOperationException("Unable to resize image payload.");

        var canvas = new Rgb565Canvas(width, height, RgbColor.Black);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var color = resized.GetPixel(x, y);
                canvas.SetPixel(x, y, new RgbColor(color.Red, color.Green, color.Blue));
            }
        }

        return canvas;
    }

    public static RgbColor DecodeAverageColor(string imagePayload)
    {
        var bytes = DecodeBytes(imagePayload);
        using var bitmap = SKBitmap.Decode(bytes) ?? throw new InvalidOperationException("Unable to decode image payload.");

        long red = 0;
        long green = 0;
        long blue = 0;
        var count = bitmap.Width * bitmap.Height;
        if (count == 0)
            return RgbColor.Black;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                red += color.Red;
                green += color.Green;
                blue += color.Blue;
            }
        }

        return new RgbColor(
            (byte)(red / count),
            (byte)(green / count),
            (byte)(blue / count));
    }

    private static byte[] DecodeBytes(string imagePayload)
    {
        var comma = imagePayload.IndexOf(',');
        var base64 = comma >= 0 ? imagePayload[(comma + 1)..] : imagePayload;
        return Convert.FromBase64String(base64);
    }
}
