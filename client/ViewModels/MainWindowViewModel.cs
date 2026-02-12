using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using client.Models;
using client.Services;
using client.Utils;

namespace client.ViewModels;

public class MainWindowViewModel : INotifyPropertyChanged
{
    private object? _currentViewModel;
    private bool _serverDisconnectedVisible;
    private string _serverDisconnectedMessage = "Соединение с сервером потеряно.";
    private bool _serverReconnectAvailable;
    private bool _serverChecking;
    private CancellationTokenSource? _serverCheckCts;

    public object? CurrentViewModel
    {
        get => _currentViewModel;
        set { _currentViewModel = value; OnPropertyChanged(); }
    }

    public bool ServerDisconnectedVisible
    {
        get => _serverDisconnectedVisible;
        set { _serverDisconnectedVisible = value; OnPropertyChanged(); }
    }

    public string ServerDisconnectedMessage
    {
        get => _serverDisconnectedMessage;
        set { _serverDisconnectedMessage = value ?? ""; OnPropertyChanged(); }
    }

    public bool ServerReconnectAvailable
    {
        get => _serverReconnectAvailable;
        set { _serverReconnectAvailable = value; OnPropertyChanged(); }
    }

    public bool ServerChecking
    {
        get => _serverChecking;
        set { _serverChecking = value; OnPropertyChanged(); }
    }

    public ICommand CloseDisconnectCommand { get; }
    public ICommand ReconnectCommand { get; }

    public GameServerClient GameClient { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindowViewModel(GameServerClient client)
    {
        GameClient = client;

        CloseDisconnectCommand = new RelayCommand(_ =>
        {
            // Полный выход из игры при нажатии «Закрыть» в окне отключения сервера
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        });

        ReconnectCommand = new RelayCommand(async _ => await TryReconnectAsync(), _ => ServerReconnectAvailable);

        client.ServerDisconnected += OnServerDisconnected;
        client.ServerConnected += OnServerConnected;

        var mainMenu = new MainMenuViewModel(client);
        mainMenu.RoomJoined += OnRoomJoined;
        CurrentViewModel = mainMenu;
    }

    private void OnServerDisconnected()
    {
        ServerDisconnectedMessage = "Сервер отключился. Ожидаем, когда он снова заработает...";
        ServerDisconnectedVisible = true;
        ServerReconnectAvailable = false;
        StartServerAvailabilityLoop();
        (ReconnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void OnServerConnected()
    {
        // Если пользователь не успел нажать «Закрыть», а переподключение прошло —
        // уходим в лобби, потому что состояние комнаты/игры на сервере могло потеряться.
        ServerDisconnectedVisible = false;
        ServerReconnectAvailable = false;
        StopServerAvailabilityLoop();
        (ReconnectCommand as RelayCommand)?.RaiseCanExecuteChanged();

        // Отписываемся от старых обработчиков, чтобы не было "двойных" подписок после переподключения.
        if (CurrentViewModel is GameViewModel gameVm)
            gameVm.DetachFromClient();
        else if (CurrentViewModel is PlacementViewModel placementVm)
            placementVm.DetachFromClient();
        else if (CurrentViewModel is MainMenuViewModel menuVm)
            menuVm.DetachFromClient();

        var menu = new MainMenuViewModel(GameClient);
        menu.RoomJoined += OnRoomJoined;
        CurrentViewModel = menu;
    }

    private void StartServerAvailabilityLoop()
    {
        StopServerAvailabilityLoop();
        _serverCheckCts = new CancellationTokenSource();
        var token = _serverCheckCts.Token;

        ServerChecking = true;

        _ = Task.Run(async () =>
        {
            // Берём адрес из последнего подключения (если есть), иначе стандартный локальный.
            var uri = string.IsNullOrWhiteSpace(GameClient.LastUri) ? "ws://127.0.0.1:5556/" : GameClient.LastUri;
            string host = "127.0.0.1";
            int port = 5556;
            try
            {
                var u = new Uri(uri);
                host = string.IsNullOrWhiteSpace(u.Host) ? host : u.Host;
                port = u.Port > 0 ? u.Port : port;
            }
            catch { /* ignore */ }

            while (!token.IsCancellationRequested)
            {
                var ok = await GameServerClient.IsServerReachableAsync(host, port, timeoutMs: 700);
                if (ok)
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        ServerDisconnectedMessage = "Сервер снова работает. Нажмите «Подключиться».";
                        ServerReconnectAvailable = true;
                        ServerChecking = false;
                        (ReconnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    });
                    return;
                }

                try { await Task.Delay(1000, token); } catch { return; }
            }
        }, token);
    }

    private void StopServerAvailabilityLoop()
    {
        try { _serverCheckCts?.Cancel(); } catch { /* ignore */ }
        _serverCheckCts = null;
        ServerChecking = false;
    }

    private async Task TryReconnectAsync()
    {
        if (GameClient.IsConnected)
            return;

        ServerReconnectAvailable = false;
        ServerDisconnectedMessage = "Подключаюсь к серверу...";
        (ReconnectCommand as RelayCommand)?.RaiseCanExecuteChanged();

        try
        {
            var uri = string.IsNullOrWhiteSpace(GameClient.LastUri) ? "ws://127.0.0.1:5556/" : GameClient.LastUri;
            var name = string.IsNullOrWhiteSpace(GameClient.DisplayName) ? ("Player_" + Guid.NewGuid().ToString()[..8]) : GameClient.DisplayName;
            await GameClient.ConnectAsync(uri, name);
            // OnServerConnected скроет окно.
        }
        catch (Exception ex)
        {
            ServerDisconnectedMessage = "Не удалось подключиться: " + ex.Message;
            StartServerAvailabilityLoop();
        }
    }

    private void OnRoomJoined(RoomInfo room)
    {
        if (CurrentViewModel is MainMenuViewModel mainMenu)
            mainMenu.DetachFromClient();
        GoToPlacement(room);
    }

    /// <summary>Переход на экран расстановки. При «Начать новую игру» сервер шлёт ReturnToPlacement — тогда вызывается это (сколько угодно раз).</summary>
    private void GoToPlacement(RoomInfo room)
    {
        var placement = new PlacementViewModel(GameClient, room, GameClient.DisplayName);
        placement.BackRequested += () =>
        {
            var menu = new MainMenuViewModel(GameClient);
            menu.RoomJoined += OnRoomJoined;
            CurrentViewModel = menu;
        };
        placement.GameStarted += (isYourTurn, myShipsFromServer, turnStartedAtUtcMs) =>
        {
            var myShips = myShipsFromServer ?? placement.GetShipCoordinates();
            placement.DetachFromClient();
            GoToGame(room, isYourTurn, myShips, turnStartedAtUtcMs);
        };
        CurrentViewModel = placement;
    }

    /// <summary>Переход в игру. У каждого GameViewModel подписываем ReturnToPlacementRequested → снова GoToPlacement.</summary>
    private void GoToGame(RoomInfo room, bool isYourTurn, IReadOnlyList<(int x, int y)> myShips, long turnStartedAtUtcMs = 0)
    {
        var gameVm = new GameViewModel(GameClient, room, isYourTurn, myShips, turnStartedAtUtcMs);
        gameVm.ReturnToMainMenuRequested += () =>
        {
            gameVm.DetachFromClient();
            var menu = new MainMenuViewModel(GameClient);
            menu.RoomJoined += OnRoomJoined;
            CurrentViewModel = menu;
        };
        gameVm.ReturnToPlacementRequested += returnRoom =>
        {
            gameVm.DetachFromClient();
            GoToPlacement(returnRoom);
        };
        CurrentViewModel = gameVm;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

