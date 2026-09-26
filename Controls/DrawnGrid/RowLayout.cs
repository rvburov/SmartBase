namespace SmartBase.Controls.DrawnGrid;

/// <summary>
/// Раскладка строк рисованной сетки: где у строки с данным индексом верхняя граница и какой у
/// нее шаг (высота плюс линия под ней). Стандартный режим — все строки одного шага, положение
/// это умножение. Ручные высоты (<c>ui_row_heights</c>) — разреженные: своя высота есть у
/// десятка строк из миллионов, поэтому хранятся только отклонения от стандартного шага, а
/// положение строки — «индекс × шаг плюс сумма отклонений строк выше». Отклонения лежат в
/// отсортированном массиве с префиксными суммами, поиск двоичный: 10 млн строк с десятком ручных
/// высот стоят O(log n) на строку и на попадание по координате.
///
/// Индексы — позиции в текущем срезе (порядок таблицы, сортировка или список id), а не id
/// записей: перевод id → индекс делает <c>RowRanks</c> в фоне, раскладка про записи не знает.
/// Чистая арифметика без контролов, тесты — <c>Tests/Unit/RowLayoutTests</c>.
/// </summary>
public sealed class RowLayout
{
    private readonly int[]    _index;   // индексы строк с ручной высотой, по возрастанию
    private readonly double[] _pitch;   // их шаг (высота + линия)
    private readonly double[] _prefix;  // _prefix[k] — сумма отклонений первых k ручных строк

    private RowLayout(double pitch, int[] index, double[] pitches)
    {
        Pitch   = pitch;
        _index  = index;
        _pitch  = pitches;
        _prefix = new double[index.Length + 1];
        for (var i = 0; i < index.Length; i++) _prefix[i + 1] = _prefix[i] + (pitches[i] - pitch);
    }

    /// <summary>Стандартный шаг строки (высота плюс линия).</summary>
    public double Pitch { get; }

    /// <summary>Есть ли строки с ручной высотой.</summary>
    public bool HasManual => _index.Length > 0;

    /// <summary>Все строки одного шага.</summary>
    public static RowLayout Uniform(double pitch) => new(Math.Max(1, pitch), [], []);

    /// <summary>
    /// Раскладка с ручными высотами. <paramref name="manual"/> — индекс строки → ее высота
    /// (без линии); строки ниже стандартной высоты и отрицательные индексы не учитываются,
    /// повторы индекса берут последнее значение.
    /// </summary>
    public static RowLayout Build(double pitch, double lineWidth, IEnumerable<KeyValuePair<int, double>> manual)
    {
        pitch = Math.Max(1, pitch);
        var map = new SortedDictionary<int, double>();
        foreach (var (index, height) in manual)
        {
            if (index < 0) continue;
            var p = height + lineWidth;
            if (p > pitch + 1e-6) map[index] = p;
        }
        if (map.Count == 0) return Uniform(pitch);
        return new RowLayout(pitch, map.Keys.ToArray(), map.Values.ToArray());
    }

    /// <summary>Шаг строки с индексом <paramref name="index"/> (высота плюс линия).</summary>
    public double Height(int index)
    {
        var k = Array.BinarySearch(_index, index);
        return k >= 0 ? _pitch[k] : Pitch;
    }

    /// <summary>Верхняя граница строки в координатах содержимого (без прокрутки).</summary>
    public double Top(int index)
    {
        if (index <= 0) return 0;
        // Сколько ручных строк лежит строго выше index.
        var k = Array.BinarySearch(_index, index);
        var above = k >= 0 ? k : ~k;
        return index * Pitch + _prefix[above];
    }

    /// <summary>Нижняя граница строки.</summary>
    public double Bottom(int index) => Top(index) + Height(index);

    /// <summary>Общая высота <paramref name="count"/> строк — протяженность прокрутки.</summary>
    public double TotalHeight(int count) => count <= 0 ? 0 : Top(count);

    /// <summary>
    /// Индекс строки, содержащей координату <paramref name="y"/> (в координатах содержимого);
    /// −1 вне строк. Двоичный поиск по индексу: положение монотонно.
    /// </summary>
    public int IndexAt(double y, int count)
    {
        if (count <= 0 || y < 0 || y >= TotalHeight(count)) return -1;
        if (!HasManual) return Math.Min(count - 1, (int)Math.Floor(y / Pitch));

        var lo = 0;
        var hi = count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (Top(mid) <= y) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>Строки, хотя бы частично попавшие в окно высотой <paramref name="viewport"/> при
    /// смещении <paramref name="offsetY"/>. Пустой диапазон — <c>Last &lt; First</c>.</summary>
    public (int First, int Last) VisibleRange(double offsetY, double viewport, int count)
    {
        if (count <= 0 || viewport <= 0) return (0, -1);
        var total = TotalHeight(count);
        var first = IndexAt(Math.Clamp(offsetY, 0, Math.Max(0, total - 1e-6)), count);
        if (first < 0) first = 0;
        var bottom = offsetY + viewport - 1e-6;
        var last   = bottom >= total ? count - 1 : IndexAt(Math.Max(0, bottom), count);
        if (last < first) last = first;
        return (first, Math.Min(last, count - 1));
    }
}
