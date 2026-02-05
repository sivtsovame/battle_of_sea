using battle_of_sea.Game;
using battle_of_sea.Protocol;
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace battle_of_sea.Network
{
    public class ClientConnection
    {
        private readonly TcpClient _client;
        private Player _player;

        public ClientConnection(TcpClient client)
        {
            _client = client;
        }
        public class GameServer
        {
            private static GameServer _instance;
            public static GameServer Instance => _instance ??= new GameServer();

            public GameManager GameManager { get; private set; } = new GameManager();
        }
        public async Task HandleAsync()
        {
            try
            {
                using (_client)
                {
                    var stream = _client.GetStream();
                    var reader = new StreamReader(stream, new UTF8Encoding(false));
                    var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                    while (true)
                    {
                        string line = await reader.ReadLineAsync();
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        Console.WriteLine($"Received: {line}");

                        ClientMessage message;
                        try
                        {
                            message = JsonSerializer.Deserialize<ClientMessage>(line, options);
                        }
                        catch
                        {
                            await SendAsync( new ServerMessage { Type = "error", Payload = new { message = "Invalid JSON" } });
                            continue;
                        }

                        if (message == null || string.IsNullOrEmpty(message.Type))
                        {
                            await SendAsync( new ServerMessage { Type = "error", Payload = new { message = "Missing Type" } });
                            continue;
                        }

                        await HandleMessageAsync(message, writer);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Client error: {ex.Message}");
            }
            finally
            {
                if (_player != null)
                {
                    Console.WriteLine($"Player disconnected: {_player.Name}");
                    // ❗ НЕ удаляем игрока из игры
                    // он может переподключиться
                }
                ;
            }
        }

        private async Task HandleMessageAsync(ClientMessage message, StreamWriter writer)
        {
            switch (message.Type.ToLower())
            {
                case "connect":
                    {
                        string playerName = message.Payload.GetProperty("playerName").GetString();
                        string playerId = Guid.NewGuid().ToString();

                        _player = new Player
                        {
                            Id = playerId,
                            Name = playerName,
                            Connection = this
                        };

                        GameServer.Instance.GameManager.AddPlayer(_player);

                        await SendAsync(new ServerMessage
                        {
                            Type = "connected",
                            Payload = new { playerId }
                        });

                        Console.WriteLine($"Player connected: {playerName} ({playerId})");
                        break;
                    }
                case "ping":
                    await SendAsync(new ServerMessage { Type = "pong", Payload = new { } });
                    break;

                case "createroom":
                    {
                        if (_player == null)
                        {
                            await SendAsync(new ServerMessage
                            {
                                Type = "error",
                                Payload = new { message = "Not connected" }
                            });
                            break;
                        }

                        string roomName = message.Payload.GetProperty("roomName").GetString();
                        int maxPlayers = 2;

                        var room = GameServer.Instance.GameManager.CreateRoom(roomName, maxPlayers);
                        GameServer.Instance.GameManager.JoinRoom(_player, room);
                        room.PlayerReadyStatus[_player.Id] = false;

                        Console.WriteLine($"[ROOM_CREATED] Room {room.Name} (ID: {room.Id}) created by {_player.Name}");

                        // Отправляем создателю информацию о комнате
                        await SendAsync(new ServerMessage
                        {
                            Type = "RoomCreated",
                            Payload = new
                            {
                                roomId = room.Id,
                                roomName = room.Name,
                                maxPlayers = room.MaxPlayers
                            }
                        });

                        // Отправляем обновленный список всем
                        await BroadcastRoomsList();
                        break;
                    }

                case "joinroom":
                    {
                        if (_player == null)
                        {
                            await SendAsync(new ServerMessage
                            {
                                Type = "error",
                                Payload = new { message = "Not connected" }
                            });
                            break;
                        }

                        string roomId = message.Payload.GetProperty("roomId").GetString();
                        var room = GameServer.Instance.GameManager.FindRoomById(roomId);

                        if (room == null)
                        {
                            await SendAsync(new ServerMessage
                            {
                                Type = "JoinRoomResult",
                                Payload = new
                                {
                                    success = false,
                                    message = "Room not found"
                                }
                            });
                            break;
                        }

                        if (room.IsGameStarted)
                        {
                            await SendAsync(new ServerMessage
                            {
                                Type = "JoinRoomResult",
                                Payload = new
                                {
                                    success = false,
                                    message = "Game already started"
                                }
                            });
                            break;
                        }

                        if (room.Players.Count >= room.MaxPlayers)
                        {
                            await SendAsync(new ServerMessage
                            {
                                Type = "JoinRoomResult",
                                Payload = new
                                {
                                    success = false,
                                    message = "Room is full"
                                }
                            });
                            break;
                        }

                        GameServer.Instance.GameManager.JoinRoom(_player, room);
                        room.PlayerReadyStatus[_player.Id] = false;

                        Console.WriteLine($"[ROOM_JOINED] Player {_player.Name} joined room {room.Name}");

                        // Отправляем результат присоединения
                        await SendAsync(new ServerMessage
                        {
                            Type = "JoinRoomResult",
                            Payload = new
                            {
                                success = true,
                                roomId = room.Id,
                                roomName = room.Name,
                                players = room.Players.Select(p => new { id = p.Id, name = p.Name }).ToList(),
                                message = "Joined room successfully"
                            }
                        });

                        // Отправляем обновленный список всем
                        await BroadcastRoomsList();
                        break;
                    }

                case "playerready":
                    {
                        if (_player == null)
                        {
                            await SendAsync(new ServerMessage
                            {
                                Type = "error",
                                Payload = new { message = "Not connected" }
                            });
                            break;
                        }

                        var room = GameServer.Instance.GameManager.FindRoomByPlayerId(_player.Id);
                        if (room == null)
                        {
                            await SendAsync(new ServerMessage
                            {
                                Type = "error",
                                Payload = new { message = "Not in a room" }
                            });
                            break;
                        }

                        bool success = GameServer.Instance.GameManager.MarkPlayerReady(_player.Id);
                        if (!success)
                        {
                            await SendAsync(new ServerMessage
                            {
                                Type = "error",
                                Payload = new { message = "Failed to mark player as ready" }
                            });
                            break;
                        }

                        // Отправляем подтверждение
                        await SendAsync(new ServerMessage
                        {
                            Type = "PlayerReady",
                            Payload = new
                            {
                                playerId = _player.Id,
                                message = "Player ready"
                            }
                        });

                        Console.WriteLine($"[PLAYER_READY] {_player.Name} is ready in room {room.Name}");

                        // Проверяем, готовы ли все игроки
                        if (room.AreAllPlayersReady())
                        {
                            bool gameStarted = GameServer.Instance.GameManager.CheckAndStartGame(room);
                            if (gameStarted)
                            {
                                Console.WriteLine($"[GAME_STARTED] Game starting in room {room.Name}");

                                // Отправляем обоим игрокам сообщение о старте игры
                                var gameStartMessage = new ServerMessage
                                {
                                    Type = "GameStateChanged",
                                    Payload = new
                                    {
                                        state = "GameStarted",
                                        roomId = room.Id,
                                        players = room.Players.Select(p => new { id = p.Id, name = p.Name }).ToList()
                                    }
                                };

                                foreach (var player in room.Players)
                                {
                                    if (player.Connection is ClientConnection conn)
                                    {
                                        await conn.SendAsync(gameStartMessage);
                                    }
                                }
                            }
                        }
                        break;
                    }

                case "playagain":
                    {
                        if (_player == null)
                        {
                            await SendAsync(new ServerMessage
                            {
                                Type = "error",
                                Payload = new { message = "Not connected" }
                            });
                            break;
                        }

                        var game = GameServer.Instance.GameManager.FindGameByPlayerId(_player.Id);
                        if (game == null)
                        {
                            await SendAsync(new ServerMessage
                            {
                                Type = "error",
                                Payload = new { message = "Not in a game" }
                            });
                            break;
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
                            await SendAsync(new ServerMessage { Type = "error", Payload = new { message = "Invalid player ID" } });
                            break;
                        }

                        // Check if both players want to play again
                        if (game.BothPlayersWantPlayAgain)
                        {
                            Console.WriteLine("[PlayAgain] Both players want to play again - resetting game");
                            game.ResetForNewGame();

                            // Get both player connections
                            var player1Conn = game.Player1.Connection as ClientConnection;
                            var player2Conn = game.Player2.Connection as ClientConnection;

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

                        break;
                    }

                case "roomslist":
                    {
                        await BroadcastRoomsList();
                        break;
                    }

                case "shoot":
                    {
                        if (_player == null)
                        {
                            await SendAsync(new ServerMessage
                            {
                                Type = "error",
                                Payload = new { message = "Not connected" }
                            });
                            break;
                        }

                        var game = GameServer.Instance.GameManager.FindGameByPlayerId(_player.Id);
                        if (game == null)
                        {
                            await SendAsync(new ServerMessage
                            {
                                Type = "error",
                                Payload = new { message = "Not in a game" }
                            });
                            break;
                        }

                        int x = message.Payload.GetProperty("x").GetInt32();
                        int y = message.Payload.GetProperty("y").GetInt32();

                        await game.ProcessShotAsync(_player, x, y);

                        break;
                    }
                case "reconnect":
                    {
                        string playerId = message.Payload.GetProperty("playerId").GetString();

                        var player = GameServer.Instance.GameManager.FindPlayerById(playerId);
                        if (player == null)
                        {
                            await SendAsync(new ServerMessage
                            {
                                Type = "error",
                                Payload = new { message = "Player not found" }
                            });
                            break;
                        }

                        // 🔄 Перепривязываем соединение
                        player.Connection = this;
                        _player = player;

                        await SendAsync(new ServerMessage
                        {
                            Type = "reconnected",
                            Payload = new
                            {
                                playerId = player.Id,
                                opponent = GameServer.Instance.GameManager
                                    .FindGameByPlayerId(player.Id)
                                    ?.GetOpponentPlayer().Name
                            }
                        });

                        Console.WriteLine($"Player reconnected: {player.Name}");
                        break;
                    }

                default:
                    await SendAsync(new ServerMessage { Type = "error", Payload = new { message = "Unknown command" } });
                    break;
            }
        }

        private async Task BroadcastRoomsList()
        {
            var rooms = GameServer.Instance.GameManager.GetAvailableRooms();
            var roomsList = new ServerMessage
            {
                Type = "RoomsList",
                Payload = new
                {
                    rooms = rooms.Select(r => new
                    {
                        id = r.Id,
                        name = r.Name,
                        maxPlayers = r.MaxPlayers,
                        currentPlayers = r.Players.Count,
                        isGameStarted = r.IsGameStarted,
                        players = r.Players.Select(p => new { id = p.Id, name = p.Name }).ToList()
                    }).ToList()
                }
            };

            // Отправляем всем подключенным клиентам
            var allPlayers = GameServer.Instance.GameManager.Players;
            foreach (var player in allPlayers)
            {
                if (player.Connection is ClientConnection connection)
                {
                    await connection.SendAsync(roomsList);
                }
            }

            Console.WriteLine($"[BROADCAST] RoomsList sent to {allPlayers.Count} players");
        }

        public Task SendAsync(ServerMessage message)
        {
            var stream = _client.GetStream();
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            string json = JsonSerializer.Serialize(message);
            return writer.WriteLineAsync(json);
        }

    }
}
