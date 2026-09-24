namespace SmartBase.ViewModels.ConstructorDb;

/// <summary>
/// Чистая арифметика страниц виртуального списка строк — без базы и потоков, чтобы проверяться
/// модульными тестами: бюджет страниц кеша от ширины окна колонок, недостающие поля страницы,
/// упреждающая страница по направлению прокрутки, разбиение страницы сортированного вида на
/// часть головы и часть хвоста.
/// </summary>
internal static class RowPaging
{
    public const int PageSize = 200;

    // Бюджет кеша — в ЯЧЕЙКАХ: строка несет значения только окна колонок, поэтому предел страниц
    // считается от ширины окна (а не от числа всех полей коллекции, как раньше). Узкие таблицы —
    // прежние 50 страниц; окно в 20 колонок на любой ширине коллекции — тоже 50; нижняя граница —
    // из SyncRangeRows (синхронный путь выделения диапазона рассчитан на теплый кеш).
    private const int CellBudget  = 400_000;
    private const int MaxPagesCap = 50;
    public  const int SyncRangeRows = 2_000;
    private const int MinPages    = SyncRangeRows / PageSize + 1;

    /// <summary>Сколько страниц держать в кеше при окне в <paramref name="windowFields"/> полей.</summary>
    public static int MaxPages(int windowFields) =>
        Math.Clamp(CellBudget / (Math.Max(1, windowFields) * PageSize), MinPages, MaxPagesCap);

    /// <summary>Поля окна, которых у страницы еще нет и которые не дочитываются прямо сейчас;
    /// null — дочитывать нечего. <paramref name="loaded"/> null — у страницы все поля.</summary>
    public static int[]? MissingFields(int[]? loaded, IReadOnlyList<int> window, IReadOnlySet<int>? inFlight)
    {
        if (loaded is null) return null;
        List<int>? missing = null;
        foreach (var f in window)
        {
            if (Array.IndexOf(loaded, f) >= 0) continue;
            if (inFlight is not null && inFlight.Contains(f)) continue;
            (missing ??= []).Add(f);
        }
        return missing?.ToArray();
    }

    /// <summary>Объединение полей страницы с дочитанными (отсортировано, без повторов).</summary>
    public static int[] Union(int[]? loaded, IReadOnlyList<int> added)
    {
        var set = new HashSet<int>(loaded ?? []);
        set.UnionWith(added);
        var arr = set.ToArray();
        Array.Sort(arr);
        return arr;
    }

    /// <summary>Страница для упреждающей подгрузки: следующая за видимым окном при движении вниз
    /// (и при первом показе), предыдущая — при движении вверх; null — за краем таблицы.</summary>
    public static int? PrefetchPage(int firstPage, int lastPage, int prevFirstRow, int firstRow, int pageCount)
    {
        var down = prevFirstRow < 0 || firstRow >= prevFirstRow;
        var page = down ? lastPage + 1 : firstPage - 1;
        return page >= 0 && page < pageCount ? page : null;
    }

    /// <summary>Разбить строки [skip, skip + take) сортированного вида на часть головы и часть
    /// хвоста при известной длине головы: сколько строк брать из головы (со смещением skip) и
    /// сколько из хвоста (со смещением tailSkip внутри хвоста).</summary>
    public static (int HeadTake, int TailSkip, int TailTake) SplitSorted(int skip, int take, int headCount)
    {
        var headTake = Math.Clamp(headCount - skip, 0, take);
        var tailSkip = Math.Max(0, skip - headCount);
        var tailTake = take - headTake;
        return (headTake, tailSkip, tailTake);
    }

    /// <summary>Число страниц для <paramref name="rowCount"/> строк.</summary>
    public static int PageCount(int rowCount) => (rowCount + PageSize - 1) / PageSize;
}
