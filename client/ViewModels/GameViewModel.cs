using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using client.Models;
using client.Services;
using client.Utils;

namespace client.ViewModels;

public enum ShotState { None, Miss, Hit, Sunk }

public class GameCellViewModel : INotifyPropertyChanged
{
    private bool _hasShip;
    private ShotState _shotState;
    private IBrush _cellBackground;

    public int X { get; }
    public int Y { get; }

    /// <summary>Для ячеек поля противника — команда выстрела (задаётся из GameViewModel).</summary>
    public ICommand? ShootCommand { get; set; }

    public bool HasShip
    {
        get => _hasShip;
        set 
        { 
            _hasShip = value; 
            OnPropertyChanged(); 
            UpdateBackground();
        }
    }

    public ShotState ShotState
    {
        get => _shotState;
        set 
        { 
            _shotState = value; 
            OnPropertyChanged(); 
            OnPropertyChanged(nameof(ShotStateText)); 
            UpdateBackground();
        }
    }

    public string ShotStateText => _shotState.ToString();

    public IBrush CellBackground
    {
        get => _cellBackground ?? Brushes.Transparent;
        set 
        { 
            if (_cellBackground != value)
            {
                _cellBackground = value;
                OnPropertyChanged();
            }
        }
    }

    private void UpdateBackground()
    {
        // Приоритет: выстрелы > корабли > пусто
        if (ShotState == ShotState.Sunk) 
        {
            CellBackground = Brushes.DarkRed;
        }
        else if (ShotState == ShotState.Hit) 
        {
            CellBackground = Brushes.Red;
        }
        else if (ShotState == ShotState.Miss) 
        {
            CellBackground = Brushes.LightGray;
        }
        else if (HasShip) 
        {
            CellBackground = Brushes.SteelBlue;
        }
        else 
        {
            CellBackground = Brushes.Transparent;
        }
    }

    public GameCellViewModel(int x, int y)
    {
        X = x;
        Y = y;
        _cellBackground = Brushes.Transparent;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class GameViewModel : INotifyPropertyChanged
{
    private readonly GameServerClient _client;
    private string _status;
    private bool _isMyTurn;
    private bool _gameOver;
    private bool _isWinner;
    private string _gameResult;
    private bool _gameResultVisible;
    private int _turnSecondsLeft = 30;
    private CancellationTokenSource? _turnTimerCts;

    public RoomInfo Room { get; }

    /// <summary>Секунд осталось на ход (30 при начале хода, обновляется каждую секунду).</summary>
    public int TurnSecondsLeft
    {
        get => _turnSecondsLeft;
        set { _turnSecondsLeft = value; OnPropertyChanged(); }
    }

    public ObservableCollection<GameCellViewModel> MyBoardCells { get; } = new();
    public ObservableCollection<GameCellViewModel> EnemyBoardCells { get; } = new();

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public bool IsMyTurn
    {
        get => _isMyTurn;
        set
        {
            _isMyTurn = value;
            OnPropertyChanged();
            RaiseShootCanExecute();
            if (value)
                StartTurnCountdown();
            else
                StopTurnCountdown();
        }
    }

    public bool GameOver
    {
        get => _gameOver;
        set { _gameOver = value; OnPropertyChanged(); }
    }

    public bool IsWinner
    {
        get => _isWinner;
        set { _isWinner = value; OnPropertyChanged(); }
    }

    public string GameResult
    {
        get => _gameResult;
        set { _gameResult = value; OnPropertyChanged(); }
    }

    public bool GameResultVisible
    {
        get => _gameResultVisible;
        set { _gameResultVisible = value; OnPropertyChanged(); }
    }

    public ICommand ShootCommand { get; }
    public ICommand PlayAgainCommand { get; }
    public ICommand SendChatCommand { get; }

    public ObservableCollection<ChatMessageItem> ChatMessages { get; } = new();
    private string _chatText = "";
    public string ChatText
    {
        get => _chatText;
        set { _chatText = value ?? ""; OnPropertyChanged(); (SendChatCommand as RelayCommand)?.RaiseCanExecuteChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<RoomInfo>? ReturnToPlacementRequested;
    public event Action? ReturnToMainMenuRequested;

    public GameViewModel(GameServerClient client, RoomInfo room, bool isYourTurn, IReadOnlyList<(int x, int y)> myShips)
    {
        _client = client;
        Room = room;
        _isMyTurn = isYourTurn;
        _status = isYourTurn ? "Ваш ход. Выберите клетку на поле противника." : "Ход соперника.";
        _gameOver = false;
        _gameResult = string.Empty;
        _gameResultVisible = false;

        for (int y = 0; y < 10; y++)
            for (int x = 0; x < 10; x++)
            {
                var c = new GameCellViewModel(x, y);
                if (myShips.Any(s => s.x == x && s.y == y))
                    c.HasShip = true;
                MyBoardCells.Add(c);
            }

        for (int y = 0; y < 10; y++)
            for (int x = 0; x < 10; x++)
                EnemyBoardCells.Add(new GameCellViewModel(x, y));

        ICommand? shootCmd = null;
        shootCmd = new RelayCommand(async param =>
        {
            if (param is GameCellViewModel cell && !_gameOver && _isMyTurn && cell.ShotState == ShotState.None)
                await _client.SendAsync("shoot", new { row = cell.Y, col = cell.X });
        }, param => !_gameOver && _isMyTurn && param is GameCellViewModel c && c.ShotState == ShotState.None);
        ShootCommand = shootCmd;

        ICommand? playAgainCmd = null;
        playAgainCmd = new RelayCommand(async _ =>
        {
            await _client.SendAsync("playAgain", new { });
        });
        PlayAgainCommand = playAgainCmd;

        SendChatCommand = new RelayCommand(async _ => await SendChatAsync(), _ => !string.IsNullOrWhiteSpace(ChatText));

        foreach (var cell in EnemyBoardCells)
        {
            cell.ShootCommand = ShootCommand;
            cell.PropertyChanged += (_, _) => (ShootCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        _client.MessageReceived += OnServerMessage;

        if (isYourTurn)
            StartTurnCountdown();
    }

    private async Task SendChatAsync()
    {
        var text = (ChatText ?? "").Trim();
        if (string.IsNullOrEmpty(text)) return;
        var myName = Room.MyPlayerName ?? "Я";
        ChatMessages.Add(new ChatMessageItem { Sender = myName, Text = text, IsMine = true });
        ChatText = "";
        OnPropertyChanged(nameof(ChatText));
        await _client.SendAsync("chat", new { text });
    }

    private const int TurnDurationSeconds = 30;

    private async void StartTurnCountdown()
    {
        StopTurnCountdown();
        _turnTimerCts = new CancellationTokenSource();
        var token = _turnTimerCts.Token;
        for (int s = TurnDurationSeconds; s >= 0 && !token.IsCancellationRequested; s--)
        {
            TurnSecondsLeft = s;
            try
            {
                await Task.Delay(1000, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void StopTurnCountdown()
    {
        _turnTimerCts?.Cancel();
        _turnTimerCts = null;
        TurnSecondsLeft = 30;
    }

    private void RaiseShootCanExecute() => (ShootCommand as RelayCommand)?.RaiseCanExecuteChanged();

    private void OnServerMessage(string type, JsonElement payload)
    {
        if (string.Equals(type, "ShootResult", StringComparison.OrdinalIgnoreCase))
        {
            var x = GetInt(payload, "x");
            var y = GetInt(payload, "y");
            var res = GetString(payload, "result");
            
            var cell = GetEnemyCell(x, y);
            if (cell != null)
            {
                var newState = ParseShotState(res);
                cell.ShotState = newState;
                
                // При попадании/потоплении — стреляем ещё раз; при промахе ждём YourTurn/OpponentTurn
                if (res == "Hit" || res == "Sunk")
                {
                    IsMyTurn = true; // таймер перезапустится через set
                    Status = "Вы попали! Ваш ход продолжается. Выберите клетку на поле противника.";
                    StartTurnCountdown();
                }
            }
        }
        else if (string.Equals(type, "OpponentShoot", StringComparison.OrdinalIgnoreCase))
        {
            var x = GetInt(payload, "x");
            var y = GetInt(payload, "y");
            var res = GetString(payload, "result");
            
            var cell = GetMyCell(x, y);
            if (cell != null)
            {
                var newState = ParseShotState(res);
                cell.ShotState = newState;
            }
        }
        else if (string.Equals(type, "YourTurn", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(type, "your_turn", StringComparison.OrdinalIgnoreCase))
        {
            IsMyTurn = true;
            Status = "Ваш ход. Выберите клетку на поле противника.";
        }
        else if (string.Equals(type, "OpponentTurn", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(type, "opponent_turn", StringComparison.OrdinalIgnoreCase))
        {
            IsMyTurn = false;
            Status = "Ход соперника.";
        }
        else if (string.Equals(type, "GameOver", StringComparison.OrdinalIgnoreCase))
        {
            var winner = GetString(payload, "winner");
            var myName = Room.MyPlayerName;
            
            GameOver = true;
            IsMyTurn = false;
            
            if (string.Equals(winner, myName, StringComparison.OrdinalIgnoreCase))
            {
                IsWinner = true;
                GameResult = $"Вы выиграли! Поздравляем!";
                Status = "Игра окончена. Вы победили!";
            }
            else
            {
                IsWinner = false;
                GameResult = $"Вы проиграли. {winner} победил.";
                Status = "Игра окончена. Вы проиграли.";
            }
            
            GameResultVisible = true;
        }
        else if (string.Equals(type, "ReturnToPlacement", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("[GameViewModel] ReturnToPlacement message received");
            ReturnToPlacementRequested?.Invoke(Room);
        }
        else if (string.Equals(type, "RoomClosed", StringComparison.OrdinalIgnoreCase))
        {
            Status = "Комната закрыта.";
            ReturnToMainMenuRequested?.Invoke();
        }
        else if (string.Equals(type, "OpponentLeft", StringComparison.OrdinalIgnoreCase))
        {
            // Вышел НЕ создатель комнаты — комната остаётся, возвращаемся в неё (не в лобби).
            Status = "Соперник вышел из игры. Возвращаемся в комнату...";
            ReturnToPlacementRequested?.Invoke(Room);
        }
        else if (string.Equals(type, "turn_timeout", StringComparison.OrdinalIgnoreCase))
        {
            IsMyTurn = false;
            Status = "Время вышло. Ход соперника.";
        }
        else if (string.Equals(type, "Chat", StringComparison.OrdinalIgnoreCase))
        {
            var sender = GetString(payload, "senderName");
            var text = GetString(payload, "text");
            if (!string.IsNullOrEmpty(text))
                ChatMessages.Add(new ChatMessageItem { Sender = sender, Text = text, IsMine = false });
        }
        else if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
        {
            var msg = GetString(payload, "message");
            if (string.Equals(msg, "Not your turn", StringComparison.OrdinalIgnoreCase))
            {
                IsMyTurn = false;
                Status = "Сейчас ход соперника.";
                return;
            }
            Status = "Ошибка: " + (msg ?? "unknown");
        }
    }

    private static int GetInt(JsonElement e, string name)
    {
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.TryGetInt32(out var i))
                return i;
        return 0;
    }

    private static string GetString(JsonElement e, string name)
    {
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value.GetString() ?? "";
        return "";
    }

    private static ShotState ParseShotState(string s)
    {
        if (string.IsNullOrEmpty(s)) return ShotState.None;
        return s.ToLowerInvariant() switch
        {
            "miss" => ShotState.Miss,
            "hit" => ShotState.Hit,
            "sunk" => ShotState.Sunk,
            _ => ShotState.None
        };
    }

    private GameCellViewModel? GetMyCell(int x, int y)
    {
        foreach (var c in MyBoardCells)
            if (c.X == x && c.Y == y) return c;
        return null;
    }

    private GameCellViewModel? GetEnemyCell(int x, int y)
    {
        foreach (var c in EnemyBoardCells)
            if (c.X == x && c.Y == y) return c;
        return null;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
