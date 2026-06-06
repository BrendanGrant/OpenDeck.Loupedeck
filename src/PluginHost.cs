namespace OpenDeck.Loupedeck;

internal sealed class PluginHost : IAsyncDisposable
{
    private static readonly IReadOnlyDictionary<string, int> EncoderIndexes = new Dictionary<string, int>
    {
        ["knobTL"] = 0,
        ["knobCL"] = 1,
        ["knobBL"] = 2,
        ["knobTR"] = 3,
        ["knobCR"] = 4,
        ["knobBR"] = 5,
    };

    private readonly OpenActionConnection? _openAction;
    private LoupedeckDeviceClient? _device;
    private string _deviceId = "";

    public PluginHost(OpenActionConnection? openAction) => _openAction = openAction;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Log.Info("OpenDeck Loupedeck plugin");
        Log.Info($"Plugin log file: {Log.FilePath ?? "<disabled>"}");
        Log.Info($"Configured serial baud rate: {PluginSettings.SerialBaudRate:N0}");

        if (_openAction is not null)
        {
            Log.Info($"Connecting to OpenDeck at {_openAction.Uri} ...");
            await _openAction.ConnectAsync(cancellationToken);
            _openAction.SetImageRequested += OnSetImageRequested;
            _openAction.SetBrightnessRequested += OnSetBrightnessRequested;
            _ = Task.Run(() => _openAction.ReceiveLoopAsync(cancellationToken), cancellationToken);
        }
        else
        {
            Log.Info("No OpenDeck port was supplied; running device input logging only.");
        }

        _device = await ConnectDeviceWithRetriesAsync(cancellationToken);
        if (_device is null)
            return;
        _deviceId = BuildDeviceId(_device);
        _device.ButtonChanged += OnButtonChanged;
        _device.KnobRotated += OnKnobRotated;
        _device.TouchChanged += OnTouchChanged;

        await _device.SetBrightnessAsync(PluginSettings.InitialBrightness, cancellationToken);
        await ClearDeviceAsync(cancellationToken);

        if (_openAction is not null)
        {
            await _openAction.RegisterDeviceAsync(_deviceId, _device.Profile, cancellationToken);
            Log.Info($"Registered {_device.Profile.Name} as OpenDeck device {_deviceId}.");
        }

        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private async void OnSetImageRequested(object? sender, SetImageRequest request)
    {
        try
        {
            if (_device is null || !IsRequestForThisDevice(request.DeviceId))
                return;
            if (!string.Equals(request.Controller, "Keypad", StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"Ignoring {request.Controller} image update at position {request.Position}; encoder strip rendering is not implemented yet.");
                return;
            }
            if (request.Position < 0)
                return;

            if (request.Position >= _device.Profile.KeyCount)
            {
                var physicalIndex = request.Position - _device.Profile.KeyCount;
                if (physicalIndex < 0 || physicalIndex >= _device.Profile.PhysicalButtonColorCount)
                    return;
                var color = ImagePayloadDecoder.DecodeAverageColor(request.Image);
                await _device.SetPhysicalButtonColorAsync(physicalIndex, color);
                return;
            }

            var canvas = ImagePayloadDecoder.DecodeToCanvas(request.Image, _device.Profile.KeySize, _device.Profile.KeySize);
            await _device.DrawCenterKeyAsync(request.Position, canvas);
        }
        catch (Exception ex)
        {
            Log.Error($"setImage failed for position {request.Position}: {ex.Message}");
        }
    }

    private async void OnSetBrightnessRequested(object? sender, SetBrightnessRequest request)
    {
        try
        {
            if (_device is null || !IsRequestForThisDevice(request.DeviceId))
                return;
            await _device.SetBrightnessAsync(request.Brightness);
            Log.Info($"brightness {request.Brightness:0.00}");
        }
        catch (Exception ex)
        {
            Log.Error($"setBrightness failed: {ex.Message}");
        }
    }

    private async void OnButtonChanged(object? sender, ButtonEventArgs e)
    {
        try
        {
            Log.Info($"button {e.Button} {(e.IsDown ? "down" : "up")}");
            if (_openAction is null)
                return;

            if (EncoderIndexes.TryGetValue(e.Button, out var encoder))
            {
                if (_device is not null && encoder >= _device.Profile.EncoderCount)
                    return;
                if (e.IsDown)
                    await _openAction.EncoderDownAsync(_deviceId, encoder, CancellationToken.None);
                else
                    await _openAction.EncoderUpAsync(_deviceId, encoder, CancellationToken.None);
                return;
            }

            if (TryMapPhysicalButton(e.Button, out var key))
            {
                if (e.IsDown)
                    await _openAction.KeyDownAsync(_deviceId, key, CancellationToken.None);
                else
                    await _openAction.KeyUpAsync(_deviceId, key, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"button forward failed: {ex.Message}");
        }
    }

    private async void OnKnobRotated(object? sender, KnobEventArgs e)
    {
        try
        {
            Log.Info($"knob {e.Knob} rotate {e.Delta:+0;-0;0}");
            if (_openAction is not null && EncoderIndexes.TryGetValue(e.Knob, out var encoder))
            {
                if (_device is not null && encoder >= _device.Profile.EncoderCount)
                    return;
                await _openAction.EncoderChangeAsync(_deviceId, encoder, e.Delta, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"knob forward failed: {ex.Message}");
        }
    }

    private async void OnTouchChanged(object? sender, TouchEventArgs e)
    {
        try
        {
            Log.Info($"{e.Kind} x={e.X} y={e.Y} target={e.Target}");
            if (_openAction is null || !TryMapTouchTarget(e.Target, out var key))
                return;
            if (e.Kind.StartsWith("touch-end", StringComparison.Ordinal))
                await _openAction.KeyUpAsync(_deviceId, key, CancellationToken.None);
            else if (e.Kind.StartsWith("touch", StringComparison.Ordinal))
                await _openAction.KeyDownAsync(_deviceId, key, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error($"touch forward failed: {ex.Message}");
        }
    }

    private async Task ClearDeviceAsync(CancellationToken cancellationToken)
    {
        if (_device is null)
            return;
        var black = new Rgb565Canvas(_device.Profile.DisplayWidth, _device.Profile.DisplayHeight, RgbColor.Black);
        await _device.DrawFullDisplayAsync(black, cancellationToken: cancellationToken);
        if (_device.Profile.PhysicalButtonColorCount > 0)
        {
            var colors = Enumerable.Repeat(RgbColor.Black, _device.Profile.PhysicalButtonColorCount).ToArray();
            await _device.SetPhysicalButtonColorsAsync(colors, cancellationToken);
        }
    }

    private static async Task<LoupedeckDeviceClient?> ConnectDeviceWithRetriesAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= Math.Max(1, PluginSettings.DeviceConnectAttempts); attempt++)
        {
            try
            {
                return await LoupedeckDeviceClient.ConnectFirstAsync(cancellationToken);
            }
            catch (Exception ex) when (attempt < PluginSettings.DeviceConnectAttempts)
            {
                Log.Error($"Device connection attempt {attempt} failed: {ex.Message}");
                await Task.Delay(PluginSettings.DeviceConnectRetryDelayMs, cancellationToken);
            }
            catch (Exception ex)
            {
                Log.Error($"Unable to connect to a Loupedeck-compatible device after {attempt} attempt(s). The plugin will exit. {ex.Message}");
                return null;
            }
        }
        return null;
    }

    private bool IsRequestForThisDevice(string requestDeviceId)
        => string.IsNullOrWhiteSpace(requestDeviceId) ||
           string.Equals(requestDeviceId, _deviceId, StringComparison.OrdinalIgnoreCase);

    private bool TryMapPhysicalButton(string button, out int key)
    {
        key = -1;
        if (_device is null)
            return false;
        if (button.StartsWith("round", StringComparison.Ordinal) &&
            int.TryParse(button[5..], out var roundIndex))
        {
            key = _device.Profile.KeyCount + roundIndex;
            return true;
        }
        if (button.StartsWith("x-key", StringComparison.Ordinal) &&
            int.TryParse(button[5..], out var xIndex))
        {
            key = xIndex;
            return true;
        }
        return false;
    }

    private static bool TryMapTouchTarget(string target, out int key)
    {
        key = -1;
        const string prefix = "LCD-key-";
        return target.StartsWith(prefix, StringComparison.Ordinal) &&
               int.TryParse(target[prefix.Length..], out key);
    }

    private static string BuildDeviceId(LoupedeckDeviceClient device)
        => $"{PluginSettings.DeviceNamespace}-{device.Profile.Name.Replace(' ', '-')}-{device.Address}".Replace("\\", "").Replace(".", "-").Replace(":", "-");

    public async ValueTask DisposeAsync()
    {
        if (_openAction is not null && !string.IsNullOrWhiteSpace(_deviceId))
            await _openAction.UnregisterDeviceAsync(_deviceId, CancellationToken.None);
        if (_device is not null)
            await _device.DisposeAsync();
        if (_openAction is not null)
            await _openAction.DisposeAsync();
    }
}
