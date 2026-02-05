using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using client.Models;
using client.Services;
using client.Utils;

namespace client.ViewModels;

public class MainMenuViewModel : INotifyPropertyChanged
{
    private readonly GameServerClient _client;

    private string _status = "Не подключено";
    private string _roomName = "Комната";
    private RoomInfo? _selectedRoom;

    public ObservableCollection<RoomInfo> Rooms { get; } = new();

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public string RoomName
    {
        get => _roomName;
        set { _roomName = value; OnPropertyChanged(); }
    }

    public RoomInfo? SelectedRoom
    {
        get => _selectedRoom;
        set { _selectedRoom = value; OnPropertyChanged(); ((RelayCommand)JoinRoomCommand).RaiseCanExecuteChanged(); }
    }

    public ICommand ConnectCommand { get; }
    public ICommand RefreshRoomsCommand { get; }
    public ICommand CreateRoomCommand { get; }
    public ICommand JoinRoomCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action<RoomInfo>? RoomJoined;

    public MainMenuViewModel(GameServerClient client)
    {
        _client = client;
        _client.MessageReceived += OnServerMessage;

        ConnectCommand = new RelayCommand(async _ => await EnsureConnectedAsync());
        RefreshRoomsCommand = new RelayCommand(async _ => await GetRoomsAsync(), _ => _client.IsConnected);
        CreateRoomCommand = new RelayCommand(async _ => await CreateRoomAsync(), _ => _client.IsConnected);
        JoinRoomCommand = new RelayCommand(async _ => await JoinSelectedRoomAsync(), _ => _client.IsConnected && SelectedRoom != null);

        if (_client.IsConnected)
        {
            Status = "Подключено";
            UpdateCommandStates();
        }
    }

    private async Task EnsureConnectedAsync()
    {
        if (_client.IsConnected)
            return;

        Status = "Подключаюсь...";
        try
        {
            var displayName = "Player_" + Guid.NewGuid().ToString().Substring(0, 8);
            await _client.ConnectAsync("ws://127.0.0.1:5556/", displayName);
            Status = "Подключено";
            UpdateCommandStates();
            await GetRoomsAsync();
        }
        catch (Exception ex)
        {
            Status = "Ошибка подключения: " + ex.Message;
        }
    }

    private void UpdateCommandStates()
    {
        if (RefreshRoomsCommand is RelayCommand r1)
            r1.RaiseCanExecuteChanged();
        if (CreateRoomCommand is RelayCommand r2)
            r2.RaiseCanExecuteChanged();
        if (JoinRoomCommand is RelayCommand r3)
            r3.RaiseCanExecuteChanged();
    }

    private async Task GetRoomsAsync()
    {
        await _client.SendAsync("getrooms", new { });
    }

    private async Task CreateRoomAsync()
    {
        var name = string.IsNullOrWhiteSpace(RoomName) ? "Комната" : RoomName.Trim();
        await _client.SendAsync("createroom", new { roomName = name, maxPlayers = 2 });
    }

    private async Task JoinSelectedRoomAsync()
    {
        if (SelectedRoom == null)
            return;

        await _client.SendAsync("joinroom", new { roomId = SelectedRoom.Id });
    }

    private void OnServerMessage(string type, JsonElement payload)
    {
        switch (type.ToLowerInvariant())
        {
            case "connected":
                Status = "Подключено к серверу";
                break;

            case "roomslist":
                Rooms.Clear();
                if (TryGetPropertyCaseInsensitive(payload, "rooms", out var roomsElem) && roomsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in roomsElem.EnumerateArray())
                    {
                        Rooms.Add(new RoomInfo
                        {
                            Id = GetStringCaseInsensitive(r, "id"),
                            Name = GetStringCaseInsensitive(r, "name"),
                            MaxPlayers = GetIntCaseInsensitive(r, "maxPlayers"),
                            Players = GetIntCaseInsensitive(r, "players")
                        });
                    }
                }
                break;

            case "roomcreated":
                Status = "Комната создана";
                // payload.room содержит данные комнаты
                if (TryGetPropertyCaseInsensitive(payload, "room", out var roomElem))
                {
                    var room = new RoomInfo
                    {
                        Id = GetStringCaseInsensitive(roomElem, "id"),
                        Name = GetStringCaseInsensitive(roomElem, "name"),
                        MaxPlayers = GetIntCaseInsensitive(roomElem, "maxPlayers"),
                        Players = GetIntCaseInsensitive(roomElem, "players")
                    };
                    RoomJoined?.Invoke(room);
                }
                break;

            case "joinroom":
                var success = payload.TryGetProperty("success", out var s) && s.GetBoolean();
                if (success && payload.TryGetProperty("roomId", out var roomIdProp))
                {
                    var id = roomIdProp.GetString() ?? string.Empty;
                    var room = new RoomInfo { Id = id, Name = "Комната", MaxPlayers = 2 };
                    RoomJoined?.Invoke(room);
                }
                break;

            case "error":
                if (TryGetPropertyCaseInsensitive(payload, "message", out var msg))
                    Status = "Ошибка: " + msg.GetString();
                break;
        }
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

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

    private static string GetStringCaseInsensitive(JsonElement element, string name)
        => TryGetPropertyCaseInsensitive(element, name, out var v) ? (v.GetString() ?? string.Empty) : string.Empty;

    private static int GetIntCaseInsensitive(JsonElement element, string name)
        => TryGetPropertyCaseInsensitive(element, name, out var v) && v.TryGetInt32(out var i) ? i : 0;
}

