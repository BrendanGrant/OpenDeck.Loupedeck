using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace OpenDeck.Loupedeck;

public sealed class LoupedeckDeviceClient : IAsyncDisposable
{
    // Command and event IDs from the Loupedeck device protocol.
    private const byte SetPhysicalButtonColorBase = 0x07;
    private const byte ButtonPress = 0x00;
    private const byte KnobRotate = 0x01;
    private const byte SetColor = 0x02;
    private const byte SetBrightness = 0x09;
    private const byte Framebuffer = 0x10;
    private const byte Draw = 0x0f;
    private const byte SerialRefresh = 0x2e;
    private const byte Touch = 0x4d;
    private const byte TouchCt = 0x52;
    private const byte TouchEnd = 0x6d;
    private const byte TouchEndCt = 0x72;
    private const byte DisplayIdentifierHigh = 0x00;
    private const byte DisplayIdentifierLow = 0x4d;
    private const byte RefreshDisplayIdentifier = 0x4d;
    private const byte RefreshCommitMode = 0x01;
    private const int FramebufferHeaderSize = 10;
    private const int FramebufferXOffset = 2;
    private const int FramebufferYOffset = 4;
    private const int FramebufferWidthOffset = 6;
    private const int FramebufferHeightOffset = 8;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(PluginSettings.CommandTimeoutSeconds);

    private static readonly byte[] MainDisplay = { DisplayIdentifierHigh, DisplayIdentifierLow };
    private readonly ILoupedeckTransport _transport;
    private readonly DeviceProfile _profile;
    private readonly ConcurrentDictionary<byte, TaskCompletionSource<byte[]>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _receiveCancellation;
    private Task? _receiveTask;
    private byte _transactionId;
    private byte[] _lastReceivedPacket = Array.Empty<byte>();

    private LoupedeckDeviceClient(ILoupedeckTransport transport)
    {
        _transport = transport;
        _profile = DeviceProfile.From(transport.DeviceInfo);
    }

    public event EventHandler<ButtonEventArgs>? ButtonChanged;
    public event EventHandler<KnobEventArgs>? KnobRotated;
    public event EventHandler<TouchEventArgs>? TouchChanged;

    public string Address => _transport.Address;
    public string StableId => _transport.DeviceInfo.StableId ?? _transport.Address;
    public DeviceProfile Profile => _profile;

    public static async Task<LoupedeckDeviceClient> ConnectFirstAsync(CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();
        foreach (var transport in LoupedeckTransportDiscovery.FindCandidates())
        {
            try
            {
                Log.Info($"Trying {transport.Address} ...");
                var device = new LoupedeckDeviceClient(transport);
                await device.ConnectAsync(cancellationToken);
                Log.Info($"Detected {device.Profile.Name} ({transport.DeviceInfo.VendorId:x4}:{transport.DeviceInfo.ProductId:x4})");
                return device;
            }
            catch (Exception ex)
            {
                failures.Add($"{transport.Address}: {ex.Message}");
                await transport.DisposeAsync();
            }
        }

        var details = failures.Count == 0 ? "No matching CDC serial port or USB network adapter was found." : string.Join(Environment.NewLine, failures);
        throw new InvalidOperationException($"Unable to connect to a Loupedeck device.{Environment.NewLine}{details}");
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _transport.ConnectAsync(cancellationToken);
        _receiveCancellation = new CancellationTokenSource();
        _receiveTask = ReceiveLoopAsync(_receiveCancellation.Token);
    }

    public Task SetBrightnessAsync(double value, CancellationToken cancellationToken = default)
    {
        var brightness = (byte)Math.Clamp((int)Math.Round(value * 10), 0, 10);
        return SendCommandAsync(SetBrightness, new[] { brightness }, waitForAck: false, cancellationToken);
    }

    public Task SetPhysicalButtonColorAsync(int index, RgbColor color, CancellationToken cancellationToken = default)
    {
        if (index < 0 || index >= _profile.PhysicalButtonColorCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return SendCommandAsync(
            SetColor,
            new[] { (byte)(SetPhysicalButtonColorBase + index), color.Red, color.Green, color.Blue },
            waitForAck: false,
            cancellationToken);
    }

    public Task SetPhysicalButtonColorsAsync(IReadOnlyList<RgbColor> colors, CancellationToken cancellationToken = default)
    {
        if (colors.Count > _profile.PhysicalButtonColorCount)
            throw new ArgumentOutOfRangeException(nameof(colors));
        var data = new byte[colors.Count * 4];
        for (var index = 0; index < colors.Count; index++)
        {
            data[index * 4] = (byte)(SetPhysicalButtonColorBase + index);
            data[index * 4 + 1] = colors[index].Red;
            data[index * 4 + 2] = colors[index].Green;
            data[index * 4 + 3] = colors[index].Blue;
        }
        return SendCommandAsync(SetColor, data, waitForAck: false, cancellationToken);
    }

    public Task DrawCenterKeyAsync(int index, Rgb565Canvas canvas, bool refresh = true, CancellationToken cancellationToken = default)
    {
        if (index < 0 || index >= _profile.KeyCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (canvas.Width != _profile.KeySize || canvas.Height != _profile.KeySize)
            throw new ArgumentException($"Center key canvases must be {_profile.KeySize}x{_profile.KeySize} pixels.", nameof(canvas));
        var x = _profile.CenterX + index % _profile.Columns * _profile.KeySize;
        var y = index / _profile.Columns * _profile.KeySize;
        return DrawRectangleAsync(x, y, canvas, refresh, cancellationToken);
    }

    public Task DrawLeftStripAsync(Rgb565Canvas canvas, bool refresh = true, CancellationToken cancellationToken = default)
        => DrawStripAsync(_profile.LeftStripX, canvas, refresh, cancellationToken);

    public Task DrawRightStripAsync(Rgb565Canvas canvas, bool refresh = true, CancellationToken cancellationToken = default)
        => DrawStripAsync(_profile.RightStripX, canvas, refresh, cancellationToken);

    public Task DrawFullDisplayAsync(Rgb565Canvas canvas, bool refresh = true, CancellationToken cancellationToken = default)
    {
        if (canvas.Width != _profile.DisplayWidth || canvas.Height != _profile.DisplayHeight)
            throw new ArgumentException($"Full display canvases must be {_profile.DisplayWidth}x{_profile.DisplayHeight} pixels.", nameof(canvas));
        return DrawRectangleAsync(0, 0, canvas, refresh, cancellationToken);
    }

    public async Task DrawRectangleAsync(int x, int y, Rgb565Canvas canvas, bool refresh = true, CancellationToken cancellationToken = default)
    {
        var data = new byte[FramebufferHeaderSize + canvas.Pixels.Length];
        MainDisplay.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(FramebufferXOffset), checked((ushort)x));
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(FramebufferYOffset), checked((ushort)y));
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(FramebufferWidthOffset), checked((ushort)canvas.Width));
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(FramebufferHeightOffset), checked((ushort)canvas.Height));
        canvas.Pixels.CopyTo(data, FramebufferHeaderSize);
        await SendCommandAsync(Framebuffer, data, waitForAck: PluginSettings.WaitForFramebufferAck, cancellationToken);
        if (PluginSettings.PostFramebufferDelayMs > 0)
            await Task.Delay(PluginSettings.PostFramebufferDelayMs, cancellationToken);
        if (refresh)
            await RefreshAsync(cancellationToken);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var repeatCount = Math.Max(1, PluginSettings.RefreshRepeatCount);
        for (var index = 0; index < repeatCount; index++)
            await SendCommandAsync(SerialRefresh, new byte[] { RefreshDisplayIdentifier, RefreshCommitMode }, waitForAck: PluginSettings.WaitForRefreshAck, cancellationToken);
    }

    private Task DrawStripAsync(int x, Rgb565Canvas canvas, bool refresh, CancellationToken cancellationToken)
    {
        if (!_profile.HasSideStrips)
            throw new NotSupportedException($"{_profile.Name} does not have side strips.");
        if (canvas.Width != _profile.SideStripWidth || canvas.Height != _profile.SideStripHeight)
            throw new ArgumentException($"Side strip canvases must be {_profile.SideStripWidth}x{_profile.SideStripHeight} pixels.", nameof(canvas));
        return DrawRectangleAsync(x, 0, canvas, refresh, cancellationToken);
    }

    private async Task SendCommandAsync(byte command, byte[] data, bool waitForAck, CancellationToken cancellationToken)
    {
        TaskCompletionSource<byte[]> response;
        byte transactionId;
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            transactionId = NextTransactionId();
            response = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (waitForAck && !_pending.TryAdd(transactionId, response))
                throw new InvalidOperationException($"Transaction ID {transactionId} is already pending.");

            var packet = new byte[3 + data.Length];
            packet[0] = (byte)Math.Min(packet.Length, byte.MaxValue);
            packet[1] = command;
            packet[2] = transactionId;
            data.CopyTo(packet, 3);
            Trace("tx", packet);
            await _transport.SendAsync(packet, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }

        try
        {
            if (!waitForAck)
                return;
            await response.Task.WaitAsync(CommandTimeout, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            var pending = string.Join(", ", _pending.Keys.Select(id => $"0x{id:x2}"));
            var last = _lastReceivedPacket.Length == 0 ? "<none>" : Convert.ToHexString(_lastReceivedPacket);
            throw new TimeoutException(
                $"Timed out waiting for ACK to command 0x{command:x2}, transaction 0x{transactionId:x2}. Pending transactions: [{pending}]. Last received packet: {last}.",
                ex);
        }
        finally
        {
            _pending.TryRemove(transactionId, out _);
        }
    }

    private byte NextTransactionId()
    {
        _transactionId++;
        if (_transactionId == 0)
            _transactionId++;
        return _transactionId;
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var packet = await _transport.ReceiveAsync(cancellationToken);
                if (packet is null)
                    return;
                Trace("rx", packet);
                HandleMessage(packet);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void HandleMessage(byte[] packet)
    {
        if (packet.Length < 3)
            return;

        _lastReceivedPacket = packet;
        var declaredLength = Math.Min(packet[0], packet.Length);
        var command = packet[1];
        var transactionId = packet[2];
        var data = packet.AsSpan(3, Math.Max(0, declaredLength - 3));

        switch (command)
        {
            case ButtonPress when data.Length >= 2:
                ButtonChanged?.Invoke(this, new ButtonEventArgs(LoupedeckControlMap.GetButtonName(data[0]), data[1] == 0));
                break;
            case KnobRotate when data.Length >= 2:
                KnobRotated?.Invoke(this, new KnobEventArgs(LoupedeckControlMap.GetButtonName(data[0]), (sbyte)data[1]));
                break;
            case Touch or TouchCt or TouchEnd or TouchEndCt when data.Length >= 6:
                var x = BinaryPrimitives.ReadUInt16BigEndian(data[1..]);
                var y = BinaryPrimitives.ReadUInt16BigEndian(data[3..]);
                TouchChanged?.Invoke(this, new TouchEventArgs(TouchKind(command), x, y, data[5], LoupedeckControlMap.GetTouchTarget(_profile, x, y)));
                break;
        }

        if (_pending.TryGetValue(transactionId, out var response))
            response.TrySetResult(packet);
    }

    private static void Trace(string direction, byte[] packet)
    {
        if (!PluginSettings.TracePackets)
            return;
        var shown = packet.AsSpan(0, Math.Min(packet.Length, PluginSettings.TraceMaxBytes));
        var suffix = packet.Length > shown.Length ? $" ... ({packet.Length} bytes total)" : "";
        Log.Info($"{direction} {Convert.ToHexString(shown)}{suffix}");
    }

    private static string TouchKind(byte command) => command switch
    {
        Touch => "touch",
        TouchCt => "touch-ct",
        TouchEnd => "touch-end",
        TouchEndCt => "touch-end-ct",
        _ => "unknown",
    };

    public async ValueTask DisposeAsync()
    {
        _receiveCancellation?.Cancel();
        await _transport.DisposeAsync();
        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (IOException)
            {
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (TimeoutException)
            {
                Log.Info("Receive loop did not stop within 1 second during shutdown; continuing close.");
            }
        }

        _receiveCancellation?.Dispose();
        _sendLock.Dispose();
    }
}

public sealed record ButtonEventArgs(string Button, bool IsDown);
public sealed record KnobEventArgs(string Knob, int Delta);
public sealed record TouchEventArgs(string Kind, int X, int Y, int TouchId, string Target);
