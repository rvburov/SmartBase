namespace SmartBase.Core.Models;

/// <summary>Клавиша, двигающая выделение ячеек. Shift к стрелкам и прочим клавишам передается
/// отдельно (<c>extend</c>), а Shift+Tab — своя клавиша: Tab выделение не растягивает никогда.</summary>
public enum CellNavKey
{
    Up,
    Down,
    Left,
    Right,
    Home,
    End,
    PageUp,
    PageDown,
    CtrlHome,
    CtrlEnd,
    CtrlUp,
    CtrlDown,
    CtrlLeft,
    CtrlRight,
    Tab,
    ShiftTab,
}

/// <summary>
/// Клавиатурное движение прямоугольника ячеек — чистая арифметика без сетки, поэтому проверяется
/// тестами целиком, а таблица только переводит клавишу в <see cref="CellNavKey"/> и показывает
/// результат.
///
/// Активная ячейка — ЯКОРЬ (куда кликнули). Без Shift клавиша двигает якорь и схлопывает
/// выделение в одну ячейку; с Shift двигается ФОКУС — дальний угол, а якорь стоит. Так Shift+↓
/// после Shift+→ растягивает тот же прямоугольник, а не начинает новый от угла.
///
/// Столбцы — не сплошной диапазон: между видимыми данными живут служебные «№» и «+» и скрытые
/// в «Отображении» поля. Поэтому движение по столбцам идет по списку <c>dataColumns</c>
/// (позиции видимых столбцов с данными по возрастанию), и скрытый столбец перешагивается, а не
/// «съедает» нажатие.
///
/// Все считается за O(число столбцов) и не зависит от числа строк: на коллекции в 10 млн записей
/// PageDown и Ctrl+End стоят столько же, сколько на десяти.
/// </summary>
public static class CellRangeNavigation
{
    /// <summary>
    /// Куда переедет выделение от нажатия <paramref name="key"/>.
    /// </summary>
    /// <param name="range">Текущее выделение. Строки за краем прижимаются к таблице, столбец не из
    /// <paramref name="dataColumns"/> (поле скрыли, пока выделение жило) — к ближайшему видимому.</param>
    /// <param name="extend">Нажат Shift: двигать фокус и оставить якорь. Для Tab и Shift+Tab не
    /// действует — они всегда схлопывают выделение.</param>
    /// <param name="rowCount">Сколько строк с данными в показанной таблице.</param>
    /// <param name="dataColumns">Позиции видимых столбцов с данными в порядке показа.</param>
    /// <param name="pageRows">На сколько строк листают PageUp/PageDown (обычно — сколько влезает
    /// на экран); меньше единицы считается единицей.</param>
    /// <returns>Новое выделение; null — таблица пуста или в ней нет ни одного видимого столбца.</returns>
    public static CellRange? Navigate(
        CellRange range,
        CellNavKey key,
        bool extend,
        int rowCount,
        IReadOnlyList<int> dataColumns,
        int pageRows)
    {
        if (rowCount <= 0 || dataColumns is null || dataColumns.Count == 0) return null;

        var lastRow = rowCount - 1;
        var lastCol = dataColumns.Count - 1;
        var page    = Math.Max(1, pageRows);

        // Дальше столбцы — это индексы В СПИСКЕ видимых, а не позиции в сетке: шаг «вправо» —
        // следующий видимый столбец, сколько бы скрытых ни стояло между ними.
        var anchorRow = Math.Clamp(range.AnchorRow, 0, lastRow);
        var anchorCol = NearestIndex(dataColumns, range.AnchorColumn);
        var focusRow  = Math.Clamp(range.FocusRow, 0, lastRow);
        var focusCol  = NearestIndex(dataColumns, range.FocusColumn);

        if (key is CellNavKey.Tab or CellNavKey.ShiftTab)
        {
            // Tab ходит от активной ячейки, как по строке текста: в конце строки — на первый
            // столбец следующей, в начале — на последний предыдущей. У краев таблицы стоит.
            var (row, col) = key == CellNavKey.Tab
                ? Next(anchorRow, anchorCol, lastRow, lastCol)
                : Previous(anchorRow, anchorCol, lastCol);
            return CellRange.Single(row, dataColumns[col]);
        }

        // Движется фокус (Shift) или сам якорь (без Shift) — от той ячейки, что движется.
        var fromRow = extend ? focusRow : anchorRow;
        var fromCol = extend ? focusCol : anchorCol;

        var (toRow, toCol) = key switch
        {
            CellNavKey.Up        => (fromRow - 1,    fromCol),
            CellNavKey.Down      => (fromRow + 1,    fromCol),
            CellNavKey.Left      => (fromRow,        fromCol - 1),
            CellNavKey.Right     => (fromRow,        fromCol + 1),
            CellNavKey.Home      => (fromRow,        0),
            CellNavKey.End       => (fromRow,        lastCol),
            // Сложение в long: PageDown от последней из 2 млрд строк не должен переполнить int
            // и прыгнуть в минус.
            CellNavKey.PageUp    => ((int)Math.Max(0L,      (long)fromRow - page), fromCol),
            CellNavKey.PageDown  => ((int)Math.Min(lastRow, (long)fromRow + page), fromCol),
            CellNavKey.CtrlHome  => (0,              0),
            CellNavKey.CtrlEnd   => (lastRow,        lastCol),
            CellNavKey.CtrlUp    => (0,              fromCol),
            CellNavKey.CtrlDown  => (lastRow,        fromCol),
            CellNavKey.CtrlLeft  => (fromRow,        0),
            CellNavKey.CtrlRight => (fromRow,        lastCol),
            _                    => (fromRow,        fromCol),
        };

        toRow = Math.Clamp(toRow, 0, lastRow);
        toCol = Math.Clamp(toCol, 0, lastCol);

        return extend
            ? new CellRange(anchorRow, dataColumns[anchorCol], toRow, dataColumns[toCol])
            : CellRange.Single(toRow, dataColumns[toCol]);
    }

    /// <summary>Ctrl+A: вся таблица от левого верхнего угла до правого нижнего. Якорь — в левом
    /// верхнем углу, чтобы следующий Shift+стрелка сжимал выделение от правого нижнего, как
    /// после протягивания мышью сверху вниз. null — выделять нечего.</summary>
    public static CellRange? SelectAll(int rowCount, IReadOnlyList<int> dataColumns)
    {
        if (rowCount <= 0 || dataColumns is null || dataColumns.Count == 0) return null;
        return new CellRange(0, dataColumns[0], rowCount - 1, dataColumns[^1]);
    }

    private static (int Row, int Col) Next(int row, int col, int lastRow, int lastCol)
    {
        if (col < lastCol) return (row, col + 1);
        return row < lastRow ? (row + 1, 0) : (row, col);
    }

    private static (int Row, int Col) Previous(int row, int col, int lastCol)
    {
        if (col > 0) return (row, col - 1);
        return row > 0 ? (row - 1, lastCol) : (row, col);
    }

    /// <summary>Индекс в <paramref name="columns"/> столбца, ближайшего к позиции
    /// <paramref name="position"/>. При равном расстоянии берется левый: так Shift+→ от скрытого
    /// столбца доходит до правого соседа за одно нажатие, а не перепрыгивает его.</summary>
    private static int NearestIndex(IReadOnlyList<int> columns, int position)
    {
        var best     = 0;
        var bestDist = long.MaxValue;
        for (var i = 0; i < columns.Count; i++)
        {
            var dist = Math.Abs((long)columns[i] - position);
            if (dist < bestDist) { best = i; bestDist = dist; }
            if (dist == 0) break;
        }
        return best;
    }
}
