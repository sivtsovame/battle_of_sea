namespace battle_of_sea.Game
{
    public class Player
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public object? Connection { get; set; } // Can be ClientConnection or WebSocketConnection
        public bool IsReady { get; set; } = false;
        public Board Board { get; set; } = new Board();
    }
}
