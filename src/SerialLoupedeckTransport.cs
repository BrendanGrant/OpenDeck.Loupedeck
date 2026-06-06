using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace OpenDeck.Loupedeck;

[SupportedOSPlatform("windows")]
internal sealed class SerialLoupedeckTransport : ILoupedeckTransport
{
    private const string UpgradeRequest =
        "GET /index.html HTTP/1.1\r\n" +
        "Connection: Upgrade\r\n" +
        "Upgrade: websocket\r\n" +
        "Sec-WebSocket-Key: 123abc\r\n\r\n";

    private readonly string _portName;
    private SafeFileHandle? _handle;

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
            _handle = await Task.Run(() => NativeSerial.Open(_portName, PluginSettings.SerialBaudRate), connectToken);
            await Task.Run(() => WriteRaw(System.Text.Encoding.ASCII.GetBytes(UpgradeRequest), connectToken), connectToken);

            var response = new List<byte>();
            var singleByte = new byte[1];
            while (response.Count < 4096)
            {
                if (await Task.Run(() => NativeSerial.Read(Handle, singleByte, connectToken), connectToken) == 0)
                    throw new IOException("Serial device closed during handshake.");
                response.Add(singleByte[0]);
                if (response.Count >= 4 &&
                    response[^4] == '\r' &&
                    response[^3] == '\n' &&
                    response[^2] == '\r' &&
                    response[^1] == '\n')
                    break;
            }
            if (!System.Text.Encoding.ASCII.GetString(response.ToArray()).StartsWith("HTTP/1.1", StringComparison.Ordinal))
                throw new IOException("Serial device returned an invalid upgrade response.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await DisposeAsync();
            throw new TimeoutException($"{_portName} did not complete the serial WebSocket handshake within {PluginSettings.SerialConnectTimeoutSeconds} seconds.");
        }
    }

    public async Task SendAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
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
        await Task.Run(
            () =>
            {
                WriteRaw(prefix, cancellationToken);
                WriteRaw(packet, cancellationToken);
                if (PluginSettings.WaitForSerialTransmitDrain)
                    NativeSerial.FlushTransmitQueue(Handle);
            },
            cancellationToken);

        if (PluginSettings.TraceSerialWriteTiming)
        {
            var elapsed = DateTimeOffset.UtcNow - started;
            var mode = PluginSettings.WaitForSerialTransmitDrain ? "drained" : "paced";
            Log.Info($"serial tx {mode} {prefix.Length + packet.Length:N0} bytes in {elapsed.TotalSeconds:N1}s");
        }
    }

    public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
        => await Task.Run(() => ReceivePacket(cancellationToken), cancellationToken);

    private byte[]? ReceivePacket(CancellationToken cancellationToken)
    {
        var first = new byte[1];
        while (true)
        {
            if (NativeSerial.Read(Handle, first, cancellationToken) == 0)
                return null;
            if (first[0] == 0x82)
                break;
        }

        var lengthByte = new byte[1];
        ReadExactly(lengthByte, cancellationToken);
        var length = (int)lengthByte[0];
        if (length == 0xff)
        {
            var extended = new byte[12];
            ReadExactly(extended, cancellationToken);
            length = checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(extended.AsSpan(4, 4)));
        }
        var packet = new byte[length];
        ReadExactly(packet, cancellationToken);
        return packet;
    }

    private SafeFileHandle Handle => _handle ?? throw new InvalidOperationException("Serial port is not connected.");

    private static TimeSpan EstimateSerialWriteDelay(int byteCount)
    {
        if (byteCount <= 0 || PluginSettings.SerialBaudRate == 0)
            return TimeSpan.Zero;

        // 8-N-1 serial framing is 10 wire bits per byte. Add a small cushion because
        // the USB CDC bridge can accept data faster than the device consumes it.
        var milliseconds = byteCount * 10_000.0 / PluginSettings.SerialBaudRate * 1.15;
        return TimeSpan.FromMilliseconds(Math.Max(1, milliseconds));
    }

    private void WriteRaw(byte[] data, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(PluginSettings.SerialWriteChunkSize, data.Length - offset);
            NativeSerial.Write(Handle, data.AsSpan(offset, count));
            offset += count;
        }
    }

    private void ReadExactly(byte[] data, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            var buffer = new byte[data.Length - offset];
            var count = NativeSerial.Read(Handle, buffer, cancellationToken);
            if (count == 0)
                throw new IOException("Serial device closed unexpectedly.");
            buffer.AsSpan(0, count).CopyTo(data.AsSpan(offset));
            offset += count;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var handle = _handle;
        if (handle is not null)
        {
            try
            {
                WriteRaw(new byte[] { 0x88, 0x80, 0, 0, 0, 0 }, CancellationToken.None);
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                _handle = null;
                handle.Dispose();
            }
        }
        await Task.CompletedTask;
    }
}

[SupportedOSPlatform("windows")]
internal static class NativeSerial
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint DcbFlags = 0x1011; // binary mode, DTR enabled, RTS enabled, no flow control.
    private const uint ErrorIoPending = 997;
    private const uint Infinite = 0xffffffff;
    private const uint SetDtr = 5;
    private const uint SetRts = 3;

    public static SafeFileHandle Open(string portName, uint baudRate)
    {
        var handle = CreateFile($@"\\.\{portName}", GenericRead | GenericWrite, 0, IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
        if (handle.IsInvalid)
            throw CreateOpenFailure(portName);

        if (!SetupComm(handle, 1 << 16, 1 << 20))
            throw new IOException($"Unable to size {portName} serial queues.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));

        var state = new Dcb { Length = (uint)Marshal.SizeOf<Dcb>() };
        if (!GetCommState(handle, ref state))
            throw new IOException($"Unable to read {portName} settings.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        state.BaudRate = baudRate;
        state.Flags = DcbFlags;
        state.ByteSize = 8;
        state.Parity = 0;
        state.StopBits = 0;
        if (!SetCommState(handle, ref state))
            throw new IOException($"Unable to configure {portName}.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));

        var timeouts = new CommTimeouts
        {
            ReadIntervalTimeout = 0,
            ReadTotalTimeoutMultiplier = 0,
            ReadTotalTimeoutConstant = 0,
            WriteTotalTimeoutMultiplier = 0,
            WriteTotalTimeoutConstant = 5000,
        };
        if (!SetCommTimeouts(handle, ref timeouts))
            throw new IOException($"Unable to configure {portName} timeouts.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        EscapeCommFunction(handle, SetDtr);
        EscapeCommFunction(handle, SetRts);

        return handle;
    }

    private static IOException CreateOpenFailure(string portName)
    {
        var error = Marshal.GetLastWin32Error();
        var hint = error switch
        {
            5 or 32 =>
                "The port is already in use. Stop the official Loupedeck software and any older OpenDeck Loupedeck plugin or demo instance.",
            2 =>
                "The port no longer exists. Reconnect the device and try again.",
            _ =>
                "Make sure the device is connected and that no other application is using the port.",
        };

        return new IOException(
            $"Unable to open {portName}. {hint}",
            Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
    }

    public static void Write(SafeFileHandle handle, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return;

        var buffer = data.ToArray();
        var written = OverlappedIo(handle, buffer, isWrite: true, CancellationToken.None);
        if (written != buffer.Length)
            throw new IOException($"Serial write timed out after {written} of {buffer.Length} bytes.");
    }

    public static int Read(SafeFileHandle handle, byte[] buffer, CancellationToken cancellationToken)
        => checked((int)OverlappedIo(handle, buffer, isWrite: false, cancellationToken));

    private static uint OverlappedIo(SafeFileHandle handle, byte[] buffer, bool isWrite, CancellationToken cancellationToken)
    {
        using var waitHandle = new ManualResetEvent(false);
        var overlapped = new NativeOverlapped { EventHandle = waitHandle.SafeWaitHandle.DangerousGetHandle() };
        var nativeOverlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        var pinnedBuffer = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            Marshal.StructureToPtr(overlapped, nativeOverlapped, false);
            var bufferAddress = pinnedBuffer.AddrOfPinnedObject();
            var ok = isWrite
                ? WriteFile(handle, bufferAddress, checked((uint)buffer.Length), out _, nativeOverlapped)
                : ReadFile(handle, bufferAddress, checked((uint)buffer.Length), out _, nativeOverlapped);

            if (!ok)
            {
                var error = (uint)Marshal.GetLastWin32Error();
                if (error != ErrorIoPending)
                    throw new IOException("Serial I/O failed.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
            }

            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    CancelIoEx(handle, nativeOverlapped);
                    WaitForSingleObject(waitHandle.SafeWaitHandle.DangerousGetHandle(), Infinite);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                var wait = WaitForSingleObject(waitHandle.SafeWaitHandle.DangerousGetHandle(), 100);
                if (wait == 0)
                    break;
                if (wait != 258)
                    throw new IOException($"Serial I/O wait failed with code {wait}.");
            }

            if (!GetOverlappedResult(handle, nativeOverlapped, out var transferred, false))
                throw new IOException("Serial I/O completion failed.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
            return transferred;
        }
        finally
        {
            pinnedBuffer.Free();
            Marshal.FreeHGlobal(nativeOverlapped);
        }
    }

    public static void FlushTransmitQueue(SafeFileHandle handle)
    {
        if (!FlushFileBuffers(handle))
            throw new IOException("Unable to flush serial transmit queue.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetCommState(SafeFileHandle file, ref Dcb dcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetCommState(SafeFileHandle file, ref Dcb dcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetupComm(SafeFileHandle file, uint inQueue, uint outQueue);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetCommTimeouts(SafeFileHandle file, ref CommTimeouts timeouts);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool EscapeCommFunction(SafeFileHandle file, uint function);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(SafeFileHandle file);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle file, IntPtr buffer, uint bytesToWrite, out uint bytesWritten, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle file, IntPtr buffer, uint bytesToRead, out uint bytesRead, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle file, IntPtr overlapped, out uint bytesTransferred, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [StructLayout(LayoutKind.Sequential)]
    private struct Dcb
    {
        public uint Length;
        public uint BaudRate;
        public uint Flags;
        public ushort Reserved;
        public ushort XonLimit;
        public ushort XoffLimit;
        public byte ByteSize;
        public byte Parity;
        public byte StopBits;
        public byte XonChar;
        public byte XoffChar;
        public byte ErrorChar;
        public byte EofChar;
        public byte EvtChar;
        public ushort Reserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommTimeouts
    {
        public uint ReadIntervalTimeout;
        public uint ReadTotalTimeoutMultiplier;
        public uint ReadTotalTimeoutConstant;
        public uint WriteTotalTimeoutMultiplier;
        public uint WriteTotalTimeoutConstant;
    }
}
