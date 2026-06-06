using System.Net.NetworkInformation;
using System.Net.WebSockets;
using Microsoft.Win32;

namespace OpenDeck.Loupedeck;

internal interface ILoupedeckTransport : IAsyncDisposable
{
    string Address { get; }
    LoupedeckDeviceInfo DeviceInfo { get; }
    Task ConnectAsync(CancellationToken cancellationToken);
    Task SendAsync(byte[] packet, CancellationToken cancellationToken);
    Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken);
}

internal static class LoupedeckTransportDiscovery
{
    public static IEnumerable<ILoupedeckTransport> FindCandidates()
    {
        foreach (var candidate in FindSerialPorts())
            yield return new SerialLoupedeckTransport(candidate.PortName, candidate.DeviceInfo);

        foreach (var host in FindWebSocketHosts())
            yield return new WebSocketLoupedeckTransport(host);
    }

    private static IEnumerable<SerialCandidate> FindSerialPorts()
    {
        if (!OperatingSystem.IsWindows())
            yield break;

        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usb is null)
            yield break;

        foreach (var deviceKeyName in usb.GetSubKeyNames())
        {
            var info = TryParseDeviceInfo(deviceKeyName);
            if (info is null || (info.VendorId != 0x2ec2 && info.VendorId != 0x1532))
                continue;

            using var device = usb.OpenSubKey(deviceKeyName);
            if (device is null)
                continue;

            foreach (var instanceKeyName in device.GetSubKeyNames())
            {
                using var parameters = device.OpenSubKey($@"{instanceKeyName}\Device Parameters");
                if (parameters?.GetValue("PortName") is string portName && portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                    yield return new SerialCandidate(portName, info);
            }
        }
    }

    private static LoupedeckDeviceInfo? TryParseDeviceInfo(string keyName)
    {
        var parts = keyName.Split('&');
        var vidPart = parts.FirstOrDefault(part => part.StartsWith("VID_", StringComparison.OrdinalIgnoreCase));
        var pidPart = parts.FirstOrDefault(part => part.StartsWith("PID_", StringComparison.OrdinalIgnoreCase));
        if (vidPart is null || pidPart is null)
            return null;
        return new LoupedeckDeviceInfo(
            Convert.ToInt32(vidPart[4..], 16),
            Convert.ToInt32(pidPart[4..], 16));
    }

    private static IEnumerable<string> FindWebSocketHosts()
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (var address in networkInterface.GetIPProperties().UnicastAddresses)
            {
                var ip = address.Address.ToString();
                if (ip.StartsWith("100.127.", StringComparison.Ordinal) && ip.EndsWith(".2", StringComparison.Ordinal))
                    yield return ip[..^1] + "1";
            }
        }
    }
}

internal sealed class WebSocketLoupedeckTransport : ILoupedeckTransport
{
    private readonly ClientWebSocket _socket = new();
    private readonly Uri _uri;

    public WebSocketLoupedeckTransport(string host) => _uri = new Uri($"ws://{host}");
    public string Address => _uri.ToString();
    public LoupedeckDeviceInfo DeviceInfo { get; } = new(0x2ec2, 0x0004);

    public Task ConnectAsync(CancellationToken cancellationToken) => _socket.ConnectAsync(_uri, cancellationToken);

    public Task SendAsync(byte[] packet, CancellationToken cancellationToken)
        => _socket.SendAsync(packet, WebSocketMessageType.Binary, true, cancellationToken);

    public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var chunk = new byte[8192];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(chunk, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            message.Write(chunk, 0, result.Count);
        } while (!result.EndOfMessage);
        return message.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
            }
            catch (WebSocketException)
            {
            }
        }
        _socket.Dispose();
    }
}

internal sealed record SerialCandidate(string PortName, LoupedeckDeviceInfo DeviceInfo);
