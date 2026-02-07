using battle_of_sea.Protocol;
using battle_of_sea.Network;
using System;
using System.Timers;

namespace battle_of_sea.Game
{
    public class GameSession
    {
        public Player Player1 { get; }
        public Player Player2 { get; }
        public bool IsFinished { get; private set; }
        public bool Player1Ready { get; set; } = false;
        public bool Player2Ready { get; set; } = false;
        public bool Player1WantsPlayAgain { get; set; } = false;
        public bool Player2WantsPlayAgain { get; set; } = false;
        /// <summary>
        /// Флаг "игра переигрывается": оба выбрали PlayAgain и находятся на экране расстановки.
        /// Пока он true, выход любого игрока из комнаты должен удалять комнату.
        /// </summary>
        public bool RestartPending { get; set; } = false;

        public string CurrentTurnPlayerId { get; private set; }

        /// <summary>Unix-время (мс) начала текущего хода — передаётся клиентам для одинакового отображения таймера.</summary>
        public long TurnStartedAtUtcMs { get; private set; }

        private readonly System.Timers.Timer _turnTimer;
        private const int TurnTimeMs = 30_000; // 30 секунд
        
        // Событие завершения игры
        public event Action<GameSession>? GameFinished;
        
        public GameSession(Player p1, Player p2)
        {
            Player1 = p1;
            Player2 = p2;
            CurrentTurnPlayerId = p1.Id;

            _turnTimer = new System.Timers.Timer(TurnTimeMs);
            _turnTimer.AutoReset = false;
            _turnTimer.Elapsed += OnTurnTimeout;

            // Таймер первого хода запускается только после того,
            // как оба игрока нажали "Готов" (см. HandlePlayerReady).
        }

        public Player GetCurrentPlayer() =>
            CurrentTurnPlayerId == Player1.Id ? Player1 : Player2;

        public Player GetOpponentPlayer() =>
            CurrentTurnPlayerId == Player1.Id ? Player2 : Player1;

        public bool BothPlayersReady => Player1Ready && Player2Ready;
        public bool BothPlayersWantPlayAgain => Player1WantsPlayAgain && Player2WantsPlayAgain;

        public void SwitchTurn()
        {
            CurrentTurnPlayerId = GetOpponentPlayer().Id;
            StartTurnTimer();
        }

        /// <summary>
        /// Запускает/перезапускает серверный таймер хода на полные 30 секунд.
        /// </summary>
        public void StartTurnTimer()
        {
            _turnTimer.Stop();
            _turnTimer.Start();
            TurnStartedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        private async void OnTurnTimeout(object sender, ElapsedEventArgs e)
        {
            // Игра уже завершена (например, последний выстрел потопил все корабли) — не переключаем ход.
            // Иначе "опоздавший" колбэк таймера мог бы переключить ход после победы.
            if (IsFinished)
            {
                Console.WriteLine("Turn timeout ignored: game already finished.");
                return;
            }

            var timedOutPlayer = GetCurrentPlayer();
            var opponent = GetOpponentPlayer();

            Console.WriteLine($"Turn timeout: {timedOutPlayer.Name}");

            // Сначала переключаем ход и стартуем таймер, чтобы turnStartedAt был один для обоих клиентов
            SwitchTurn();
            var turnStartedAt = TurnStartedAtUtcMs;

            await SendMessageToPlayer(timedOutPlayer,
                new ServerMessage { Type = "turn_timeout", Payload = new { turnStartedAt } });
            await SendMessageToPlayer(opponent,
                new ServerMessage { Type = "your_turn", Payload = new { turnStartedAt } });
        }

        public async Task ProcessShotAsync(Player shooter, int x, int y)
        {
            // Останавливаем таймер на время обработки выстрела, чтобы колбэк таймаута не сработал
            // между выстрелом и проверкой победы (особенно при серии попаданий без промахов).
            _turnTimer.Stop();

            // Проверка хода (дополнительная защита)
            if (shooter.Id != CurrentTurnPlayerId)
            {
                Console.WriteLine($"[ProcessShot] Not your turn: shooter.Id={shooter.Id}, CurrentTurnPlayerId={CurrentTurnPlayerId}, P1.Id={Player1.Id}, P2.Id={Player2.Id}");
                StartTurnTimer(); // восстанавливаем таймер текущего хода
                await SendMessageToPlayer(shooter, new ServerMessage
                {
                    Type = "error",
                    Payload = new { message = "Not your turn" }
                });
                return;
            }

            var opponent = GetOpponentPlayer();

            var result = opponent.Board.Shoot(x, y);

            // При Hit/Sunk таймер перезапускаем и передаём turnStartedAt, чтобы клиент показывал те же 30 сек
            if (result == ShotResult.Hit || result == ShotResult.Sunk)
                StartTurnTimer();
            var turnStartedAt = TurnStartedAtUtcMs;

            await SendMessageToPlayer(shooter, new ServerMessage
            {
                Type = "ShootResult",
                Payload = new { x, y, result = result.ToString(), turnStartedAt }
            });

            await SendMessageToPlayer(opponent, new ServerMessage
            {
                Type = "OpponentShoot",
                Payload = new { x, y, result = result.ToString() }
            });

            // Победа?
            if (opponent.Board.IsDefeated())
            {
                _turnTimer.Stop();
                

                await SendMessageToPlayer(shooter, new ServerMessage
                {
                    Type = "GameOver",
                    Payload = new { winner = shooter.Name }
                });

                await SendMessageToPlayer(opponent, new ServerMessage
                {
                    Type = "GameOver",
                    Payload = new { winner = shooter.Name }
                });
                IsFinished = true;
                
                // Вызываем событие завершения игры
                GameFinished?.Invoke(this);
                return;
            }

            // Передаём ход только при промахе; при Hit/Sunk стреляющий стреляет снова (как в правилах морского боя).
            // При этом таймер хода всегда перезапускается заново на 30 секунд, независимо от результата выстрела.
            if (result == ShotResult.Miss)
            {
                SwitchTurn();
                var nextPlayer = GetCurrentPlayer();
                var prevPlayer = GetOpponentPlayer();
                var startedAt = TurnStartedAtUtcMs;
                await SendMessageToPlayer(nextPlayer,
                    new ServerMessage { Type = "YourTurn", Payload = new { turnStartedAt = startedAt } });
                await SendMessageToPlayer(prevPlayer,
                    new ServerMessage { Type = "OpponentTurn", Payload = new { turnStartedAt = startedAt } });
            }
            // при Hit/Sunk ход не переключается — ShootResult уже отправлен, клиент оставит isMyTurn=true
        }

        public void ResetForNewGame()
        {
            // Сбрасываем флаги готовности и флаги "хочу еще раз"
            Player1Ready = false;
            Player2Ready = false;
            Player1WantsPlayAgain = false;
            Player2WantsPlayAgain = false;
            
            // Сбрасываем доски
            Player1.Board.Reset();
            Player2.Board.Reset();
            
            // Сбрасываем текущий ход
            CurrentTurnPlayerId = Player1.Id;
            
            // Сбрасываем флаг завершения
            IsFinished = false;
            // Помечаем, что мы находимся в состоянии "переигровки" (оба на экране расстановки)
            RestartPending = true;

            // Таймер новой игры запустится, когда оба снова нажмут "Готов"
        }

        private async Task SendMessageToPlayer(Player player, ServerMessage message)
        {
            try
            {
                if (player.Connection is WebSocketConnection wsConnection)
                {
                    await wsConnection.SendAsync(message);
                }
                else if (player.Connection is ClientConnection tcpConnection)
                {
                    await tcpConnection.SendAsync(message);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending message to player {player.Name}: {ex.Message}");
            }
        }
    }
}