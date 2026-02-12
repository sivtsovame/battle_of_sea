using System;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace client.Services;

public class GameServerClient : IAsyncDisposable
{
    private ClientWebSocket? _webSocket;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private CancellationTokenSource? _receiveCts;
    private string _displayName = string.Empty;
    private string _lastUri = string.Empty;

    public event Action<string, JsonElement>? MessageReceived;
    /// <summary>Вызывается на UI-потоке, когда соединение с сервером потеряно (сервер закрыл или сеть оборвалась).</summary>
    public event Action? ServerDisconnected;
    /// <summary>Вызывается на UI-потоке после успешного подключения.</summary>
    public event Action? ServerConnected;

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;
    public string DisplayName => _displayName;
    public string LastUri => _lastUri;

    public async Task ConnectAsync(string uri, string displayName)
    {
        if (IsConnected)
            return;

        _displayName = displayName;
        _lastUri = uri;

        // ClientWebSocket нельзя переиспользовать после Close/Abort — создаём новый при каждом подключении.
        try { _receiveCts?.Cancel(); } catch { /* ignore */ }
        try { _webSocket?.Dispose(); } catch { /* ignore */ }
        _webSocket = new ClientWebSocket();

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
        Dispatcher.UIThread.Post(() => ServerConnected?.Invoke());
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
        await _webSocket!.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];

        try
        {
            while (!cancellationToken.IsCancellationRequested && _webSocket != null && _webSocket.State == WebSocketState.Open)
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
            // Выход по отмене (Dispose) — не показываем "сервер отключён"
        }
        catch (Exception)
        {
            // Соединение оборвалось (сеть, сервер упал и т.д.)
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
                Dispatcher.UIThread.Post(() => ServerDisconnected?.Invoke());
        }
    }

    /// <summary>Быстрая проверка доступности сервера (без WebSocket), чтобы включить кнопку «Подключиться».</summary>
    public static async Task<bool> IsServerReachableAsync(string host, int port, int timeoutMs = 500)
    {
        try
        {
            using var tcp = new TcpClient();
            var connectTask = tcp.ConnectAsync(host, port);
            var done = await Task.WhenAny(connectTask, Task.Delay(timeoutMs)) == connectTask;
            return done && tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _receiveCts?.Cancel();
            if (_webSocket != null && (_webSocket.State == WebSocketState.Open || _webSocket.State == WebSocketState.CloseReceived))
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None);
            }
        }
        catch
        {
            // ignore
        }
        try { _webSocket?.Dispose(); } catch { /* ignore */ }
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

