namespace OpenDeck.Loupedeck;

public readonly record struct RgbColor(byte Red, byte Green, byte Blue)
{
    public static readonly RgbColor Black = new(0, 0, 0);
    public static readonly RgbColor White = new(255, 255, 255);
}

public sealed class Rgb565Canvas
{
    public Rgb565Canvas(int width, int height, RgbColor background)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 2];
        Clear(background);
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public void Clear(RgbColor color) => FillRectangle(0, 0, Width, Height, color);

    public void Blit(int x, int y, Rgb565Canvas source)
    {
        for (var sourceY = 0; sourceY < source.Height; sourceY++)
        {
            var targetY = y + sourceY;
            if (targetY < 0 || targetY >= Height)
                continue;

            for (var sourceX = 0; sourceX < source.Width; sourceX++)
            {
                var targetX = x + sourceX;
                if (targetX < 0 || targetX >= Width)
                    continue;

                var sourceOffset = (sourceY * source.Width + sourceX) * 2;
                var targetOffset = (targetY * Width + targetX) * 2;
                Pixels[targetOffset] = source.Pixels[sourceOffset];
                Pixels[targetOffset + 1] = source.Pixels[sourceOffset + 1];
            }
        }
    }

    public void FillRectangle(int x, int y, int width, int height, RgbColor color)
    {
        var packed = Pack(color);
        for (var py = Math.Max(y, 0); py < Math.Min(y + height, Height); py++)
        {
            for (var px = Math.Max(x, 0); px < Math.Min(x + width, Width); px++)
                SetPackedPixel(px, py, packed);
        }
    }

    public void SetPixel(int x, int y, RgbColor color)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height)
            return;
        SetPackedPixel(x, y, Pack(color));
    }

    public void DrawRectangle(int x, int y, int width, int height, RgbColor color, int thickness = 1)
    {
        FillRectangle(x, y, width, thickness, color);
        FillRectangle(x, y + height - thickness, width, thickness, color);
        FillRectangle(x, y, thickness, height, color);
        FillRectangle(x + width - thickness, y, thickness, height, color);
    }

    public void DrawText(int x, int y, string text, RgbColor color, int scale = 1)
    {
        foreach (var character in text.ToUpperInvariant())
        {
            if (!Font5x7.Glyphs.TryGetValue(character, out var glyph))
                glyph = Font5x7.Glyphs['?'];
            for (var row = 0; row < glyph.Length; row++)
            {
                for (var column = 0; column < 5; column++)
                {
                    if ((glyph[row] & (1 << (4 - column))) != 0)
                        FillRectangle(x + column * scale, y + row * scale, scale, scale, color);
                }
            }
            x += 6 * scale;
        }
    }

    private void SetPackedPixel(int x, int y, ushort color)
    {
        var offset = (y * Width + x) * 2;
        Pixels[offset] = (byte)color;
        Pixels[offset + 1] = (byte)(color >> 8);
    }

    private static ushort Pack(RgbColor color)
        => (ushort)(((color.Red >> 3) << 11) | ((color.Green >> 2) << 5) | (color.Blue >> 3));
}

internal static class Font5x7
{
    public static readonly IReadOnlyDictionary<char, byte[]> Glyphs = new Dictionary<char, byte[]>
    {
        [' '] = Rows("00000", "00000", "00000", "00000", "00000", "00000", "00000"),
        ['?'] = Rows("01110", "10001", "00001", "00010", "00100", "00000", "00100"),
        ['-'] = Rows("00000", "00000", "00000", "11111", "00000", "00000", "00000"),
        ['0'] = Rows("01110", "10001", "10011", "10101", "11001", "10001", "01110"),
        ['1'] = Rows("00100", "01100", "00100", "00100", "00100", "00100", "01110"),
        ['2'] = Rows("01110", "10001", "00001", "00010", "00100", "01000", "11111"),
        ['3'] = Rows("11110", "00001", "00001", "01110", "00001", "00001", "11110"),
        ['4'] = Rows("00010", "00110", "01010", "10010", "11111", "00010", "00010"),
        ['5'] = Rows("11111", "10000", "10000", "11110", "00001", "00001", "11110"),
        ['6'] = Rows("01110", "10000", "10000", "11110", "10001", "10001", "01110"),
        ['7'] = Rows("11111", "00001", "00010", "00100", "01000", "01000", "01000"),
        ['8'] = Rows("01110", "10001", "10001", "01110", "10001", "10001", "01110"),
        ['9'] = Rows("01110", "10001", "10001", "01111", "00001", "00001", "01110"),
        ['A'] = Rows("01110", "10001", "10001", "11111", "10001", "10001", "10001"),
        ['B'] = Rows("11110", "10001", "10001", "11110", "10001", "10001", "11110"),
        ['C'] = Rows("01111", "10000", "10000", "10000", "10000", "10000", "01111"),
        ['D'] = Rows("11110", "10001", "10001", "10001", "10001", "10001", "11110"),
        ['E'] = Rows("11111", "10000", "10000", "11110", "10000", "10000", "11111"),
        ['F'] = Rows("11111", "10000", "10000", "11110", "10000", "10000", "10000"),
        ['G'] = Rows("01111", "10000", "10000", "10111", "10001", "10001", "01111"),
        ['H'] = Rows("10001", "10001", "10001", "11111", "10001", "10001", "10001"),
        ['I'] = Rows("01110", "00100", "00100", "00100", "00100", "00100", "01110"),
        ['J'] = Rows("00111", "00010", "00010", "00010", "00010", "10010", "01100"),
        ['K'] = Rows("10001", "10010", "10100", "11000", "10100", "10010", "10001"),
        ['L'] = Rows("10000", "10000", "10000", "10000", "10000", "10000", "11111"),
        ['M'] = Rows("10001", "11011", "10101", "10101", "10001", "10001", "10001"),
        ['N'] = Rows("10001", "11001", "10101", "10011", "10001", "10001", "10001"),
        ['O'] = Rows("01110", "10001", "10001", "10001", "10001", "10001", "01110"),
        ['P'] = Rows("11110", "10001", "10001", "11110", "10000", "10000", "10000"),
        ['Q'] = Rows("01110", "10001", "10001", "10001", "10101", "10010", "01101"),
        ['R'] = Rows("11110", "10001", "10001", "11110", "10100", "10010", "10001"),
        ['S'] = Rows("01111", "10000", "10000", "01110", "00001", "00001", "11110"),
        ['T'] = Rows("11111", "00100", "00100", "00100", "00100", "00100", "00100"),
        ['U'] = Rows("10001", "10001", "10001", "10001", "10001", "10001", "01110"),
        ['V'] = Rows("10001", "10001", "10001", "10001", "10001", "01010", "00100"),
        ['W'] = Rows("10001", "10001", "10001", "10101", "10101", "10101", "01010"),
        ['X'] = Rows("10001", "10001", "01010", "00100", "01010", "10001", "10001"),
        ['Y'] = Rows("10001", "10001", "01010", "00100", "00100", "00100", "00100"),
        ['Z'] = Rows("11111", "00001", "00010", "00100", "01000", "10000", "11111"),
    };

    private static byte[] Rows(params string[] rows)
        => rows.Select(row => Convert.ToByte(row, 2)).ToArray();
}
