namespace OpenDeck.Loupedeck;

internal static class LoupedeckControlMap
{
    private const string LeftStripTarget = "left-strip";
    private const string RightStripTarget = "right-strip";
    private const string DisplayTarget = "display";
    private const string LcdKeyPrefix = "LCD-key-";
    private const string RoundButtonPrefix = "round";
    private const string LcdButtonPrefix = "x-key";

    private static readonly IReadOnlyDictionary<byte, string> ButtonNames = new Dictionary<byte, string>
    {
        [0x00] = "knobCT",
        [0x01] = "knobTL",
        [0x02] = "knobCL",
        [0x03] = "knobBL",
        [0x04] = "knobTR",
        [0x05] = "knobCR",
        [0x06] = "knobBR",
        [0x07] = "round0",
        [0x08] = "round1",
        [0x09] = "round2",
        [0x0a] = "round3",
        [0x0b] = "round4",
        [0x0c] = "round5",
        [0x0d] = "round6",
        [0x0e] = "round7",
        [0x1b] = "x-key0",
        [0x1c] = "x-key1",
        [0x1d] = "x-key2",
        [0x1e] = "x-key3",
        [0x1f] = "x-key4",
        [0x20] = "x-key5",
        [0x21] = "x-key6",
        [0x22] = "x-key7",
        [0x23] = "x-key8",
        [0x24] = "x-key9",
        [0x25] = "x-key10",
        [0x26] = "x-key11",
        [0x27] = "x-key12",
        [0x28] = "x-key13",
        [0x29] = "x-key14",
    };

    private static readonly IReadOnlyDictionary<string, int> EncoderIndexes = new Dictionary<string, int>
    {
        ["knobTL"] = 0,
        ["knobCL"] = 1,
        ["knobBL"] = 2,
        ["knobTR"] = 3,
        ["knobCR"] = 4,
        ["knobBR"] = 5,
    };

    public static string GetButtonName(byte id) => ButtonNames.TryGetValue(id, out var name) ? name : $"0x{id:x2}";

    public static bool TryGetEncoderIndex(string button, out int encoder)
        => EncoderIndexes.TryGetValue(button, out encoder);

    public static bool TryMapPhysicalButton(string button, DeviceProfile profile, out int key)
    {
        key = -1;

        if (button.StartsWith(RoundButtonPrefix, StringComparison.Ordinal) &&
            int.TryParse(button[RoundButtonPrefix.Length..], out var roundIndex))
        {
            key = profile.KeyCount + roundIndex;
            return true;
        }

        if (button.StartsWith(LcdButtonPrefix, StringComparison.Ordinal) &&
            int.TryParse(button[LcdButtonPrefix.Length..], out var xIndex))
        {
            key = xIndex;
            return true;
        }

        return false;
    }

    public static string GetTouchTarget(DeviceProfile profile, int x, int y)
    {
        if (profile.HasSideStrips && x < profile.SideStripWidth)
            return LeftStripTarget;
        if (profile.HasSideStrips && x >= profile.RightStripX)
            return RightStripTarget;
        if (x < profile.CenterX || x >= profile.CenterX + profile.CenterWidth || y < 0 || y >= profile.CenterHeight)
            return DisplayTarget;

        var key = y / profile.KeySize * profile.Columns + (x - profile.CenterX) / profile.KeySize;
        return $"{LcdKeyPrefix}{key}";
    }

    public static bool TryMapTouchTarget(string target, out int key)
    {
        key = -1;
        return target.StartsWith(LcdKeyPrefix, StringComparison.Ordinal) &&
               int.TryParse(target[LcdKeyPrefix.Length..], out key);
    }
}
