using SmartBase.ViewModels.ConstructorDb;

namespace SmartBase.Views.ConstructorDb.DrawnGrid;

/// <summary>
/// Раскладка колонок рисованной сетки: какие колонки показаны, где у каждой левая граница и
/// какая ширина. Чистая арифметика без контролов — скрытая в «Отображении» колонка сюда не
/// попадает вовсе, у нее нет ни ширины, ни ячеек (это и есть настоящая «мера 6» дорожной карты).
/// Поиск колонки по координате и видимого диапазона по смещению прокрутки — двоичный, поэтому
/// 1000 колонок и 10 стоят одинаково.
///
/// Прототип этапа 0 плана [drawn-grid.md]: ширины считаются по тем же правилам, что у нынешней
/// таблицы (стандартная — поровну от ширины окна, не уже <c>standardWidth</c>; ручная — из модели,
/// остаток раздается пропорционально), но без звездных весов и без прохода по колонкам на каждый
/// кадр разметки.
/// </summary>
public sealed class ColumnLayout
{
    /// <summary>Пол ручной ширины — как <c>MinManualColWidth</c> нынешней таблицы.</summary>
    public const double MinManualWidth = 48;

    private readonly TableColumnDef[] _defs;
    private readonly double[]         _widths;
    private readonly double[]         _lefts;   // _lefts[i] — левая граница i-й колонки, _lefts[Count] — общая ширина

    public static readonly ColumnLayout Empty = new([], []);

    private ColumnLayout(TableColumnDef[] defs, double[] widths)
    {
        _defs   = defs;
        _widths = widths;
        _lefts  = new double[defs.Length + 1];
        for (var i = 0; i < defs.Length; i++) _lefts[i + 1] = _lefts[i] + widths[i];
    }

    public int Count => _defs.Length;

    public TableColumnDef this[int index] => _defs[index];

    public double Left(int index)  => _lefts[index];
    public double Right(int index) => _lefts[index + 1];
    public double Width(int index) => _widths[index];

    /// <summary>Общая ширина показанных колонок — протяженность горизонтальной прокрутки.</summary>
    public double TotalWidth => _lefts[^1];

    /// <summary>Индекс колонки под координатой <paramref name="x"/> (в координатах содержимого,
    /// без учета прокрутки); −1, если координата вне колонок.</summary>
    public int IndexAt(double x)
    {
        if (Count == 0 || x < 0 || x >= TotalWidth) return -1;
        // Ищем последнюю левую границу, не превышающую x.
        var lo = 0;
        var hi = Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (_lefts[mid] <= x) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>Диапазон колонок, хотя бы частично попавших в окно <paramref name="viewportWidth"/>
    /// при смещении <paramref name="offsetX"/>. Пустой диапазон — <c>Last &lt; First</c>.</summary>
    public (int First, int Last) VisibleRange(double offsetX, double viewportWidth)
    {
        if (Count == 0 || viewportWidth <= 0) return (0, -1);

        var first = IndexAt(Math.Max(0, offsetX));
        if (first < 0) return (0, -1);

        var rightEdge = offsetX + viewportWidth;
        var last = rightEdge >= TotalWidth ? Count - 1 : IndexAt(Math.Max(0, rightEdge - 1e-6));
        if (last < first) last = first;
        return (first, last);
    }

    /// <summary>Позиция колонки по ключу; −1, если ее нет среди показанных.</summary>
    public int IndexOf(string key)
    {
        for (var i = 0; i < _defs.Length; i++)
            if (_defs[i].Key == key) return i;
        return -1;
    }

    /// <summary>
    /// Собрать раскладку из определений колонок.
    /// </summary>
    /// <param name="defs">Все колонки таблицы в порядке показа, включая служебные «№» и «+».</param>
    /// <param name="manualWidths">Ручные ширины (ключ → DIP); пусто — стандартный режим.</param>
    /// <param name="hidden">Ключи колонок, скрытых в «Отображении»: их в раскладке не будет.</param>
    /// <param name="viewportWidth">Ширина окна таблицы в DIP; 0 — окно еще не разложено.</param>
    /// <param name="standardWidth">Стандартная ширина колонки (нижняя граница стандартного режима
    /// и ширина колонки без ручного значения).</param>
    /// <param name="quant">Шаг физического пикселя в DIP (1 / масштаб): ширины кладутся на
    /// физическую сетку, чтобы линии не мылились.</param>
    public static ColumnLayout Build(IReadOnlyList<TableColumnDef> defs,
                                     IReadOnlyDictionary<string, double> manualWidths,
                                     IReadOnlySet<string> hidden,
                                     double viewportWidth, double standardWidth, double quant)
    {
        if (quant <= 0) quant = 1;
        double Q(double v) => Math.Max(quant, Math.Round(v / quant) * quant);

        var shown  = defs.Where(d => !hidden.Contains(d.Key)).ToArray();
        var widths = new double[shown.Length];

        var service = 0.0;
        var movable = 0;
        for (var i = 0; i < shown.Length; i++)
        {
            if (shown[i].IsStarWidth) { movable++; continue; }
            widths[i] = Q(shown[i].FixedWidth);
            service  += widths[i];
        }
        if (movable == 0) return new ColumnLayout(shown, widths);

        var available = Math.Max(0, viewportWidth - service);
        var manual    = manualWidths.Count > 0;

        if (!manual)
        {
            // Стандартный режим: поровну от ширины окна, но не уже стандартной ширины.
            var share = Q(Math.Max(standardWidth, Math.Floor(available / movable / quant) * quant));
            for (var i = 0; i < shown.Length; i++)
                if (shown[i].IsStarWidth) widths[i] = share;
            return new ColumnLayout(shown, widths);
        }

        // Ручной режим: ширина из модели, ниже своей колонка не опускается; остаток окна
        // раздается пропорционально ширинам — пустоты справа нет, «+» у правого края.
        var sum = 0.0;
        for (var i = 0; i < shown.Length; i++)
        {
            if (!shown[i].IsStarWidth) continue;
            var w = manualWidths.TryGetValue(shown[i].Key, out var m) && m > 0 ? m : standardWidth;
            widths[i] = Q(Math.Max(MinManualWidth, w));
            sum += widths[i];
        }
        if (sum < available && sum > 0)
        {
            var extra = available - sum;
            for (var i = 0; i < shown.Length; i++)
                if (shown[i].IsStarWidth) widths[i] = Q(widths[i] + extra * widths[i] / sum);
        }
        return new ColumnLayout(shown, widths);
    }
}
