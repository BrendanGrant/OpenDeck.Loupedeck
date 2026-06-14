namespace OpenDeck.Loupedeck;

public sealed record LoupedeckDeviceInfo(int VendorId, int ProductId, string? StableId = null);

public sealed record DeviceProfile(
    string Name,
    int Columns,
    int Rows,
    int KeySize,
    int DisplayWidth,
    int DisplayHeight,
    int CenterWidth,
    int CenterHeight,
    int CenterX,
    bool HasSideStrips,
    int PhysicalButtonColorCount,
    int EncoderCount)
{
    public int KeyCount => Columns * Rows;

    public static DeviceProfile From(LoupedeckDeviceInfo info) => (info.VendorId, info.ProductId) switch
    {
        (0x1532, 0x0d09) => RazerStreamControllerX,
        (0x1532, 0x0d06) => RazerStreamController,
        (0x2ec2, 0x0004) => LoupedeckLive,
        _ => LoupedeckLive,
    };

    public static readonly DeviceProfile LoupedeckLive = new(
        "Loupedeck Live",
        Columns: 4,
        Rows: 3,
        KeySize: 90,
        DisplayWidth: 480,
        DisplayHeight: 270,
        CenterWidth: 360,
        CenterHeight: 270,
        CenterX: 60,
        HasSideStrips: true,
        PhysicalButtonColorCount: 8,
        EncoderCount: 6);

    public static readonly DeviceProfile RazerStreamController = LoupedeckLive with
    {
        Name = "Razer Stream Controller"
    };

    public static readonly DeviceProfile RazerStreamControllerX = new(
        "Razer Stream Controller X",
        Columns: 5,
        Rows: 3,
        KeySize: 96,
        DisplayWidth: 480,
        DisplayHeight: 288,
        CenterWidth: 480,
        CenterHeight: 288,
        CenterX: 0,
        HasSideStrips: false,
        PhysicalButtonColorCount: 0,
        EncoderCount: 0);
}
