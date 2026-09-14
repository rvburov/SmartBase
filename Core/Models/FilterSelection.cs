namespace SmartBase.Core.Models;

/// <summary>
/// Галочки чеклиста в окне фильтра колонки — чистая логика поверх общего набора отмеченных
/// СЫРЫХ значений (<see cref="Checked"/>). Набор один на оба вида списка — плоский чеклист и
/// дерево дат — поэтому и правила тут общие; что именно «показано сейчас», сообщает
/// вызывающий (<c>visibleRaws</c>: найденное поиском, а без поиска — все).
///
/// Повторяет поведение табличных редакторов:
///  • ввод в поиске отмечает ВСЕ найденное, и так на каждое изменение текста — в отбор идет
///    то, что нашлось, без ручного щелканья по галочкам;
///  • очищенный поиск возвращает галочки, какими они были ДО начала ввода;
///  • «Готово» при поиске берет отмеченное СРЕДИ найденного и заменяет им фильтр («Заменить»),
///    без поиска — все отмеченное; отмечено все, что есть в колонке, — фильтра нет;
///  • при повторном открытии отобранные раньше значения стоят первыми
///    (<see cref="ChosenFirst{T}"/>) — их видно, не листая весь список.
/// </summary>
public sealed class FilterSelection
{
    /// <summary>Отмеченные сырые значения — общий набор со строками списка/дерева.</summary>
    public HashSet<string> Checked { get; }

    // Галочки до начала поиска: пока не null — поиск идет, очистка текста вернет их.
    private HashSet<string>? _beforeSearch;

    public FilterSelection(HashSet<string> checkedSet) => Checked = checkedSet;

    /// <summary>Идет ли поиск (в поле есть текст).</summary>
    public bool Searching => _beforeSearch is not null;

    /// <summary>Набор галочек заполнен заново (пришли данные) — снимок «до поиска», снятый с
    /// пустого набора, устарел; следующий <see cref="OnSearch"/> снимет его заново.</summary>
    public void Reset() => _beforeSearch = null;

    /// <summary>Пустой ли запрос: пробелы поиском не считаются (как <c>Trim</c> у списка).</summary>
    public static bool IsEmptyQuery(string? query) => string.IsNullOrWhiteSpace(query);

    /// <summary>
    /// Текст поиска изменился. Непустой запрос — найденное (<paramref name="visibleRaws"/>)
    /// отмечается целиком, а первый ввод запоминает прежние галочки; пустой — возвращает их.
    /// </summary>
    public void OnSearch(string? query, IEnumerable<string> visibleRaws)
    {
        if (IsEmptyQuery(query))
        {
            if (_beforeSearch is null) return;
            Checked.Clear();
            Checked.UnionWith(_beforeSearch);
            _beforeSearch = null;
            return;
        }

        _beforeSearch ??= new HashSet<string>(Checked);
        Checked.UnionWith(visibleRaws);
    }

    /// <summary>
    /// Итог по «Готово»: набор значений фильтра или null, если фильтр не нужен — отмечено все,
    /// что есть в колонке (<paramref name="domain"/>). При поиске в фильтр идут только отмеченные
    /// среди показанного, остальное отсекается; без поиска — все отмеченные, в том числе значения
    /// прежнего фильтра, которых сейчас нет в списке (их спрятал фильтр другой колонки).
    /// </summary>
    public HashSet<string>? Effective(IEnumerable<string> visibleRaws, IReadOnlySet<string> domain)
    {
        var effective = Searching
            ? visibleRaws.Where(Checked.Contains).ToHashSet()
            : new HashSet<string>(Checked);
        return effective.IsSupersetOf(domain) ? null : effective;
    }

    /// <summary>
    /// Порядок значений при открытии окна: сначала <paramref name="chosen"/> (значения
    /// действующего фильтра колонки), затем остальные; внутри групп порядок исходный.
    /// Без действующего фильтра список не меняется.
    /// </summary>
    public static List<T> ChosenFirst<T>(IEnumerable<T> items, Func<T, string> raw, IReadOnlySet<string>? chosen)
    {
        var list = items.ToList();
        if (chosen is null || chosen.Count == 0) return list;
        var first = list.Where(i => chosen.Contains(raw(i)));
        var rest  = list.Where(i => !chosen.Contains(raw(i)));
        return first.Concat(rest).ToList();
    }
}
