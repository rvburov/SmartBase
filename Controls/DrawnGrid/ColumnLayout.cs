using SmartBase.ViewModels.ConstructorDb;

namespace SmartBase.Controls.DrawnGrid;

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
/// кадр разметки. Служебные таблицы (этап 6а) строят раскладку <see cref="BuildRounded"/> — по
/// правилам прежних таблиц на DataGrid, до пикселя.
/// </summary>
public sealed class ColumnLayout
{
    /// <summary>Пол ручной ширины — как <c>MinManualColWidth</c> нынешней таблицы.</summary>
    public const double MinManualWidth = 48;

    private readonly TableColumnDef[] _defs;
    private readonly double[]         _lefts;    // левая граница каждой колонки
    private readonly double[]         _widths;
    private readonly double           _total;    // протяженность прокрутки (у служебных таблиц — сумма дробных ширин до округления)

    public static readonly ColumnLayout Empty = new([], [], [], 0);

    private ColumnLayout(TableColumnDef[] defs, double[] lefts, double[] widths, double total)
    {
        _defs   = defs;
        _lefts  = lefts;
        _widths = widths;
        _total  = total;
    }

    // Колонки встык: левая граница — сумма ширин предыдущих.
    private static ColumnLayout Contiguous(TableColumnDef[] defs, double[] widths)
    {
        var lefts = new double[defs.Length];
        var x = 0.0;
        for (var i = 0; i < defs.Length; i++) { lefts[i] = x; x += widths[i]; }
        return new ColumnLayout(defs, lefts, widths, x);
    }

    public int Count => _defs.Length;

    public TableColumnDef this[int index] => _defs[index];

    public double Left(int index)  => _lefts[index];
    public double Right(int index) => _lefts[index] + _widths[index];
    public double Width(int index) => _widths[index];

    /// <summary>Протяженность горизонтальной прокрутки — сумма ширин показанных колонок.</summary>
    public double TotalWidth => _total;

    /// <summary>Индекс колонки под координатой <paramref name="x"/> (в координатах содержимого,
    /// без учета прокрутки); −1, если координата вне колонок. Из перекрывающихся на пиксель ячеек
    /// служебной таблицы отвечает правая — она нарисована поверх.</summary>
    public int IndexAt(double x)
    {
        if (Count == 0 || x < 0 || x >= Math.Max(_total, Right(Count - 1))) return -1;
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
        var movable = new List<int>();
        for (var i = 0; i < shown.Length; i++)
        {
            if (shown[i].IsStarWidth) { movable.Add(i); continue; }
            widths[i] = Q(shown[i].FixedWidth);
            service  += widths[i];
        }
        if (movable.Count == 0) return Contiguous(shown, widths);

        // Все в целых физических пикселях (квантах): сумма ширин обязана точно равняться ширине
        // окна, когда таблица помещается, — иначе справа от «+» остается полоса или на пиксель
        // вылезает горизонтальная прокрутка. Остатки раздаются методом наибольших остатков, как
        // снап ширин нынешней таблицы.
        var availUnits = (int)Math.Floor(Math.Max(0, viewportWidth - service) / quant + 1e-6);
        var units      = new int[movable.Count];
        var manual     = manualWidths.Count > 0;

        if (!manual)
        {
            // Стандартный режим: поровну от ширины окна, остаток — первым колонкам; уже
            // стандартной ширины колонки не бывают (тогда таблица шире окна).
            var baseU = availUnits / movable.Count;
            if (baseU * quant < standardWidth)
            {
                var std = (int)Math.Max(1, Math.Round(standardWidth / quant));
                for (var k = 0; k < units.Length; k++) units[k] = std;
            }
            else
            {
                var rem = availUnits - baseU * movable.Count;
                for (var k = 0; k < units.Length; k++) units[k] = baseU + (k < rem ? 1 : 0);
            }
        }
        else
        {
            // Ручной режим: ширина из модели, ниже своей колонка не опускается; остаток окна
            // раздается пропорционально ширинам — пустоты справа нет, «+» у правого края.
            var model = new double[movable.Count];
            var sumU  = 0;
            for (var k = 0; k < movable.Count; k++)
            {
                var key = shown[movable[k]].Key;
                var w   = manualWidths.TryGetValue(key, out var m) && m > 0 ? m : standardWidth;
                model[k] = Math.Max(MinManualWidth, w) / quant;
                units[k] = Math.Max(1, (int)Math.Floor(model[k] + 1e-6));
                sumU    += units[k];
            }
            if (availUnits <= sumU)
            {
                // Таблица шире окна: ширины из модели, округленные к ближайшему кванту.
                for (var k = 0; k < units.Length; k++) units[k] = Math.Max(1, (int)Math.Round(model[k]));
            }
            else
            {
                var extra = availUnits - sumU;
                var share = new double[units.Length];
                var given = 0;
                for (var k = 0; k < units.Length; k++)
                {
                    share[k]  = extra * (double)units[k] / sumU;
                    var whole = (int)Math.Floor(share[k]);
                    units[k] += whole;
                    given    += whole;
                }
                foreach (var k in Enumerable.Range(0, units.Length)
                                            .OrderByDescending(k => share[k] - Math.Floor(share[k]))
                                            .ThenBy(k => k)
                                            .Take(Math.Max(0, extra - given)))
                    units[k]++;
            }
        }

        for (var k = 0; k < movable.Count; k++) widths[movable[k]] = units[k] * quant;
        return Contiguous(shown, widths);
    }

    /// <summary>
    /// Раскладка служебных таблиц — как у прежних таблиц на DataGrid, до пикселя. Пиксельные колонки
    /// берут свою ширину; звездные делят остаток окна поровну ДРОБНО, без квантования, не опускаясь
    /// ниже своей минимальной ширины: кому равная доля мала, тот встает на минимум и выходит из
    /// дележа, остальные делят остаток. Прямоугольники ячеек — по правилу раскладки Avalonia с
    /// округлением: начало ячейки округляется к ближайшему физическому пикселю (половина — к четному),
    /// ширина — вверх вместе с дробной частью начала (<c>ceil(x + w) − floor(x)</c>). Соседние ячейки
    /// поэтому могут перекрываться на пиксель, а линия колонки стоит на <c>ceil(x + w) − 1</c>.
    /// Протяженность прокрутки — сумма дробных ширин: последняя ячейка после округления вылезает за
    /// окно на пиксель, как и у DataGrid, и это не повод показывать полосу.
    /// </summary>
    /// <param name="defs">Колонки в порядке показа.</param>
    /// <param name="viewportWidth">Ширина окна таблицы в DIP (без вертикальной полосы).</param>
    /// <param name="scale">Физических пикселей в DIP (масштаб экрана × зум).</param>
    public static ColumnLayout BuildRounded(IReadOnlyList<TableColumnDef> defs, double viewportWidth, double scale)
    {
        if (scale <= 0) scale = 1;
        var shown  = defs.ToArray();
        var widths = new double[shown.Length];

        var fixedSum = 0.0;
        var stars    = new List<int>();
        for (var i = 0; i < shown.Length; i++)
        {
            if (shown[i].IsStarWidth) { stars.Add(i); continue; }
            widths[i] = shown[i].FixedWidth;
            fixedSum += widths[i];
        }
        if (stars.Count > 0)
        {
            var sharing = new List<int>(stars);
            var left    = Math.Max(0, viewportWidth - fixedSum);
            while (true)
            {
                var share  = sharing.Count > 0 ? left / sharing.Count : 0;
                var pinned = sharing.Where(k => shown[k].MinWidth > share).ToList();
                if (pinned.Count == 0) break;
                foreach (var k in pinned)
                {
                    widths[k] = shown[k].MinWidth;
                    left     -= shown[k].MinWidth;
                    sharing.Remove(k);
                }
            }
            if (sharing.Count > 0)
            {
                var share = Math.Max(0, left) / sharing.Count;
                foreach (var k in sharing) widths[k] = share;
            }
        }

        const double eps = 1e-6;
        var lefts   = new double[shown.Length];
        var rounded = new double[shown.Length];
        var x = 0.0;
        for (var i = 0; i < shown.Length; i++)
        {
            lefts[i]   = Math.Round(x * scale, MidpointRounding.ToEven) / scale;
            rounded[i] = (Math.Ceiling((x + widths[i]) * scale - eps) - Math.Floor(x * scale + eps)) / scale;
            x += widths[i];
        }
        return new ColumnLayout(shown, lefts, rounded, x);
    }
}
