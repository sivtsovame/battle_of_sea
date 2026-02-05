using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using client.ViewModels;

namespace client.Converters;

public class GameCellToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not GameCellViewModel cell)
            return Brushes.Transparent;

        // Приоритет: выстрелы > корабли > пусто
        if (cell.ShotState == ShotState.Sunk) return Brushes.DarkRed;
        if (cell.ShotState == ShotState.Hit) return Brushes.Red;
        if (cell.ShotState == ShotState.Miss) return Brushes.LightGray;
        if (cell.HasShip) return Brushes.SteelBlue;
        return Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
