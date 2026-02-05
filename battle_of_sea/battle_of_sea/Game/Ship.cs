namespace battle_of_sea.Game
{
    public class Ship
    {
        public List<(int x, int y)> Cells { get; } = new();
        public HashSet<(int x, int y)> Hits { get; } = new();

        public bool IsSunk => Hits.Count == Cells.Count;

        public bool Contains(int x, int y)
            => Cells.Contains((x, y));

        public void RegisterHit(int x, int y)
        {
            Hits.Add((x, y));
        }
    }
}
