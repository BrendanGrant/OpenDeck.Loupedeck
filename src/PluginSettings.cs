namespace OpenDeck.Loupedeck;

internal static class PluginSettings
{
    public const string DeviceNamespace = "LD";
    public const string PluginUuid = "io.github.brendangrant.opendeck.loupedeck";
    public static bool TracePackets = false;
    public static int TraceMaxBytes = 96;
    public static uint SerialBaudRate = 921600;
    public static int SerialConnectTimeoutSeconds = 3;
    public static int SerialWriteChunkSize = 4096;
    public static bool WaitForSerialTransmitDrain = false;
    public static bool TraceSerialWriteTiming = false;
    public static int CommandTimeoutSeconds = 12;
    public static int DeviceConnectAttempts = 3;
    public static int DeviceConnectRetryDelayMs = 1000;
    public static bool WaitForFramebufferAck = true;
    public static bool WaitForRefreshAck = true;
    public static int RefreshRepeatCount = 2;
    public static int PostFramebufferDelayMs = 0;
    public static double InitialBrightness = 0.8;
}
