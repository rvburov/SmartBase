namespace SmartBase.Core.Models;

/// <summary>
/// Прямоугольный диапазон ячеек таблицы: от якоря (где нажали) до фокуса (куда дотянули или
/// куда доехали стрелками), как выделение в табличном редакторе. Координаты — индексы строки и колонки
/// В ТОМ ПОРЯДКЕ, В КАКОМ ОНИ ПОКАЗАНЫ на экране: строка = позиция в видимом списке, колонка =
/// DisplayIndex. Ни id записи, ни ключ поля сюда не попадают — прямоугольник ничего не знает
/// про базу, поэтому его можно считать и проверять без таблицы.
///
/// Якорь и фокус хранятся как есть, ненормализованными: тянуть можно вверх и влево, и при
/// продолжении выделения (Shift + клик, Shift + стрелка) двигается именно фокус, а якорь стоит.
/// Нормализация — это уже <see cref="Top"/>/<see cref="Left"/>/<see cref="Bottom"/>/<see cref="Right"/>.
/// </summary>
public readonly record struct CellRange(int AnchorRow, int AnchorColumn, int FocusRow, int FocusColumn)
{
    /// <summary>Диапазон из одной ячейки (обычный клик).</summary>
    public static CellRange Single(int row, int column) => new(row, column, row, column);

    public int Top    => Math.Min(AnchorRow, FocusRow);
    public int Bottom => Math.Max(AnchorRow, FocusRow);
    public int Left   => Math.Min(AnchorColumn, FocusColumn);
    public int Right  => Math.Max(AnchorColumn, FocusColumn);

    public int RowCount    => Bottom - Top + 1;
    public int ColumnCount => Right - Left + 1;

    /// <summary>Сколько ячеек накрыто. Нужно для порогов: подтверждение очистки, запись в журнал.</summary>
    public int CellCount => RowCount * ColumnCount;

    /// <summary>Одна ячейка — частый случай, для которого не нужны ни подтверждения, ни прогресс.</summary>
    public bool IsSingleCell => RowCount == 1 && ColumnCount == 1;

    /// <summary>Тот же якорь, новый фокус — растягивание Shift-кликом и стрелками.</summary>
    public CellRange WithFocus(int row, int column) => this with { FocusRow = row, FocusColumn = column };

    public bool Contains(int row, int column) =>
        row >= Top && row <= Bottom && column >= Left && column <= Right;

    /// <summary>
    /// Видимые столбцы диапазона: из <paramref name="visibleColumns"/> (позиции видимых столбцов с
    /// данными по возрастанию) — те, что между <see cref="Left"/> и <see cref="Right"/>. Ровно они
    /// копируются, заполняются вставкой и считаются в «Выделено: R × C»: скрытый в «Отображении»
    /// столбец внутри диапазона человек не видит, и трогать его нельзя.
    /// </summary>
    public List<int> VisibleColumns(IReadOnlyList<int> visibleColumns)
    {
        var inside = new List<int>();
        foreach (var column in visibleColumns)
            if (column >= Left && column <= Right) inside.Add(column);
        return inside;
    }

    /// <summary>Координаты всех ячеек диапазона, слева направо и сверху вниз — в том порядке,
    /// в каком они лягут в буфер обмена.</summary>
    public IEnumerable<(int Row, int Column)> Cells()
    {
        for (var r = Top; r <= Bottom; r++)
            for (var c = Left; c <= Right; c++)
                yield return (r, c);
    }
}
