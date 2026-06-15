using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.Runtime.Versioning;
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
    // The ioreg serial node sits near its USB parent; scan a bounded window instead of parsing the full tree.
    private const int MacOSIoregLookBehindChars = 4096;
    private const int MacOSIoregSearchWindowChars = MacOSIoregLookBehindChars * 2;
    private const int MacOSIoregTimeoutMs = 3000;
    // Loupedeck Live exposes a USB network adapter where the device is .2 and the host endpoint is .1.
    private const string UsbNetworkAddressPrefix = "100.127.";
    private const string UsbNetworkDeviceAddressSuffix = ".2";
    private const string UsbNetworkHostAddressSuffix = ".1";

    public static IEnumerable<ILoupedeckTransport> FindCandidates()
    {
        var serialCandidates = FindSerialPorts().ToArray();
        if (PluginSettings.TraceDiscovery)
        {
            if (serialCandidates.Length == 0)
            {
                Log.Info("No serial transport candidates were discovered.");
            }
            else
            {
                Log.Info($"Serial transport candidates: {string.Join(", ", serialCandidates.Select(candidate => $"{candidate.PortName} ({candidate.DeviceInfo.VendorId:x4}:{candidate.DeviceInfo.ProductId:x4})"))}");
            }
        }

        foreach (var candidate in serialCandidates)
            yield return new SerialLoupedeckTransport(candidate.PortName, candidate.DeviceInfo);

        var webSocketHosts = FindWebSocketHosts().ToArray();
        if (PluginSettings.TraceDiscovery && webSocketHosts.Length == 0)
            Log.Info("No USB network WebSocket transport candidates were discovered.");

        foreach (var host in webSocketHosts)
            yield return new WebSocketLoupedeckTransport(host);
    }

    private static IEnumerable<SerialCandidate> FindSerialPorts()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var candidate in FindWindowsSerialPorts())
                yield return candidate;
            yield break;
        }

        if (OperatingSystem.IsLinux())
        {
            foreach (var candidate in FindLinuxSerialPorts())
                yield return candidate;
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            foreach (var candidate in FindMacOSSerialPorts())
                yield return candidate;
        }
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<SerialCandidate> FindWindowsSerialPorts()
    {
        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usb is null)
            yield break;

        foreach (var deviceKeyName in usb.GetSubKeyNames())
        {
            var info = TryParseDeviceInfo(deviceKeyName);
            if (info is null || !IsSupportedVendor(info.VendorId))
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

    private static IEnumerable<SerialCandidate> FindLinuxSerialPorts()
    {
        var portPaths = System.IO.Ports.SerialPort.GetPortNames()
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (portPaths.Length == 0)
            yield break;

        var matched = false;
        foreach (var portPath in portPaths)
        {
            var info = TryReadLinuxDeviceInfo(portPath);
            if (info is null || !IsSupportedVendor(info.VendorId))
                continue;

            matched = true;
            var stableId = TryGetLinuxStableId(portPath) ?? portPath;
            yield return new SerialCandidate(portPath, info with { StableId = stableId });
        }

        if (matched)
            yield break;

        foreach (var portPath in portPaths.Where(IsLikelyLinuxLoupedeckPort))
        {
            if (PluginSettings.TraceDiscovery)
                Log.Info($"Falling back to probing likely Linux CDC serial port {portPath}.");

            var stableId = TryGetLinuxStableId(portPath) ?? portPath;
            yield return new SerialCandidate(
                portPath,
                new LoupedeckDeviceInfo(KnownUsbIds.VendorLoupedeck, KnownUsbIds.ProductLoupedeckLive) { StableId = stableId });
        }
    }

    private static bool IsLikelyLinuxLoupedeckPort(string portPath)
    {
        var name = Path.GetFileName(portPath);
        return name.StartsWith("ttyACM", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("ttyUSB", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<SerialCandidate> FindMacOSSerialPorts()
    {
        var ioreg = ReadMacOSIoreg();
        var portPaths = FindMacOSPortNames()
            .Where(IsLikelyMacOSLoupedeckPort)
            .OrderBy(static name => name.StartsWith("/dev/cu.", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var portPath in portPaths)
        {
            var info = TryReadMacOSDeviceInfo(portPath, ioreg) ??
                new LoupedeckDeviceInfo(KnownUsbIds.VendorLoupedeck, KnownUsbIds.ProductLoupedeckLive) { StableId = portPath };

            if (PluginSettings.TraceDiscovery)
                Log.Info($"Probing likely macOS CDC serial port {portPath} ({info.VendorId:x4}:{info.ProductId:x4}).");

            yield return new SerialCandidate(portPath, info);
        }
    }

    private static IEnumerable<string> FindMacOSPortNames()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var portName in System.IO.Ports.SerialPort.GetPortNames())
        {
            if (seen.Add(portName))
                yield return portName;
        }

        foreach (var pattern in new[] { "cu.usbmodem*", "cu.usbserial*", "tty.usbmodem*", "tty.usbserial*" })
        {
            foreach (var path in Directory.EnumerateFiles("/dev", pattern))
            {
                if (seen.Add(path))
                    yield return path;
            }
        }
    }

    private static bool IsLikelyMacOSLoupedeckPort(string portPath)
    {
        var name = Path.GetFileName(portPath);
        return name.StartsWith("cu.usbmodem", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("cu.usbserial", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("tty.usbmodem", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("tty.usbserial", StringComparison.OrdinalIgnoreCase);
    }

    private static LoupedeckDeviceInfo? TryReadMacOSDeviceInfo(string portPath, string? ioreg)
    {
        if (string.IsNullOrWhiteSpace(ioreg))
            return null;

        var suffix = TryGetMacOSUsbModemSuffix(portPath);
        if (suffix is null)
            return null;

        var suffixMarker = $"\"IOTTYSuffix\" = \"{suffix}\"";
        var suffixIndex = ioreg.IndexOf(suffixMarker, StringComparison.Ordinal);
        if (suffixIndex < 0)
            return null;

        // AppleUSBACMData carries the tty suffix and VID/PID, while serial number may live on a nearby parent node.
        var start = Math.Max(0, suffixIndex - MacOSIoregLookBehindChars);
        var length = Math.Min(ioreg.Length - start, MacOSIoregSearchWindowChars);
        var window = ioreg.Substring(start, length);

        var vendor = ReadMacOSIoregInt(window, "idVendor");
        var product = ReadMacOSIoregInt(window, "idProduct");
        if (vendor is null || product is null || !IsSupportedVendor(vendor.Value))
            return null;

        var stableId =
            ReadMacOSIoregString(window, "USB Serial Number") ??
            ReadMacOSIoregString(window, "kUSBSerialNumberString") ??
            portPath;

        return new LoupedeckDeviceInfo(vendor.Value, product.Value, stableId);
    }

    private static string? TryGetMacOSUsbModemSuffix(string portPath)
    {
        var name = Path.GetFileName(portPath);
        foreach (var prefix in new[] { "cu.usbmodem", "tty.usbmodem" })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.Length > prefix.Length)
                return name[prefix.Length..];
        }

        return null;
    }

    private static int? ReadMacOSIoregInt(string ioreg, string property)
    {
        var marker = $"\"{property}\" = ";
        var markerIndex = ioreg.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return null;

        var valueIndex = markerIndex + marker.Length;
        while (valueIndex < ioreg.Length && char.IsWhiteSpace(ioreg[valueIndex]))
            valueIndex++;

        var endIndex = valueIndex;
        while (endIndex < ioreg.Length && char.IsDigit(ioreg[endIndex]))
            endIndex++;

        return endIndex == valueIndex || !int.TryParse(ioreg[valueIndex..endIndex], out var value) ? null : value;
    }

    private static string? ReadMacOSIoregString(string ioreg, string property)
    {
        var marker = $"\"{property}\" = \"";
        var markerIndex = ioreg.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return null;

        var valueIndex = markerIndex + marker.Length;
        var endIndex = ioreg.IndexOf('"', valueIndex);
        return endIndex <= valueIndex ? null : ioreg[valueIndex..endIndex];
    }

    private static string? ReadMacOSIoreg()
    {
        try
        {
            var startInfo = new ProcessStartInfo("ioreg")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            startInfo.ArgumentList.Add("-p"); // Select the registry plane.
            startInfo.ArgumentList.Add("IOService"); // Includes serial BSD clients and USB parent services.
            startInfo.ArgumentList.Add("-l"); // Include service properties such as idVendor, idProduct, and IOTTYSuffix.
            startInfo.ArgumentList.Add("-w"); // Set output line width.
            startInfo.ArgumentList.Add("0"); // Disable truncation so long USB properties stay intact.

            using var process = Process.Start(startInfo);
            if (process is null)
                return null;

            var outputTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(MacOSIoregTimeoutMs))
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                }

                return null;
            }

            return process.ExitCode == 0 ? outputTask.GetAwaiter().GetResult() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveLinuxDevicePath(FileSystemInfo link)
    {
        try
        {
            var target = link.ResolveLinkTarget(true);
            return target?.FullName;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetLinuxStableId(string portPath)
    {
        try
        {
            var byIdDirectory = new DirectoryInfo("/dev/serial/by-id");
            if (byIdDirectory.Exists)
            {
                var canonicalPortPath = Path.GetFullPath(portPath);
                foreach (var link in byIdDirectory.EnumerateFileSystemInfos())
                {
                    var resolved = ResolveLinuxDevicePath(link);
                    if (resolved is not null &&
                        string.Equals(Path.GetFullPath(resolved), canonicalPortPath, StringComparison.Ordinal))
                    {
                        return link.FullName;
                    }
                }
            }

            var ttyName = Path.GetFileName(portPath);
            var sysfsBase = Path.Combine("/sys/class/tty", ttyName, "device");
            var serialFile = Path.Combine(sysfsBase, "serial");
            if (File.Exists(serialFile))
            {
                var serial = File.ReadAllText(serialFile).Trim();
                if (!string.IsNullOrWhiteSpace(serial))
                    return serial;
            }

            var usbNode = Directory.Exists(sysfsBase) ? new DirectoryInfo(sysfsBase).Name : null;
            return string.IsNullOrWhiteSpace(usbNode) ? null : usbNode;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSupportedVendor(int vendorId)
        => vendorId is KnownUsbIds.VendorLoupedeck or KnownUsbIds.VendorRazer;

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

    private static LoupedeckDeviceInfo? TryReadLinuxDeviceInfo(string portPath)
    {
        try
        {
            var ttyName = Path.GetFileName(portPath);
            var sysfsBase = Path.Combine("/sys/class/tty", ttyName, "device");
            var current = new DirectoryInfo(sysfsBase);

            while (current is not null)
            {
                var vendorPath = Path.Combine(current.FullName, "idVendor");
                var productPath = Path.Combine(current.FullName, "idProduct");
                if (File.Exists(vendorPath) && File.Exists(productPath))
                {
                    var vendor = Convert.ToInt32(File.ReadAllText(vendorPath).Trim(), 16);
                    var product = Convert.ToInt32(File.ReadAllText(productPath).Trim(), 16);
                    return new LoupedeckDeviceInfo(vendor, product);
                }

                current = current.Parent;
            }
        }
        catch
        {
        }

        return null;
    }

    private static IEnumerable<string> FindWebSocketHosts()
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (var address in networkInterface.GetIPProperties().UnicastAddresses)
            {
                var ip = address.Address.ToString();
                if (ip.StartsWith(UsbNetworkAddressPrefix, StringComparison.Ordinal) && ip.EndsWith(UsbNetworkDeviceAddressSuffix, StringComparison.Ordinal))
                    yield return ip[..^UsbNetworkDeviceAddressSuffix.Length] + UsbNetworkHostAddressSuffix;
            }
        }
    }
}

internal sealed class WebSocketLoupedeckTransport : ILoupedeckTransport
{
    private const int ReceiveBufferSize = 8192;

    private readonly ClientWebSocket _socket = new();
    private readonly Uri _uri;

    public WebSocketLoupedeckTransport(string host) => _uri = new Uri($"ws://{host}");

    public string Address => _uri.ToString();
    public LoupedeckDeviceInfo DeviceInfo { get; } = new(KnownUsbIds.VendorLoupedeck, KnownUsbIds.ProductLoupedeckLive);

    public Task ConnectAsync(CancellationToken cancellationToken) => _socket.ConnectAsync(_uri, cancellationToken);

    public Task SendAsync(byte[] packet, CancellationToken cancellationToken)
        => _socket.SendAsync(packet, WebSocketMessageType.Binary, true, cancellationToken);

    public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var chunk = new byte[ReceiveBufferSize];
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
