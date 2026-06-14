using System.IO.Ports;

namespace OpenDeck.Loupedeck;

internal sealed class SerialLoupedeckTransport : ILoupedeckTransport
{
    private const string UpgradeRequest =
        "GET /index.html HTTP/1.1\r\n" +
        "Connection: Upgrade\r\n" +
        "Upgrade: websocket\r\n" +
        "Sec-WebSocket-Key: 123abc\r\n\r\n";

    private readonly string _portName;
    private SerialPort? _port;

    public SerialLoupedeckTransport(string portName, LoupedeckDeviceInfo deviceInfo)
    {
        _portName = portName;
        DeviceInfo = deviceInfo;
    }

    public string Address => _portName;
    public LoupedeckDeviceInfo DeviceInfo { get; }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(PluginSettings.SerialConnectTimeoutSeconds));
        var connectToken = timeout.Token;

        try
        {
            _port = CreatePort(_portName);
            _port.Open();

            var requestBytes = System.Text.Encoding.ASCII.GetBytes(UpgradeRequest);
            await WriteRawAsync(requestBytes, connectToken);
            if (PluginSettings.WaitForSerialTransmitDrain)
                await BaseStream.FlushAsync(connectToken);

            var response = new List<byte>();
            var singleByte = new byte[1];
            while (response.Count < 4096)
            {
                var read = await ReadAsync(singleByte, connectToken);
                if (read == 0)
                    throw new IOException("Serial device closed during handshake.");

                response.Add(singleByte[0]);
                if (response.Count >= 4 &&
                    response[^4] == '\r' &&
                    response[^3] == '\n' &&
                    response[^2] == '\r' &&
                    response[^1] == '\n')
                {
                    break;
                }
            }

            if (!System.Text.Encoding.ASCII.GetString(response.ToArray()).StartsWith("HTTP/1.1", StringComparison.Ordinal))
                throw new IOException("Serial device returned an invalid upgrade response.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or FileNotFoundException)
        {
            await DisposeAsync();
            throw CreateOpenFailure(_portName, ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await DisposeAsync();
            throw new TimeoutException($"{_portName} did not complete the serial WebSocket handshake within {PluginSettings.SerialConnectTimeoutSeconds} seconds.");
        }
    }

    public async Task SendAsync(byte[] packet, CancellationToken cancellationToken)
    {
        byte[] prefix;
        if (packet.Length > byte.MaxValue)
        {
            prefix = new byte[14];
            prefix[0] = 0x82;
            prefix[1] = 0xff;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(6), checked((uint)packet.Length));
        }
        else
        {
            prefix = new byte[6];
            prefix[0] = 0x82;
            prefix[1] = (byte)(0x80 + packet.Length);
        }

        await WriteRawAsync(prefix, cancellationToken);
        await WriteRawAsync(packet, cancellationToken);
        if (PluginSettings.WaitForSerialTransmitDrain)
            await BaseStream.FlushAsync(cancellationToken);
    }

    public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var first = new byte[1];
        while (true)
        {
            var read = await ReadAsync(first, cancellationToken);
            if (read == 0)
                return null;
            if (first[0] == 0x82)
                break;
        }

        var lengthByte = new byte[1];
        await ReadExactlyAsync(lengthByte, cancellationToken);
        var length = (int)lengthByte[0];
        if (length == 0xff)
        {
            var extended = new byte[12];
            await ReadExactlyAsync(extended, cancellationToken);
            length = checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(extended.AsSpan(4, 4)));
        }

        var packet = new byte[length];
        await ReadExactlyAsync(packet, cancellationToken);
        return packet;
    }

    private Stream BaseStream => _port?.BaseStream ?? throw new InvalidOperationException("Serial port is not connected.");

    private static SerialPort CreatePort(string portName)
        => new(portName)
        {
            BaudRate = checked((int)PluginSettings.SerialBaudRate),
            DataBits = 8,
            Parity = Parity.None,
            StopBits = StopBits.One,
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = Timeout.Infinite,
            WriteTimeout = PluginSettings.CommandTimeoutSeconds * 1000,
        };

    private static IOException CreateOpenFailure(string portName, Exception ex)
    {
        var explanation = ex switch
        {
            UnauthorizedAccessException => "The port is already in use. Stop the official Loupedeck software and any older OpenDeck Loupedeck plugin or demo instance.",
            FileNotFoundException => "The port no longer exists. Reconnect the device and try again.",
            IOException ioEx when ioEx.Message.Contains("No such file", StringComparison.OrdinalIgnoreCase) => "The port no longer exists. Reconnect the device and try again.",
            _ => "Make sure the device is connected and that no other application is using the port.",
        };

        return new IOException($"Unable to open {portName}. {explanation}", ex);
    }

    private async Task WriteRawAsync(byte[] data, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            var count = Math.Min(PluginSettings.SerialWriteChunkSize, data.Length - offset);
            await BaseStream.WriteAsync(data.AsMemory(offset, count), cancellationToken);
            offset += count;
        }
    }

    private async Task<int> ReadAsync(byte[] data, CancellationToken cancellationToken)
    {
        try
        {
            return await BaseStream.ReadAsync(data.AsMemory(), cancellationToken);
        }
        catch (TimeoutException)
        {
            return 0;
        }
    }

    private async Task ReadExactlyAsync(byte[] data, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            var read = await BaseStream.ReadAsync(data.AsMemory(offset, data.Length - offset), cancellationToken);
            if (read == 0)
                throw new IOException("Serial device closed unexpectedly.");
            offset += read;
        }
    }

    public ValueTask DisposeAsync()
    {
        var port = _port;
        _port = null;
        if (port is not null)
        {
            try
            {
                if (port.IsOpen)
                {
                    port.Write(new byte[] { 0x88, 0x80, 0, 0, 0, 0 }, 0, 6);
                    port.Close();
                }
            }
            catch (IOException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                port.Dispose();
            }
        }

        return ValueTask.CompletedTask;
    }
}
