using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace OpenDeck.Loupedeck;

internal sealed class OpenActionConnection : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly string _pluginUuid;
    private readonly string _registerEvent;

    public OpenActionConnection(Uri uri, string pluginUuid, string registerEvent)
    {
        Uri = uri;
        _pluginUuid = pluginUuid;
        _registerEvent = registerEvent;
    }

    public Uri Uri { get; }
    public event EventHandler<SetImageRequest>? SetImageRequested;
    public event EventHandler<SetBrightnessRequest>? SetBrightnessRequested;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _socket.ConnectAsync(Uri, cancellationToken);
        await SendAsync(new Dictionary<string, object?>
        {
            ["event"] = _registerEvent,
            ["uuid"] = _pluginUuid,
        }, cancellationToken);
    }

    public Task RegisterDeviceAsync(string deviceId, DeviceProfile profile, CancellationToken cancellationToken)
        => SendEventAsync("registerDevice", new
        {
            id = deviceId,
            name = profile.Name,
            rows = checked((byte)profile.Rows),
            columns = checked((byte)profile.Columns),
            encoders = checked((byte)profile.EncoderCount),
            touchpoints = checked((byte)profile.PhysicalButtonColorCount),
            type = (byte)0,
        }, cancellationToken);

    public Task UnregisterDeviceAsync(string deviceId, CancellationToken cancellationToken)
        => SendEventAsync("unregisterDevice", new { id = deviceId, device = deviceId }, cancellationToken);

    public Task KeyDownAsync(string deviceId, int position, CancellationToken cancellationToken)
        => SendInputAsync("keyDown", deviceId, position, null, cancellationToken);

    public Task KeyUpAsync(string deviceId, int position, CancellationToken cancellationToken)
        => SendInputAsync("keyUp", deviceId, position, null, cancellationToken);

    public Task EncoderDownAsync(string deviceId, int encoder, CancellationToken cancellationToken)
        => SendInputAsync("encoderDown", deviceId, encoder, null, cancellationToken);

    public Task EncoderUpAsync(string deviceId, int encoder, CancellationToken cancellationToken)
        => SendInputAsync("encoderUp", deviceId, encoder, null, cancellationToken);

    public Task EncoderChangeAsync(string deviceId, int encoder, int delta, CancellationToken cancellationToken)
        => SendInputAsync("encoderChange", deviceId, encoder, delta, cancellationToken);

    public async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Text)
                HandleMessage(Encoding.UTF8.GetString(message.ToArray()));
        }
    }

    private Task SendInputAsync(string eventName, string deviceId, int position, int? delta, CancellationToken cancellationToken)
    {
        object payload = eventName == "encoderChange"
            ? new
            {
                device = deviceId,
                position = checked((byte)position),
                ticks = checked((short)(delta ?? 0)),
            }
            : new
            {
                device = deviceId,
                position = checked((byte)position),
            };
        return SendEventAsync(eventName, payload, cancellationToken);
    }

    private Task SendEventAsync(string eventName, object payload, CancellationToken cancellationToken)
        => SendAsync(new Dictionary<string, object?>
        {
            ["event"] = eventName,
            ["payload"] = payload,
        }, cancellationToken);

    private async Task SendAsync(object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, JsonOptions);
        if (PluginSettings.TracePackets)
            Log.Info($"oa tx {json}");
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void HandleMessage(string json)
    {
        if (PluginSettings.TracePackets)
            Log.Info($"oa rx {json}");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var eventName = ReadString(root, "event") ?? ReadString(root, "type") ?? "";
        var payload = root.TryGetProperty("payload", out var payloadValue) ? payloadValue : root;

        if (eventName is "setImage" or "set_image")
        {
            var image = ReadString(payload, "image") ?? ReadString(payload, "data");
            var device = ReadString(payload, "device") ?? ReadString(root, "device") ?? "";
            var controller = ReadController(root, payload);
            var position = ReadPosition(payload);
            if (!string.IsNullOrWhiteSpace(image) && position >= 0)
                SetImageRequested?.Invoke(this, new SetImageRequest(device, controller, position, image));
            return;
        }

        if (eventName is "setBrightness" or "set_brightness")
        {
            var device = ReadString(payload, "device") ?? ReadString(root, "device") ?? "";
            var brightness = ReadDouble(payload, "brightness") ?? ReadDouble(payload, "value");
            if (brightness is not null)
                SetBrightnessRequested?.Invoke(this, new SetBrightnessRequest(device, NormalizeBrightness(brightness.Value)));
        }
    }

    private static int ReadPosition(JsonElement payload)
    {
        if (ReadInt(payload, "position") is { } position)
            return position;
        if (ReadInt(payload, "key") is { } key)
            return key;
        if (payload.TryGetProperty("coordinates", out var coordinates))
        {
            var row = ReadInt(coordinates, "row");
            var column = ReadInt(coordinates, "column") ?? ReadInt(coordinates, "col");
            if (row is not null && column is not null)
                return row.Value * 8 + column.Value;
        }
        return -1;
    }

    private static string ReadController(JsonElement root, JsonElement payload)
    {
        var controller = ReadString(payload, "controller") ?? ReadString(root, "controller");
        if (!string.IsNullOrWhiteSpace(controller))
            return controller;

        var context = ReadString(root, "context") ?? ReadString(payload, "context");
        if (!string.IsNullOrWhiteSpace(context))
        {
            var parts = context.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var known = parts.FirstOrDefault(part =>
                string.Equals(part, "Keypad", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, "Encoder", StringComparison.OrdinalIgnoreCase));
            if (known is not null)
                return known;
        }

        return "Keypad";
    }

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? ReadInt(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : null;

    private static double? ReadDouble(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.TryGetDouble(out var result) ? result : null;

    private static double NormalizeBrightness(double brightness)
        => Math.Clamp(brightness > 1 ? brightness / 100.0 : brightness, 0, 1);

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
        _sendLock.Dispose();
        _socket.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

internal sealed record SetImageRequest(string DeviceId, string Controller, int Position, string Image);
internal sealed record SetBrightnessRequest(string DeviceId, double Brightness);
