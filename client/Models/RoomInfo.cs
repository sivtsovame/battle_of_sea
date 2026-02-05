namespace client.Models;

public class RoomInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int MaxPlayers { get; set; }
    public int Players { get; set; }
    public string MyPlayerName { get; set; } = string.Empty;
}

