namespace SmartBase.Core.Calendar;

/// <summary>
/// Колонки дней Диаграммы. Обычно колонка — каждый день периода; при 5-дневке на масштабе
/// месяца колонки — только рабочие дни, как в «Месяце» и «Неделе»: суббота и воскресенье с оси
/// убраны, полоса Пт→Пн идет из пятницы прямо в понедельник, а ее длина — число рабочих дней.
/// Раньше выходные стояли на оси колонками, и при 5-дневке полосы тянулись через них, хотя
/// перенос и подпись длительности их уже пропускали.
/// </summary>
public sealed class DiagramColumns
{
    private readonly DateTime _start;
    private readonly int _days;
    // _before[k] — сколько видимых дней в [0, k) от начала периода; null — видим каждый день.
    private readonly int[]? _before;
    private readonly DateTime[] _visible;

    /// <param name="start">Первый день периода.</param>
    /// <param name="days">Дней в периоде.</param>
    /// <param name="workdaysOnly">Колонки — только рабочие дни (5-дневка на масштабе месяца).</param>
    public DiagramColumns(DateTime start, int days, bool workdaysOnly)
    {
        _start = start.Date;
        _days  = Math.Max(0, days);
        if (!workdaysOnly)
        {
            _visible = [];
            Count    = _days;
            return;
        }

        var before  = new int[_days + 1];
        var visible = new List<DateTime>(_days);
        for (var k = 0; k < _days; k++)
        {
            before[k] = visible.Count;
            var d = _start.AddDays(k);
            if (!CalendarMath.IsWeekend(d)) visible.Add(d);
        }
        before[_days] = visible.Count;
        _before  = before;
        _visible = [.. visible];
        Count    = _visible.Length;
    }

    /// <summary>Сколько колонок на оси.</summary>
    public int Count { get; }

    /// <summary>Колонки — только рабочие дни.</summary>
    public bool WorkdaysOnly => _before is not null;

    /// <summary>День колонки (номер прижимается к оси).</summary>
    public DateTime DayOf(int column) =>
        _before is null ? _start.AddDays(column)
        : _visible.Length == 0 ? _start
        : _visible[Math.Clamp(column, 0, _visible.Length - 1)];

    /// <summary>
    /// Номер колонки, с которой начинается день со сдвигом <paramref name="dayOffset"/> от
    /// начала периода. Выходной начинается там же, где ближайший следующий рабочий день; за
    /// краями периода счет продолжается по дням — так событие, начатое до периода, получает
    /// отрицательный номер, а кончившееся после — номер за последней колонкой.
    /// </summary>
    public int ColumnAt(int dayOffset) =>
        _before is null || dayOffset <= 0 ? dayOffset
        : dayOffset >= _days ? Count + (dayOffset - _days)
        : _before[dayOffset];

    /// <summary>
    /// Колонки события с первого по последний день включительно. <paramref name="InWeekend"/> —
    /// событие целиком в выходных: колонок у него нет, и обе границы — колонка рабочего дня
    /// после выходных (его рисуют кругом на стыке недель).
    /// </summary>
    public (int First, int Last, bool InWeekend) Span(DateTime firstDay, DateTime lastDay)
    {
        var a = (int)(firstDay.Date - _start).TotalDays;
        var b = Math.Max(a, (int)(lastDay.Date - _start).TotalDays);
        var first = ColumnAt(a);
        var last  = ColumnAt(b + 1) - 1;
        return last < first ? (first, first, true) : (first, last, false);
    }

    /// <summary>Положение момента на оси в колонках (дробное: доля дня внутри колонки). Момент
    /// в выходной — граница перед следующим рабочим днем.</summary>
    public double X(DateTime moment)
    {
        var total = (moment - _start).TotalDays;
        var k     = (int)Math.Floor(total);
        var frac  = total - k;
        if (_before is null) return k + frac;
        var inside = k >= 0 && k < _days && !CalendarMath.IsWeekend(_start.AddDays(k));
        return ColumnAt(k) + (inside ? frac : 0);
    }
}
