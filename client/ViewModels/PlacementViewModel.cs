using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Threading.Tasks;
using System.Text.Json;
using client.Models;
using client.Services;
using client.Utils;

namespace client.ViewModels;

/// <summary>Сигнатура события начала игры: первый ход, корабли с сервера (если есть), время старта хода.</summary>
public delegate void GameStartedHandler(bool isYourTurn, IReadOnlyList<(int x, int y)>? myShips, long turnStartedAtUtcMs);

public class PlacementViewModel : INotifyPropertyChanged
{
    private readonly GameServerClient _client;
    private string _displayName = "Player";
    private string _status = "Расставьте корабли на своём поле";
    private bool _isHorizontal = true;
    private int _shipsToPlace4 = 1;
    private int _shipsToPlace3 = 2;
    private int _shipsToPlace2 = 3;
    private int _shipsToPlace1 = 4;
    private bool _allShipsPlaced;
    private bool _isReady;

    public RoomInfo Room { get; }
    public string DisplayName => _displayName;

    public ObservableCollection<CellViewModel> Cells { get; } = new();

    public bool IsHorizontal
    {
        get => _isHorizontal;
        set { _isHorizontal = value; OnPropertyChanged(); }
    }

    public int ShipsToPlace4
    {
        get => _shipsToPlace4;
        set { _shipsToPlace4 = value; OnPropertyChanged(); }
    }

    public int ShipsToPlace3
    {
        get => _shipsToPlace3;
        set { _shipsToPlace3 = value; OnPropertyChanged(); }
    }

    public int ShipsToPlace2
    {
        get => _shipsToPlace2;
        set { _shipsToPlace2 = value; OnPropertyChanged(); }
    }

    public int ShipsToPlace1
    {
        get => _shipsToPlace1;
        set { _shipsToPlace1 = value; OnPropertyChanged(); }
    }

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public ICommand BackToMenuCommand { get; }
    public ICommand ToggleOrientationCommand { get; }
    public ICommand CellClickCommand { get; }
    public ICommand ReadyCommand { get; }
    public ICommand SendChatCommand { get; }

    public bool CanPressReady => _allShipsPlaced && !_isReady;

    /// <summary>Чат доступен только когда в комнате два игрока.</summary>
    private bool _canUseChat;
    public bool CanUseChat
    {
        get => _canUseChat;
        set { _canUseChat = value; OnPropertyChanged(); (SendChatCommand as RelayCommand)?.RaiseCanExecuteChanged(); }
    }

    public ObservableCollection<ChatMessageItem> ChatMessages { get; } = new();
    private string _chatText = "";
    public string ChatText
    {
        get => _chatText;
        set { _chatText = value ?? ""; OnPropertyChanged(); (SendChatCommand as RelayCommand)?.RaiseCanExecuteChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event System.Action? BackRequested;
    /// <param name="isYourTurn">Чей первый ход.</param>
    /// <param name="myShips">Координаты кораблей с сервера (если есть) — приоритет над локальными.</param>
    public event GameStartedHandler? GameStarted;

    public PlacementViewModel(GameServerClient client, RoomInfo room, string displayName = "Player")
    {
        _client = client;
        Room = room;
        _displayName = displayName;
        Room.MyPlayerName = displayName;
        CanUseChat = Room.Players >= 2;

        // инициализируем поле 10x10
        for (var y = 0; y < 10; y++)
        {
            for (var x = 0; x < 10; x++)
            {
                Cells.Add(new CellViewModel(x, y));
            }
        }

        BackToMenuCommand = new RelayCommand(async _ =>
        {
            // уведомим сервер, что выходим из комнаты
            await _client.SendAsync("leaveroom", new { });
            BackRequested?.Invoke();
        });

        ToggleOrientationCommand = new RelayCommand(_ =>
        {
            IsHorizontal = !IsHorizontal;
        });

        CellClickCommand = new RelayCommand(param =>
        {
            if (param is CellViewModel cell)
            {
                TryPlaceShipAt(cell.X, cell.Y);
            }
        });

        // Присваиваем ссылку на команду каждой ячейке, чтобы биндинг в XAML был простым
        foreach (var cell in Cells)
        {
            cell.CellClickCommand = CellClickCommand;
        }

        ReadyCommand = new RelayCommand(async _ => await SendReadyAsync(), _ => CanPressReady);

        SendChatCommand = new RelayCommand(async _ => await SendChatAsync(), _ => CanUseChat && !string.IsNullOrWhiteSpace(ChatText));

        _client.MessageReceived += OnServerMessage;
    }

    private async Task SendChatAsync()
    {
        var text = (ChatText ?? "").Trim();
        if (string.IsNullOrEmpty(text)) return;
        ChatMessages.Add(new ChatMessageItem { Sender = _displayName, Text = text, IsMine = true });
        ChatText = "";
        OnPropertyChanged(nameof(ChatText));
        await _client.SendAsync("chat", new { text });
    }

    /// <summary>Отписаться от сообщений сервера (вызывать при переходе в игру, чтобы не дублировать обработку GameStart).</summary>
    public void DetachFromClient()
    {
        _client.MessageReceived -= OnServerMessage;
    }

    private void OnServerMessage(string type, JsonElement payload)
    {
        if (string.Equals(type, "GameStart", StringComparison.OrdinalIgnoreCase))
        {
            var isYourTurn = payload.TryGetProperty("isYourTurn", out var turnProp) && turnProp.GetBoolean();
            Status = "Бой начался!";
            IReadOnlyList<(int x, int y)>? myShipsFromServer = null;
            if (payload.TryGetProperty("myShips", out var shipsElem) && shipsElem.ValueKind == JsonValueKind.Array)
            {
                var list = new List<(int x, int y)>();
                foreach (var s in shipsElem.EnumerateArray())
                {
                    var x = GetInt(s, "x");
                    var y = GetInt(s, "y");
                    list.Add((x, y));
                }
                if (list.Count > 0)
                    myShipsFromServer = list;
            }
            var turnStartedAt = payload.TryGetProperty("turnStartedAt", out var ts) ? ts.GetInt64() : 0L;
            GameStarted?.Invoke(isYourTurn, myShipsFromServer, turnStartedAt);
        }
        else if (string.Equals(type, "RoomClosed", StringComparison.OrdinalIgnoreCase))
        {
            Status = "Комната закрыта создателем.";
            BackRequested?.Invoke();
        }
        else if (string.Equals(type, "OpponentLeft", StringComparison.OrdinalIgnoreCase))
        {
            Status = "Соперник вышел из комнаты. Ожидаем нового игрока...";
            ChatMessages.Clear();
            CanUseChat = false;
        }
        else if (string.Equals(type, "OpponentJoined", StringComparison.OrdinalIgnoreCase))
        {
            var roomName = GetString(payload, "roomName");
            if (!string.IsNullOrEmpty(roomName))
            {
                Room.Name = roomName;
                OnPropertyChanged(nameof(Room));
            }
            CanUseChat = true;
            if (_isReady)
                Status = "Вы готовы. Ожидаем соперника...";
            else
                Status = "Соперник в комнате. Расставьте корабли и нажмите «Готов к бою».";
        }
        else if (string.Equals(type, "Chat", StringComparison.OrdinalIgnoreCase))
        {
            var sender = GetString(payload, "senderName");
            var text = GetString(payload, "text");
            if (!string.IsNullOrEmpty(text))
                ChatMessages.Add(new ChatMessageItem { Sender = sender, Text = text, IsMine = false });
        }
    }

    private static string GetString(JsonElement e, string name)
    {
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value.GetString() ?? "";
        return "";
    }

    private static int GetInt(JsonElement e, string name)
    {
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.TryGetInt32(out var i))
                return i;
        return 0;
    }

    private void TryPlaceShipAt(int x, int y)
    {
        var size = GetCurrentShipSize();
        if (size == 0)
            return;

        // проверяем, можем ли разместить корабль
        var cellsToOccupy = new List<CellViewModel>();
        for (int i = 0; i < size; i++)
        {
            var cx = _isHorizontal ? x + i : x;
            var cy = _isHorizontal ? y : y + i;

            if (cx < 0 || cx >= 10 || cy < 0 || cy >= 10)
                return;

            var cell = GetCell(cx, cy);
            if (cell == null || cell.HasShip)
                return;

            // проверяем соседей (включая диагональные) – не должно быть других кораблей
            for (int ny = cy - 1; ny <= cy + 1; ny++)
            {
                for (int nx = cx - 1; nx <= cx + 1; nx++)
                {
                    if (nx < 0 || nx >= 10 || ny < 0 || ny >= 10)
                        continue;
                    var neighbor = GetCell(nx, ny);
                    if (neighbor != null && neighbor.HasShip)
                        return;
                }
            }

            cellsToOccupy.Add(cell);
        }

        // размещаем
        foreach (var c in cellsToOccupy)
        {
            c.HasShip = true;
        }

        DecrementShipCounter(size);

        if (ShipsToPlace1 == 0 && ShipsToPlace2 == 0 && ShipsToPlace3 == 0 && ShipsToPlace4 == 0)
        {
            _allShipsPlaced = true;
            Status = "Все корабли расставлены. Нажмите 'Готов к бою'.";
            OnPropertyChanged(nameof(CanPressReady));
            if (ReadyCommand is client.Utils.RelayCommand rc)
                rc.RaiseCanExecuteChanged();
        }
    }

    private int GetCurrentShipSize()
    {
        if (ShipsToPlace4 > 0) return 4;
        if (ShipsToPlace3 > 0) return 3;
        if (ShipsToPlace2 > 0) return 2;
        if (ShipsToPlace1 > 0) return 1;
        return 0;
    }

    private void DecrementShipCounter(int size)
    {
        switch (size)
        {
            case 4: ShipsToPlace4--; break;
            case 3: ShipsToPlace3--; break;
            case 2: ShipsToPlace2--; break;
            case 1: ShipsToPlace1--; break;
        }
        OnPropertyChanged(nameof(CanPressReady));
        if (ReadyCommand is client.Utils.RelayCommand rc)
            rc.RaiseCanExecuteChanged();
    }

    private CellViewModel? GetCell(int x, int y)
    {
        foreach (var c in Cells)
        {
            if (c.X == x && c.Y == y)
                return c;
        }
        return null;
    }

    /// <summary>Список кораблей для отправки на сервер: x, y, size, horizontal.</summary>
    public IReadOnlyList<(int x, int y, int size, bool horizontal)> GetShips()
    {
        var set = new HashSet<(int x, int y)>();
        foreach (var c in Cells)
            if (c.HasShip) set.Add((c.X, c.Y));

        var list = new List<(int x, int y, int size, bool horizontal)>();
        while (set.Count > 0)
        {
            var p = set.First();
            bool horizontal;
            int size;
            if (set.Contains((p.x + 1, p.y)))
            {
                horizontal = true;
                size = 0;
                for (int xx = p.x; xx < 10 && set.Contains((xx, p.y)); xx++) { size++; set.Remove((xx, p.y)); }
            }
            else
            {
                horizontal = false;
                size = 0;
                for (int yy = p.y; yy < 10 && set.Contains((p.x, yy)); yy++) { size++; set.Remove((p.x, yy)); }
            }
            list.Add((p.x, p.y, size, horizontal));
        }
        return list;
    }

    public IReadOnlyList<(int x, int y)> GetShipCoordinates()
    {
        var list = new List<(int x, int y)>();
        foreach (var c in Cells)
            if (c.HasShip) list.Add((c.X, c.Y));
        return list;
    }

    private async Task SendReadyAsync()
    {
        if (!_allShipsPlaced || _isReady)
            return;

        _isReady = true;
        Status = "Вы готовы. Ожидаем соперника...";
        OnPropertyChanged(nameof(CanPressReady));

        var ships = GetShips().Select(s => new { x = s.x, y = s.y, size = s.size, horizontal = s.horizontal }).ToList();
        await _client.SendAsync("shipplacement", new { roomId = Room.Id, ships });
        await _client.SendAsync("playerready", new { roomId = Room.Id });
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public class CellViewModel : INotifyPropertyChanged
{
    private bool _hasShip;

    public int X { get; }
    public int Y { get; }

    public bool HasShip
    {
        get => _hasShip;
        set { _hasShip = value; OnPropertyChanged(); }
    }

    // Команда будет задаваться из PlacementViewModel
    public ICommand? CellClickCommand { get; set; }

    public CellViewModel(int x, int y)
    {
        X = x;
        Y = y;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

