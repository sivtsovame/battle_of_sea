using System;
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
        var placement = new PlacementViewModel(GameClient, room, GameClient.DisplayName);
        placement.BackRequested += () =>
        {
            // вернуться в главное меню
            var menu = new MainMenuViewModel(GameClient);
            menu.RoomJoined += OnRoomJoined;
            CurrentViewModel = menu;
        };
        placement.GameStarted += (isYourTurn, myShipsFromServer) =>
        {
            var myShips = myShipsFromServer ?? placement.GetShipCoordinates();
            placement.DetachFromClient();
            var gameVm = new GameViewModel(GameClient, room, isYourTurn, myShips);
            gameVm.ReturnToMainMenuRequested += () =>
            {
                var menu = new MainMenuViewModel(GameClient);
                menu.RoomJoined += OnRoomJoined;
                CurrentViewModel = menu;
            };
            gameVm.ReturnToPlacementRequested += returnRoom =>
            {
                var newPlacement = new PlacementViewModel(GameClient, returnRoom, GameClient.DisplayName);
                newPlacement.BackRequested += () =>
                {
                    var menu = new MainMenuViewModel(GameClient);
                    menu.RoomJoined += OnRoomJoined;
                    CurrentViewModel = menu;
                };
                newPlacement.GameStarted += (isYourTurnAgain, myShipsFromServerAgain) =>
                {
                    var myShipsAgain = myShipsFromServerAgain ?? newPlacement.GetShipCoordinates();
                    newPlacement.DetachFromClient();
                    var gameVmAgain = new GameViewModel(GameClient, returnRoom, isYourTurnAgain, myShipsAgain);
                    gameVmAgain.ReturnToMainMenuRequested += () =>
                    {
                        var m = new MainMenuViewModel(GameClient);
                        m.RoomJoined += OnRoomJoined;
                        CurrentViewModel = m;
                    };
                    CurrentViewModel = gameVmAgain;
                };
                CurrentViewModel = newPlacement;
            };
            CurrentViewModel = gameVm;
        };

        CurrentViewModel = placement;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

