namespace battle_of_sea.Game
{
    public enum CellState
    {
        Empty,
        Ship,
        Hit,
        Miss
    }
    public enum ShotResult
    {
        Miss,
        Hit,
        Sunk,
        AlreadyShot
    }
    
    public class Board
    {
        
        public const int Size = 10;
        public CellState[,] Cells { get; private set; } = new CellState[Size, Size];
        public List<Ship> Ships { get; } = new();

        public Board() { }

        public void Clear()
        {
            Cells = new CellState[Size, Size];
            Ships.Clear();
        }

        public void Reset()
        {
            Clear();
        }

        public bool PlaceShip(int x, int y, int size, bool horizontal)
        {
            var cells = new List<(int x, int y)>();


            for (int i = 0; i < size; i++)
            {
                int cx = horizontal ? x + i : x;
                int cy = horizontal ? y : y + i;

                if (cx < 0 || cy < 0 || cx >= Size || cy >= Size)
                    return false;

                if (Cells[cx, cy] != CellState.Empty)
                    return false;

                cells.Add((cx, cy));
            }

            var ship = new Ship();
            foreach (var c in cells)
            {
                Cells[c.x, c.y] = CellState.Ship;
                ship.Cells.Add(c);
            }

            Ships.Add(ship);
            return true;
        }

        public ShotResult Shoot(int x, int y)
        {
            if (Cells[x, y] == CellState.Hit || Cells[x, y] == CellState.Miss)
                return ShotResult.AlreadyShot;

            if (Cells[x, y] == CellState.Ship)
            {
                Cells[x, y] = CellState.Hit;

                var ship = Ships.First(s => s.Contains(x, y));
                ship.RegisterHit(x, y);

                return ship.IsSunk
                    ? ShotResult.Sunk
                    : ShotResult.Hit;
            }

            Cells[x, y] = CellState.Miss;
            return ShotResult.Miss;
        }

        public bool IsDefeated() => Ships.Count > 0 && Ships.All(s => s.IsSunk);

        /// <summary>Возвращает координаты всех клеток, занятых кораблями (для отправки клиенту в GameStart).</summary>
        public List<(int x, int y)> GetShipCoordinates()
        {
            var list = new List<(int x, int y)>();
            foreach (var ship in Ships)
                foreach (var c in ship.Cells)
                    list.Add(c);
            return list;
        }
    }
}
