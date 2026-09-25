using Avalonia;
using Avalonia.Input;
using SmartBase.ViewModels.ConstructorDb;

namespace SmartBase.Views.ConstructorDb.DrawnGrid;

/// <summary>Что лежит под точкой на поверхности сетки.</summary>
public enum GridHitKind
{
    None,
    /// <summary>Ячейка тела таблицы (в том числе «№»).</summary>
    Cell,
    /// <summary>Ячейка строки «+» (создание записи).</summary>
    AddRow,
    /// <summary>Заголовок колонки (кроме кнопки фильтра).</summary>
    Header,
    /// <summary>Кнопка фильтра и сортировки в заголовке.</summary>
    HeaderFilter,
    /// <summary>Заголовок «+» (создание свойства).</summary>
    HeaderAdd,
    /// <summary>Полоска-захват ширины у правой границы заголовка (ручной режим).</summary>
    ColumnGrip,
    /// <summary>Полоска-захват высоты у нижней границы строки (ручной режим).</summary>
    RowGrip,
}

/// <summary>
/// Результат попадания по поверхности: вид, строка и колонка (индексы в раскладке; −1, если не
/// применимо), определение колонки и прямоугольник элемента в координатах поверхности (для
/// якоря флайаута и меню). Считается арифметикой раскладок — двоичный поиск по обеим осям.
/// </summary>
public readonly record struct GridHit(GridHitKind Kind, int Row, int Column, TableColumnDef? Def, Rect Bounds)
{
    public static readonly GridHit None = new(GridHitKind.None, -1, -1, null, default);
}

/// <summary>Нажатие указателя на поверхности: что под ним и само событие (кнопка, модификаторы,
/// число кликов). Полоски-захваты сюда не попадают — жесты ширины и высоты поверхность ведет сама.</summary>
public sealed class GridPointerEventArgs(GridHit hit, PointerPressedEventArgs pointer) : EventArgs
{
    public GridHit                 Hit     { get; } = hit;
    public PointerPressedEventArgs Pointer { get; } = pointer;
}
