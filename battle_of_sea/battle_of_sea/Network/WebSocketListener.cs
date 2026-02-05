using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using battle_of_sea.Game;
using battle_of_sea.Protocol;

namespace battle_of_sea.Network;

public class WebSocketListener
{
    private readonly int _port;
    private HttpListener? _httpListener;
    private List<WebSocketConnection> _connections = new();

    public WebSocketListener(int port)
    {
        _port = port;
    }

    public async Task StartAsync()
    {
        _httpListener = new HttpListener();
        _httpListener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _httpListener.Prefixes.Add($"http://localhost:{_port}/");
        _httpListener.Start();

        Console.WriteLine($"WebSocket server started on port {_port}");

        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await _httpListener.GetContextAsync();
                Console.WriteLine($"Request received: {context.Request.HttpMethod} {context.Request.RawUrl}");
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (context.Request.IsWebSocketRequest)
            {
                Console.WriteLine("✅ WebSocket request detected");
                ProcessWebSocketRequest(context);
            }
            else
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
            }
        }
    }

    private async void ProcessWebSocketRequest(HttpListenerContext context)
    {
        HttpListenerWebSocketContext webSocketContext;
        try
        {
            webSocketContext = await context.AcceptWebSocketAsync(null);
            using (var webSocket = webSocketContext.WebSocket)
            {
                var connection = new WebSocketConnection(webSocket);
                _connections.Add(connection);
                Console.WriteLine($"Client connected. Total connections: {_connections.Count}");
                
                await connection.HandleAsync();
                
                _connections.Remove(connection);
                Console.WriteLine($"Client disconnected. Total connections: {_connections.Count}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebSocket error: {ex.Message}");
            context.Response.StatusCode = 500;
            context.Response.Close();
        }
    }

    public void Stop()
    {
        _httpListener?.Stop();
        _httpListener?.Close();
    }
}

public class WebSocketConnection
{
    private readonly WebSocket _webSocket;
    private Player? _player;
    private readonly SemaphoreSlim _sendSemaphore = new(1, 1);

    public WebSocketConnection(WebSocket webSocket)
    {
        _webSocket = webSocket;
    }

    public Player? GetPlayer() => _player;
    public void SetPlayer(Player player) => _player = player;

    public class GameServer
    {
        private static GameServer? _instance;
        public static GameServer Instance => _instance ??= new GameServer();

        public GameManager GameManager { get; private set; } = new GameManager();
        private List<WebSocketConnection> _allConnections = new();

        public void AddConnection(WebSocketConnection connection)
        {
            _allConnections.Add(connection);
            Console.WriteLine($"[GameServer] ✅ Connection added. Total: {_allConnections.Count}");
        }

        public void RemoveConnection(WebSocketConnection connection)
        {
            _allConnections.Remove(connection);
            Console.WriteLine($"[GameServer] Connection removed. Total: {_allConnections.Count}");
        }

        public List<WebSocketConnection> GetAllConnections()
        {
            var connections = _allConnections.ToList();
            Console.WriteLine($"[GameServer] GetAllConnections: returning {connections.Count} connections");
            return connections;
        }

        public WebSocketConnection? GetConnectionByPlayerId(string playerId)
        {
            return _allConnections.FirstOrDefault(c => c.GetPlayer()?.Id == playerId);
        }
    }

    public async Task HandleAsync()
    {
        try
        {
            // Регистрируем соединение на сервере
            GameServer.Instance.AddConnection(this);

            var buffer = new byte[1024 * 4];
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            while (_webSocket.State == WebSocketState.Open)
            {
                var result = await _webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    CancellationToken.None
                );

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Closing",
                        CancellationToken.None
                    );
                }
                else if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    Console.WriteLine($"Received: {json}");

                    JsonElement root;
                    try
                    {
                        root = JsonSerializer.Deserialize<JsonElement>(json, options);
                    }
                    catch
                    {
                        await SendAsync(new ServerMessage { Type = "error", Payload = new { message = "Invalid JSON" } });
                        continue;
                    }

                    if (!root.TryGetProperty("type", out var typeElem) || string.IsNullOrEmpty(typeElem.GetString()))
                    {
                        await SendAsync(new ServerMessage { Type = "error", Payload = new { message = "Missing Type" } });
                        continue;
                    }

                    var messageType = typeElem.GetString()!;
                    
                    // Пытаемся получить payload, если его нет - используем root как payload
                    JsonElement payload;
                    if (!root.TryGetProperty("payload", out payload))
                    {
                        // Если нет поля payload, то весь root - это и есть payload
                        payload = root;
                    }

                    await HandleMessageAsync(messageType, payload);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Connection error: {ex.Message}");
        }
        finally
        {
            // Удаляем соединение со списка
            GameServer.Instance.RemoveConnection(this);

            if (_player != null)
            {
                Console.WriteLine($"Player disconnected: {_player.Name}");
                // Удаляем игрока из GameManager
                GameServer.Instance.GameManager.RemovePlayer(_player.Id);
            }
            _webSocket?.Dispose();
        }
    }

    private async Task HandleMessageAsync(string messageType, JsonElement payload)
    {
        switch (messageType.ToLower())
        {
            case "connect":
                await HandleConnect(payload);
                break;

            case "ping":
                await SendAsync(new ServerMessage { Type = "pong", Payload = new { } });
                break;

            case "shoot":
                await HandleShoot(payload);
                break;

            case "shipplacement":
                await HandleShipPlacement(payload);
                break;

            case "playerready":
                await HandlePlayerReady(payload);
                break;

            case "playagain":
                await HandlePlayAgain();
                break;

            case "reconnect":
                await HandleReconnect(payload);
                break;

            case "getrooms":
                await HandleGetRooms();
                break;

            case "createroom":
                await HandleCreateRoom(payload);
                break;

            case "joinroom":
                await HandleJoinRoom(payload);
                break;

            case "leaveroom":
                await HandleLeaveRoom(payload);
                break;

            case "chat":
            case "chatmessage":
                await HandleChat(payload);
                break;

            default:
                await SendAsync(new ServerMessage { Type = "error", Payload = new { message = "Unknown command" } });
                break;
        }
    }

    private async Task HandleConnect(JsonElement payload)
    {
        try
        {
            Console.WriteLine($"[HandleConnect] Payload ValueKind: {payload.ValueKind}");
            
            if (payload.ValueKind == JsonValueKind.Undefined || payload.ValueKind == JsonValueKind.Null)
            {
                throw new InvalidOperationException("Payload is null or undefined");
            }
            
            Console.WriteLine($"[HandleConnect] Starting...");
            var playerName = "Unknown";
            var userId = Guid.NewGuid().ToString();
            
            if (payload.TryGetProperty("displayName", out var displayNameElem))
            {
                playerName = displayNameElem.GetString() ?? "Unknown";
            }
            
            if (payload.TryGetProperty("userId", out var userIdElem))
            {
                userId = userIdElem.GetString() ?? Guid.NewGuid().ToString();
            }

            Console.WriteLine($"[HandleConnect] Creating player: {playerName} ({userId})");
            
            var player = new Player
            {
                Id = userId,
                Name = playerName,
                Connection = this
            };
            
            _player = player;
            SetPlayer(player);

            Console.WriteLine($"[HandleConnect] Adding player to GameManager...");
            GameServer.Instance.GameManager.AddPlayer(_player);
            Console.WriteLine($"[HandleConnect] Total connected players: {GameServer.Instance.GameManager.Players.Count}");

            Console.WriteLine($"[HandleConnect] Sending connected message...");
            await SendAsync(new ServerMessage
            {
                Type = "connected",
                Payload = new { playerId = _player.Id, displayName = _player.Name }
            });

            // Отправляем список комнат при подключении
            Console.WriteLine($"[HandleConnect] Sending rooms list...");
            await BroadcastRoomsList();

            Console.WriteLine($"✅ Player connected: {playerName} ({_player.Id})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HandleConnect] ❌ EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"[HandleConnect] Stack trace: {ex.StackTrace}");
            try
            {
                await SendAsync(new ServerMessage { Type = "error", Payload = new { message = ex.Message } });
            }
            catch (Exception sendEx)
            {
                Console.WriteLine($"[HandleConnect] Failed to send error message: {sendEx.Message}");
            }
        }
    }

    private async Task HandleShoot(JsonElement payload)
    {
        try
        {
            if (_player == null)
            {
                await SendAsync(new ServerMessage
                {
                    Type = "error",
                    Payload = new { message = "Not connected" }
                });
                return;
            }

            var game = GameServer.Instance.GameManager.FindGameByPlayerId(_player.Id);
            if (game == null)
            {
                await SendAsync(new ServerMessage
                {
                    Type = "error",
                    Payload = new { message = "Not in a game" }
                });
                return;
            }

            var row = payload.GetProperty("row").GetInt32();
            var col = payload.GetProperty("col").GetInt32();
            // Board использует (x,y) = (col, row); клиент шлёт row=Y, col=X
            await game.ProcessShotAsync(_player, col, row);
        }
        catch (Exception ex)
        {
            await SendAsync(new ServerMessage { Type = "error", Payload = new { message = ex.Message } });
        }
    }

    private async Task HandleShipPlacement(JsonElement payload)
    {
        try
        {
            if (_player == null)
            {
                await SendAsync(new ServerMessage
                {
                    Type = "error",
                    Payload = new { message = "Not connected" }
                });
                return;
            }

            var game = GameServer.Instance.GameManager.FindGameByPlayerId(_player.Id);
            var room = GameServer.Instance.GameManager.FindRoomByPlayerId(_player.Id);
            // Разрешаем расстановку, если игрок в комнате (игра может ещё не быть создана — второй не присоединился)
            if (game == null && room == null)
            {
                await SendAsync(new ServerMessage { Type = "error", Payload = new { message = "Not in a game" } });
                return;
            }

            if (!payload.TryGetProperty("ships", out var shipsElem) || shipsElem.ValueKind != JsonValueKind.Array)
            {
                await SendAsync(new ServerMessage { Type = "error", Payload = new { message = "Missing or invalid ships array" } });
                return;
            }

            _player.Board.Clear();
            foreach (var s in shipsElem.EnumerateArray())
            {
                var x = GetInt(s, "x");
                var y = GetInt(s, "y");
                var size = GetInt(s, "size");
                var horizontal = GetBool(s, "horizontal");
                if (x < 0 || x >= Board.Size || y < 0 || y >= Board.Size || size < 1 || size > 4)
                {
                    _player.Board.Clear();
                    await SendAsync(new ServerMessage { Type = "error", Payload = new { message = "Invalid ship coordinates or size" } });
                    return;
                }
                if (!_player.Board.PlaceShip(x, y, size, horizontal))
                {
                    _player.Board.Clear();
                    await SendAsync(new ServerMessage { Type = "error", Payload = new { message = "Invalid ship placement" } });
                    return;
                }
            }

            await SendAsync(new ServerMessage { Type = "shipPlacementResult", Payload = new { success = true } });
            Console.WriteLine($"Player {_player.Name} placed ships");
        }
        catch (Exception ex)
        {
            await SendAsync(new ServerMessage { Type = "error", Payload = new { message = ex.Message } });
        }
    }

    private async Task HandleReconnect(JsonElement payload)
    {
        try
        {
            var playerId = payload.GetProperty("playerId").GetString();

            var player = GameServer.Instance.GameManager.FindPlayerById(playerId);
            if (player == null)
            {
                await SendAsync(new ServerMessage
                {
                    Type = "error",
                    Payload = new { message = "Player not found" }
                });
                return;
            }

            player.Connection = this;
            _player = player;

            var opponentName = GameServer.Instance.GameManager
                .FindGameByPlayerId(player.Id)
                ?.GetOpponentPlayer()?.Name ?? "Unknown";

            await SendAsync(new ServerMessage
            {
                Type = "reconnected",
                Payload = new
                {
                    playerId = player.Id,
                    opponent = opponentName
                }
            });

            Console.WriteLine($"Player reconnected: {player.Name}");
        }
        catch (Exception ex)
        {
            await SendAsync(new ServerMessage { Type = "error", Payload = new { message = ex.Message } });
        }
    }

    private async Task HandleGetRooms()
    {
        try
        {
            var rooms = GameServer.Instance.GameManager.GetRooms()
                .Select(r => new { r.Id, r.Name, r.MaxPlayers, Players = r.Players.Count, r.IsGameStarted })
                .ToList();
            
            await SendAsync(new ServerMessage
            {
                Type = "RoomsList",
                Payload = new { rooms = rooms }
            });
        }
        catch (Exception ex)
        {
            await SendAsync(new ServerMessage { Type = "error", Payload = new { message = ex.Message } });
        }
    }

    private async Task HandleCreateRoom(JsonElement payload)
    {
        try
        {
            if (_player == null)
            {
                await SendAsync(new ServerMessage { Type = "error", Payload = new { message = "Not connected" } });
                return;
            }

            var roomName = payload.GetProperty("roomName").GetString() ?? "Room";
            var maxPlayers = payload.TryGetProperty("maxPlayers", out var mp) ? mp.GetInt32() : 2;

            Console.WriteLine($"[CreateRoom] Creating room: {roomName}, max players: {maxPlayers}");
            var room = GameServer.Instance.GameManager.CreateRoom(roomName, maxPlayers);
            room.CreatorId = _player.Id;
            Console.WriteLine($"[CreateRoom] Room created with ID: {room.Id}, creator: {_player.Name}");
            
            // Добавляем создателя в комнату автоматически
            Console.WriteLine($"[CreateRoom] Adding creator {_player.Name} to room");
            GameServer.Instance.GameManager.JoinRoom(_player, room);

            await SendAsync(new ServerMessage
            {
                Type = "RoomCreated",
                Payload = new { room = new { room.Id, room.Name, room.MaxPlayers, Players = room.Players.Count } }
            });

            Console.WriteLine($"[CreateRoom] Sent RoomCreated message to creator");

            // Отправляем обновленный список комнат всем клиентам
            Console.WriteLine($"[CreateRoom] Broadcasting rooms list to all clients");
            await BroadcastRoomsList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CreateRoom] ERROR: {ex.Message}");
            await SendAsync(new ServerMessage { Type = "error", Payload = new { message = ex.Message } });
        }
    }

    private async Task BroadcastRoomsList()
    {
        try
        {
            var rooms = GameServer.Instance.GameManager.GetRooms()
                .Select(r => new { r.Id, r.Name, r.MaxPlayers, Players = r.Players.Count })
                .ToList();

            Console.WriteLine($"[BroadcastRoomsList] Found {rooms.Count} rooms");

            var roomsMessage = new ServerMessage
            {
                Type = "RoomsList",
                Payload = new { rooms = rooms }
            };

            // Отправляем всем подключенным клиентам
            var allConnections = GameServer.Instance.GetAllConnections();
            Console.WriteLine($"[BroadcastRoomsList] Sending to {allConnections.Count} connected clients");
            
            foreach (var connection in allConnections)
            {
                await connection.SendAsync(roomsMessage);
            }
            
            Console.WriteLine($"[BroadcastRoomsList] ✅ Broadcast complete");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BroadcastRoomsList] ❌ ERROR: {ex.Message}");
        }
    }

    private async Task HandleJoinRoom(JsonElement payload)
    {
        try
        {
            if (_player == null)
            {
                await SendAsync(new ServerMessage { Type = "error", Payload = new { message = "Not connected" } });
                return;
            }

            var roomId = payload.GetProperty("roomId").GetString();
            var room = GameServer.Instance.GameManager.FindRoomById(roomId);

            if (room == null)
            {
                await SendAsync(new ServerMessage { Type = "error", Payload = new { message = "Room not found" } });
                return;
            }

            if (room.Players.Count >= room.MaxPlayers)
            {
                await SendAsync(new ServerMessage { Type = "error", Payload = new { message = "Места нет. Создайте другую комнату." } });
                return;
            }

            GameServer.Instance.GameManager.JoinRoom(_player, room);

            await SendAsync(new ServerMessage
            {
                Type = "JoinRoom",
                Payload = new { success = true, roomId = room.Id }
            });

            Console.WriteLine($"Player {_player.Name} joined room {room.Name}");
            Console.WriteLine($"Room now has {room.Players.Count}/{room.MaxPlayers} players");
            Console.WriteLine($"[JoinRoom] room.IsGameStarted = {room.IsGameStarted}");
            
            // Отправляем обновленный список комнат всем клиентам
            Console.WriteLine($"[JoinRoom] Broadcasting updated rooms list");
            await BroadcastRoomsList();

            // Если комната полна (2 игрока), просто регистрируем, что игра создана.
            // Старт игры произойдёт только после того, как оба игрока нажмут "Готов"
            // и сервер получит два сообщения playerready (см. HandlePlayerReady).
            Console.WriteLine($"[JoinRoom] Checking room full condition: Players.Count={room.Players.Count}, MaxPlayers={room.MaxPlayers}, IsGameStarted={room.IsGameStarted}");
            if (room.Players.Count >= room.MaxPlayers && room.IsGameStarted)
            {
                Console.WriteLine($"[JoinRoom] ✅ Room {room.Name} is FULL! Players: {room.Players.Count}/{room.MaxPlayers}");
                Console.WriteLine($"[JoinRoom] Player1: {room.Players[0].Name}, Player2: {room.Players[1].Name}");

                // Находим созданную игру
                var game = GameServer.Instance.GameManager.ActiveGames.FirstOrDefault(g =>
                    (g.Player1.Id == room.Players[0].Id && g.Player2.Id == room.Players[1].Id) ||
                    (g.Player1.Id == room.Players[1].Id && g.Player2.Id == room.Players[0].Id));

                if (game != null)
                {
                    Console.WriteLine($"[JoinRoom] Game session exists: {game.Player1.Name} vs {game.Player2.Name}. Waiting for both players to send playerready.");
                }
                else
                {
                    Console.WriteLine($"[JoinRoom] ❌ Game not found in ActiveGames!");
                }
            }
        }
        catch (Exception ex)
        {
            await SendAsync(new ServerMessage { Type = "error", Payload = new { message = ex.Message } });
        }
    }

    private async Task HandleChat(JsonElement payload)
    {
        try
        {
            if (_player == null)
                return;
            var room = GameServer.Instance.GameManager.FindRoomByPlayerId(_player.Id);
            if (room == null)
                return;
            var text = payload.TryGetProperty("text", out var t) ? t.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(text))
                return;
            var message = new ServerMessage
            {
                Type = "Chat",
                Payload = new { senderName = _player.Name, text }
            };
            foreach (var p in room.Players)
            {
                if (p.Id == _player.Id) continue;
                var conn = GameServer.Instance.GetConnectionByPlayerId(p.Id);
                if (conn != null)
                    await conn.SendAsync(message);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Chat] Error: {ex.Message}");
        }
    }

    private async Task HandleLeaveRoom(JsonElement payload)
    {
        try
        {
            if (_player == null)
            {
                await SendAsync(new ServerMessage { Type = "error", Payload = new { message = "Not connected" } });
                return;
            }

            var room = GameServer.Instance.GameManager.FindRoomByPlayerId(_player.Id);
            if (room == null)
            {
                await SendAsync(new ServerMessage { Type = "LeftRoom", Payload = new { success = true } });
                return;
            }

            if (room.CreatorId == _player.Id)
            {
                // Выход создателя — комната удаляется полностью, второго игрока уведомляем
                var others = room.Players.Where(p => p.Id != _player.Id).ToList();
                foreach (var other in others)
                {
                    var conn = GameServer.Instance.GetConnectionByPlayerId(other.Id);
                    if (conn != null)
                        await conn.SendAsync(new ServerMessage { Type = "RoomClosed", Payload = new { message = "Комната закрыта создателем." } });
                }
                GameServer.Instance.GameManager.RemoveRoom(room);
                await SendAsync(new ServerMessage { Type = "LeftRoom", Payload = new { success = true } });
                await BroadcastRoomsList();
                Console.WriteLine($"[LeaveRoom] Creator {_player.Name} left — room {room.Name} removed");
            }
            else
            {
                // Выход второго игрока — удаляем его из комнаты и игру, создателю — OpponentLeft
                var otherPlayer = room.Players.FirstOrDefault(p => p.Id != _player.Id);
                room.Players.Remove(_player);
                var game = GameServer.Instance.GameManager.FindGameByPlayerId(_player.Id);
                if (game != null)
                    GameServer.Instance.GameManager.RemoveGameOnly(game);
                room.PlayerReadyStatus.Remove(_player.Id);

                await SendAsync(new ServerMessage { Type = "LeftRoom", Payload = new { success = true } });
                if (otherPlayer != null)
                {
                    var otherConn = GameServer.Instance.GetConnectionByPlayerId(otherPlayer.Id);
                    if (otherConn != null)
                        await otherConn.SendAsync(new ServerMessage { Type = "OpponentLeft", Payload = new { message = "Соперник вышел из комнаты." } });
                }
                await BroadcastRoomsList();
                Console.WriteLine($"[LeaveRoom] Player {_player.Name} left room {room.Name}");
            }
        }
        catch (Exception ex)
        {
            await SendAsync(new ServerMessage { Type = "error", Payload = new { message = ex.Message } });
        }
    }

    private async Task HandlePlayerReady(JsonElement payload)
    {
        try
        {
            Console.WriteLine($"[DEBUG] ============ HandlePlayerReady START ============");
            Console.WriteLine($"[PlayerReady] Handler called");
            Console.WriteLine($"[DEBUG] Raw payload: {payload}");
            
            if (_player == null)
            {
                Console.WriteLine($"[DEBUG] ❌ _player is null");
                await SendAsync(new ServerMessage { Type = "error", Payload = new { message = "Not connected" } });
                return;
            }

            Console.WriteLine($"[DEBUG] _player.Id={_player.Id}, _player.Name={_player.Name}");

            var roomId = payload.GetProperty("roomId").GetString();
            Console.WriteLine($"[PlayerReady] roomId из сообщения: '{roomId}'");
            Console.WriteLine($"[DEBUG] Extracted roomId: '{roomId}'");
            
            var room = GameServer.Instance.GameManager.FindRoomById(roomId);

            if (room == null)
            {
                Console.WriteLine($"[ERROR] Комната не найдена: {roomId}");
                Console.WriteLine($"[DEBUG] ❌ Room not found with ID: {roomId}");
                await SendAsync(new ServerMessage { Type = "error", Payload = new { message = "Room not found" } });
                return;
            }

            Console.WriteLine($"[DEBUG] ✅ Found room: {room.Name} (ID={room.Id})");

            var game = GameServer.Instance.GameManager.FindGameByPlayerId(_player.Id);
            if (game == null)
            {
                // Первый игрок мог нажать «Готово» до прихода второго — сохраняем готовность в комнате
                room.PlayerReadyStatus[_player.Id] = true;
                _player.IsReady = true;
                Console.WriteLine($"[PlayerReady] Player {_player.Name} marked ready in room (game not created yet)");
                await SendAsync(new ServerMessage
                {
                    Type = "PlayerReady",
                    Payload = new { playerId = _player.Id, message = "You are ready" }
                });
                await SendAsync(new ServerMessage
                {
                    Type = "info",
                    Payload = new { message = "Waiting for opponent..." }
                });
                return;
            }

            Console.WriteLine($"[DEBUG] ✅ Found game: Player1={game.Player1.Name}, Player2={game.Player2?.Name ?? "null"}");
            Console.WriteLine($"[DEBUG] Before ready: Player1Ready={game.Player1Ready}, Player2Ready={game.Player2Ready}");

            // Отмечаем этого игрока как готовного
            if (_player.Id == game.Player1.Id)
            {
                game.Player1Ready = true;
                _player.IsReady = true;
                Console.WriteLine($"[PlayerReady] ✅ {_player.Name} (Player1) is ready");
                Console.WriteLine($"[DEBUG] Set Player1Ready=true");
                // Send confirmation to Player1
                await SendAsync(new ServerMessage
                {
                    Type = "PlayerReady",
                    Payload = new { playerId = _player.Id, message = "You are ready" }
                });
            }
            else
            {
                game.Player2Ready = true;
                _player.IsReady = true;
                Console.WriteLine($"[PlayerReady] ✅ {_player.Name} (Player2) is ready");
                Console.WriteLine($"[DEBUG] Set Player2Ready=true");
                // Send confirmation to Player2
                await SendAsync(new ServerMessage
                {
                    Type = "PlayerReady",
                    Payload = new { playerId = _player.Id, message = "You are ready" }
                });
            }

            Console.WriteLine($"[DEBUG] After update: Player1Ready={game.Player1Ready}, Player2Ready={game.Player2Ready}");
            Console.WriteLine($"[DEBUG] game.BothPlayersReady={game.BothPlayersReady}");

            // Проверяем готовность обоих игроков
            if (game.BothPlayersReady)
            {
                Console.WriteLine($"[PlayerReady] Both players are ready! Starting game...");
                Console.WriteLine($"[DEBUG] ✅ BOTH PLAYERS READY - SENDING GAMESTART");

                // Отправляем GameStart обоим игрокам
                var player1Conn = GameServer.Instance.GetConnectionByPlayerId(game.Player1.Id);
                var player2Conn = GameServer.Instance.GetConnectionByPlayerId(game.Player2.Id);

                Console.WriteLine($"[DEBUG] Player1 connection: {(player1Conn != null ? "✅ Found" : "❌ Not found")}");
                Console.WriteLine($"[DEBUG] Player2 connection: {(player2Conn != null ? "✅ Found" : "❌ Not found")}");

                var p1Ships = game.Player1.Board.GetShipCoordinates().Select(c => new { x = c.x, y = c.y }).ToList();
                var p2Ships = game.Player2.Board.GetShipCoordinates().Select(c => new { x = c.x, y = c.y }).ToList();

                if (player1Conn != null)
                {
                    Console.WriteLine($"[DEBUG] Sending GameStart to Player1 ({game.Player1.Id})");
                    await player1Conn.SendAsync(new ServerMessage
                    {
                        Type = "GameStart",
                        Payload = new
                        {
                            success = true,
                            firstPlayer = game.Player1.Id,
                            isYourTurn = true,
                            myShips = p1Ships
                        }
                    });
                    Console.WriteLine($"[DEBUG] ✅ GameStart sent to Player1");
                }

                if (player2Conn != null)
                {
                    Console.WriteLine($"[DEBUG] Sending GameStart to Player2 ({game.Player2.Id})");
                    await player2Conn.SendAsync(new ServerMessage
                    {
                        Type = "GameStart",
                        Payload = new
                        {
                            success = true,
                            firstPlayer = game.Player1.Id,
                            isYourTurn = false,
                            myShips = p2Ships
                        }
                    });
                    Console.WriteLine($"[DEBUG] ✅ GameStart sent to Player2");
                }

                Console.WriteLine($"[PlayerReady] GameStart sent to both players");
                Console.WriteLine($"[DEBUG] ============ HandlePlayerReady END (GAMESTART SENT) ============");
            }
            else
            {
                // Только этот игрок готов, ждем второго
                Console.WriteLine($"[PlayerReady] Waiting for opponent to be ready...");
                Console.WriteLine($"[DEBUG] ⏳ Waiting for other player (Player1Ready={game.Player1Ready}, Player2Ready={game.Player2Ready})");
                await SendAsync(new ServerMessage
                {
                    Type = "info",
                    Payload = new { message = "Waiting for opponent..." }
                });
                Console.WriteLine($"[DEBUG] ============ HandlePlayerReady END (WAITING) ============");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlayerReady] ERROR: {ex.Message}");
            Console.WriteLine($"[DEBUG] ❌ Exception in HandlePlayerReady: {ex}");
            Console.WriteLine($"[DEBUG] ============ HandlePlayerReady END (EXCEPTION) ============");
            await SendAsync(new ServerMessage { Type = "error", Payload = new { message = ex.Message } });
        }
    }

    private async Task HandlePlayAgain()
    {
        try
        {
            Console.WriteLine($"[DEBUG] ============ HandlePlayAgain START ============");
            
            if (_player == null)
            {
                Console.WriteLine("[PlayAgain] ERROR: No player");
                await SendAsync(new ServerMessage { Type = "error", Payload = new { message = "Not connected" } });
                return;
            }

            var game = GameServer.Instance.GameManager.FindGameByPlayerId(_player.Id);
            if (game == null)
            {
                Console.WriteLine("[PlayAgain] ERROR: No game");
                await SendAsync(new ServerMessage { Type = "error", Payload = new { message = "Not in a game" } });
                return;
            }

            // Determine which player is sending the message
            if (game.Player1.Id == _player.Id)
            {
                game.Player1WantsPlayAgain = true;
                Console.WriteLine($"[PlayAgain] Player1 ({_player.Id}) wants to play again");
            }
            else if (game.Player2.Id == _player.Id)
            {
                game.Player2WantsPlayAgain = true;
                Console.WriteLine($"[PlayAgain] Player2 ({_player.Id}) wants to play again");
            }
            else
            {
                Console.WriteLine("[PlayAgain] ERROR: Player ID doesn't match either player");
                await SendAsync(new ServerMessage { Type = "error", Payload = new { message = "Invalid player ID" } });
                return;
            }

            // Check if both players want to play again
            if (game.BothPlayersWantPlayAgain)
            {
                Console.WriteLine("[PlayAgain] Both players want to play again - resetting game");
                game.ResetForNewGame();

                // Get both player connections
                var player1Conn = GameServer.Instance.GetConnectionByPlayerId(game.Player1.Id);
                var player2Conn = GameServer.Instance.GetConnectionByPlayerId(game.Player2.Id);

                // Send ReturnToPlacement message to both players
                var returnMessage = new ServerMessage
                {
                    Type = "ReturnToPlacement",
                    Payload = new { message = "Both players agreed to play again. Returning to ship placement..." }
                };

                if (player1Conn != null)
                {
                    await player1Conn.SendAsync(returnMessage);
                    Console.WriteLine("[PlayAgain] Sent ReturnToPlacement to Player1");
                }

                if (player2Conn != null)
                {
                    await player2Conn.SendAsync(returnMessage);
                    Console.WriteLine("[PlayAgain] Sent ReturnToPlacement to Player2");
                }
            }
            else
            {
                // Notify the player that we're waiting for the opponent
                await SendAsync(new ServerMessage
                {
                    Type = "info",
                    Payload = new { message = "Waiting for opponent to agree to play again..." }
                });
                Console.WriteLine($"[PlayAgain] Waiting for opponent - Player1 wants: {game.Player1WantsPlayAgain}, Player2 wants: {game.Player2WantsPlayAgain}");
            }

            Console.WriteLine($"[DEBUG] ============ HandlePlayAgain END ============");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlayAgain] ERROR: {ex.Message}");
            Console.WriteLine($"[DEBUG] ❌ Exception in HandlePlayAgain: {ex}");
            await SendAsync(new ServerMessage { Type = "error", Payload = new { message = ex.Message } });
        }
    }

    public async Task SendAsync(ServerMessage message)
    {
        try
        {
            await _sendSemaphore.WaitAsync();
            
            try
            {
                if (_webSocket.State == WebSocketState.Open)
                {
                    var json = JsonSerializer.Serialize(message);
                    Console.WriteLine($"[SendAsync] Sending {message.Type}: {json.Substring(0, Math.Min(100, json.Length))}...");
                    var buffer = Encoding.UTF8.GetBytes(json);

                    await _webSocket.SendAsync(
                        new ArraySegment<byte>(buffer),
                        WebSocketMessageType.Text,
                        true,
                        CancellationToken.None
                    );
                    Console.WriteLine($"[SendAsync] ✅ Message sent");
                }
                else
                {
                    Console.WriteLine($"[SendAsync] ❌ WebSocket not open, state: {_webSocket.State}");
                }
            }
            finally
            {
                _sendSemaphore.Release();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SendAsync] ❌ Error sending message: {ex.Message}");
        }
    }

    private static int GetInt(JsonElement e, string name)
    {
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value.TryGetInt32(out var i) ? i : 0;
        return 0;
    }

    private static bool GetBool(JsonElement e, string name)
    {
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind == JsonValueKind.True;
        return false;
    }
}
