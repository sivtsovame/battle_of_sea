using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using client.Models;
using client.Services;

namespace client.ViewModels;

public class MainWindowViewModel : INotifyPropertyChanged
{
    private object? _currentViewModel;

    public object? CurrentViewModel
    {
        get => _currentViewModel;
        set { _currentViewModel = value; OnPropertyChanged(); }
    }

    public GameServerClient GameClient { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindowViewModel(GameServerClient client)
    {
        GameClient = client;

        var mainMenu = new MainMenuViewModel(client);
        mainMenu.RoomJoined += OnRoomJoined;
        CurrentViewModel = mainMenu;
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

