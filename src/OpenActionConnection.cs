using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

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
    public event EventHandler? Closed;
    public event EventHandler<SetImageRequest>? SetImageRequested;
    public event EventHandler<SetBrightnessRequest>? SetBrightnessRequested;
    public event EventHandler<DeviceDidConnectEvent>? DeviceDidConnect;
    public event EventHandler<OpenDeckEventArgs>? EventReceived;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _socket.ConnectAsync(Uri, cancellationToken);
        await SendAsync(new RegisterPluginMessage(_registerEvent, _pluginUuid), OpenActionJsonContext.Default.RegisterPluginMessage, cancellationToken);
    }

    public Task RegisterDeviceAsync(string deviceId, DeviceProfile profile, CancellationToken cancellationToken)
        => SendAsync(
            new RegisterDeviceEnvelope(
                "registerDevice",
                new RegisterDevicePayload(
                    deviceId,
                    profile.Name,
                    checked((byte)profile.Rows),
                    checked((byte)profile.Columns),
                    checked((byte)profile.EncoderCount),
                    checked((byte)profile.PhysicalButtonColorCount),
                    0)),
            OpenActionJsonContext.Default.RegisterDeviceEnvelope,
            cancellationToken);

    public Task UnregisterDeviceAsync(string deviceId, CancellationToken cancellationToken)
        => SendAsync(
            new StringPayloadEnvelope("unregisterDevice", new StringPayload(deviceId, deviceId)),
            OpenActionJsonContext.Default.StringPayloadEnvelope,
            cancellationToken);

    public Task RerenderImagesAsync(string deviceId, CancellationToken cancellationToken)
        => SendAsync(
            new StringPayloadEnvelope("rerenderImages", new StringPayload(deviceId, deviceId)),
            OpenActionJsonContext.Default.StringPayloadEnvelope,
            cancellationToken);

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
        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Closed?.Invoke(this, EventArgs.Empty);
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                    HandleMessage(Encoding.UTF8.GetString(message.ToArray()));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }

    private Task SendInputAsync(string eventName, string deviceId, int position, int? delta, CancellationToken cancellationToken)
    {
        if (eventName == "encoderChange")
        {
            return SendAsync(
                new EncoderChangeEnvelope(
                    eventName,
                    new EncoderChangePayload(deviceId, checked((byte)position), checked((short)(delta ?? 0)))),
                OpenActionJsonContext.Default.EncoderChangeEnvelope,
                cancellationToken);
        }

        return SendAsync(
            new InputEnvelope(eventName, new InputPayload(deviceId, checked((byte)position))),
            OpenActionJsonContext.Default.InputEnvelope,
            cancellationToken);
    }

    private async Task SendAsync<TMessage>(TMessage message, JsonTypeInfo<TMessage> jsonTypeInfo, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, jsonTypeInfo);
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

        Log.Info($"OpenDeck event received: {eventName}");
        EventReceived?.Invoke(this, new OpenDeckEventArgs(eventName, json));

        if (string.Equals(eventName, "deviceDidConnect", StringComparison.OrdinalIgnoreCase))
        {
            var device = ReadString(payload, "device") ??
                         ReadString(root, "device") ??
                         string.Empty;

            if (PluginSettings.TraceDisplayUpdates)
                Log.Info($"deviceDidConnect event device={device}");

            DeviceDidConnect?.Invoke(this, new DeviceDidConnectEvent(device));
            return;
        }

        if (eventName is "setImage" or "set_image")
        {
            var image = ReadString(payload, "image") ?? ReadString(payload, "data");
            var device = ReadString(payload, "device") ?? ReadString(root, "device") ?? "";
            var controller = ReadController(root, payload);
            var position = ReadPosition(payload);
            Log.Info($"setImage event device={device} controller={controller} position={position} hasImage={!string.IsNullOrWhiteSpace(image)}");

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

}

internal sealed record SetImageRequest(string DeviceId, string Controller, int Position, string Image);
internal sealed record SetBrightnessRequest(string DeviceId, double Brightness);
internal sealed record OpenDeckEventArgs(string EventName, string Json);
internal sealed record DeviceDidConnectEvent(string DeviceId);

internal sealed record RegisterPluginMessage(string Event, string Uuid);
internal sealed record RegisterDeviceEnvelope(string Event, RegisterDevicePayload Payload);
internal sealed record RegisterDevicePayload(string Id, string Name, byte Rows, byte Columns, byte Encoders, byte Touchpoints, byte Type);
internal sealed record StringPayloadEnvelope(string Event, StringPayload Payload);
internal sealed record StringPayload(string Id, string Device);
internal sealed record InputEnvelope(string Event, InputPayload Payload);
internal sealed record InputPayload(string Device, byte Position);
internal sealed record EncoderChangeEnvelope(string Event, EncoderChangePayload Payload);
internal sealed record EncoderChangePayload(string Device, byte Position, short Ticks);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RegisterPluginMessage))]
[JsonSerializable(typeof(RegisterDeviceEnvelope))]
[JsonSerializable(typeof(StringPayloadEnvelope))]
[JsonSerializable(typeof(InputEnvelope))]
[JsonSerializable(typeof(EncoderChangeEnvelope))]
internal partial class OpenActionJsonContext : JsonSerializerContext;
