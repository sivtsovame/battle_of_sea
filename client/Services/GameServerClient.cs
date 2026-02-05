using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace client.Services;

public class GameServerClient : IAsyncDisposable
{
    private readonly ClientWebSocket _webSocket = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private CancellationTokenSource? _receiveCts;
    private string _displayName = string.Empty;

    public event Action<string, JsonElement>? MessageReceived;
    public bool IsConnected => _webSocket.State == WebSocketState.Open;
    public string DisplayName => _displayName;

    public async Task ConnectAsync(string uri, string displayName)
    {
        if (IsConnected)
            return;

        _displayName = displayName;

        await _webSocket.ConnectAsync(new Uri(uri), CancellationToken.None);

        _receiveCts = new CancellationTokenSource();
        _ = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));

        // initial connect message
        var payload = new
        {
            displayName,
            userId = Guid.NewGuid().ToString()
        };

        await SendAsync("connect", payload);
    }

    public async Task SendAsync(string type, object payload)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Not connected to server");

        var envelope = new
        {
            type,
            payload
        };

        var json = JsonSerializer.Serialize(envelope, _jsonOptions);
        var buffer = Encoding.UTF8.GetBytes(json);
        await _webSocket.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];

        try
        {
            while (!cancellationToken.IsCancellationRequested && _webSocket.State == WebSocketState.Open)
            {
                var segment = new ArraySegment<byte>(buffer);
                WebSocketReceiveResult result;
                using var ms = new MemoryStream();

                do
                {
                    result = await _webSocket.ReceiveAsync(segment, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        return;
                    }

                    ms.Write(segment.Array!, segment.Offset, result.Count);
                }
                while (!result.EndOfMessage);

                ms.Seek(0, SeekOrigin.Begin);
                using var doc = await JsonDocument.ParseAsync(ms, cancellationToken: cancellationToken);
                var root = doc.RootElement;

                if (!TryGetPropertyCaseInsensitive(root, "type", out var typeProp))
                    continue;

                var type = typeProp.GetString() ?? string.Empty;

                JsonElement payload;
                if (TryGetPropertyCaseInsensitive(root, "payload", out var payloadProp))
                {
                    payload = payloadProp;
                }
                else
                {
                    payload = root;
                }

                // Invoke on UI thread
                await Dispatcher.UIThread.InvokeAsync(() => MessageReceived?.Invoke(type, payload));
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception)
        {
            // TODO: add logging / error surface
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _receiveCts?.Cancel();
            if (_webSocket.State == WebSocketState.Open || _webSocket.State == WebSocketState.CloseReceived)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None);
            }
        }
        catch
        {
            // ignore
        }
        _webSocket.Dispose();
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string name, out JsonElement value)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}

