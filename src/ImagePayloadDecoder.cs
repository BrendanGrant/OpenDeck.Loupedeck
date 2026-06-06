using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenDeck.Loupedeck;

internal static class ImagePayloadDecoder
{
    public static Rgb565Canvas DecodeToCanvas(string imagePayload, int width, int height)
    {
        var bytes = DecodeBytes(imagePayload);
        using var stream = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var transformed = new TransformedBitmap(frame, new ScaleTransform(
            width / (double)frame.PixelWidth,
            height / (double)frame.PixelHeight));
        var converted = new FormatConvertedBitmap(transformed, PixelFormats.Bgra32, null, 0);

        var stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        var canvas = new Rgb565Canvas(width, height, RgbColor.Black);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 4;
                canvas.SetPixel(x, y, new RgbColor(
                    pixels[offset + 2],
                    pixels[offset + 1],
                    pixels[offset]));
            }
        }
        return canvas;
    }

    public static RgbColor DecodeAverageColor(string imagePayload)
    {
        var bytes = DecodeBytes(imagePayload);
        using var stream = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        long red = 0;
        long green = 0;
        long blue = 0;
        var count = width * height;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            blue += pixels[offset];
            green += pixels[offset + 1];
            red += pixels[offset + 2];
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
