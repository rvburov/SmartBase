namespace SmartBase.Core.Models;

/// <summary>
/// Отбор «кроме» в фильтре колонки. Набор значений фильтра (<c>key → HashSet</c>) обычно —
/// допустимые значения; с меткой <see cref="ExcludeMark"/> — исключаемые: строка проходит, если
/// ее значения в наборе нет. Такой отбор дает окно фильтра, когда значений у колонки больше, чем
/// оно показывает (<see cref="CappedFilterSelection"/>): снятую галочку нельзя выразить списком
/// допустимых, не зная всех значений. Метка хранится в том же наборе — сохраненные отборы
/// (ConstructorFilterMemory) и все, кто передает набор дальше, ее не теряют.
/// </summary>
public static class FilterExclusion
{
    /// <summary>Метка отбора «кроме»: управляющий символ, которого не бывает в значениях.</summary>
    public const string ExcludeMark = "\u0001кроме";

    public static bool IsExclude(IReadOnlySet<string> set) => set.Contains(ExcludeMark);

    /// <summary>Отбор «кроме» <paramref name="values"/>.</summary>
    public static HashSet<string> Exclude(IEnumerable<string> values) => [ExcludeMark, .. values];

    /// <summary>Значения набора без метки.</summary>
    public static List<string> Values(IReadOnlySet<string> set) => set.Where(v => v != ExcludeMark).ToList();
}

/// <summary>
/// Галочки окна фильтра колонки, у которой значений больше, чем показывает список (первые
/// <c>FilterValueCap</c> по алфавиту, остальное — поиском по базе). Всех значений в памяти нет,
/// поэтому галочки — не набор отмеченных, как у <see cref="FilterSelection"/>, а правило: «все,
/// кроме снятых» (<see cref="AllByDefault"/>) или «только отмеченные». Поиск ведет себя как в
/// табличных редакторах: найденное отмечается целиком на каждое изменение текста, очистка
/// поиска возвращает прежние галочки, «Готово» при поиске берет отмеченное среди найденного.
/// </summary>
public sealed class CappedFilterSelection
{
    // AllByDefault — снятые значения; иначе — отмеченные.
    private readonly HashSet<string> _explicit = [];
    // Не null — идет поиск: найденное отмечено, кроме снятого за время этого поиска.
    private HashSet<string>? _searchUnchecked;

    /// <summary>Отмечено все, кроме снятых (иначе — только отмеченные).</summary>
    public bool AllByDefault { get; private set; }

    public bool Searching => _searchUnchecked is not null;

    /// <param name="current">Действующий фильтр колонки: null — фильтра нет (отмечено все).</param>
    public CappedFilterSelection(IReadOnlySet<string>? current)
    {
        if (current is null) AllByDefault = true;
        else if (FilterExclusion.IsExclude(current)) { AllByDefault = true; _explicit.UnionWith(FilterExclusion.Values(current)); }
        else _explicit.UnionWith(current);
    }

    /// <summary>Отмечено ли все без исключений (без поиска).</summary>
    public bool AllChecked => AllByDefault && _explicit.Count == 0;

    public bool IsChecked(string raw) =>
        _searchUnchecked is { } off ? !off.Contains(raw)
        : AllByDefault ? !_explicit.Contains(raw) : _explicit.Contains(raw);

    public void Set(string raw, bool on)
    {
        if (_searchUnchecked is { } off)
        {
            if (on) off.Remove(raw); else off.Add(raw);
            return;
        }
        if (AllByDefault == on) _explicit.Remove(raw); else _explicit.Add(raw);
    }

    /// <summary>«(Выделить все)»: без поиска — все значения колонки; при поиске — найденное
    /// (<paramref name="found"/>).</summary>
    public void SetAll(bool on, IEnumerable<string> found)
    {
        if (_searchUnchecked is { } off)
        {
            off.Clear();
            if (!on) off.UnionWith(found);
            return;
        }
        AllByDefault = on;
        _explicit.Clear();
    }

    /// <summary>Текст поиска изменился: непустой — найденное отмечается целиком заново,
    /// пустой — возвращаются галочки, бывшие до поиска.</summary>
    public void OnSearch(string? query) =>
        _searchUnchecked = FilterSelection.IsEmptyQuery(query) ? null : [];

    /// <summary>Итог «Готово»: набор фильтра или null — фильтр не нужен. При поиске — отмеченное
    /// среди найденного (<paramref name="found"/>), остальное отсекается; без поиска — «кроме
    /// снятых» или отмеченные.</summary>
    public HashSet<string>? Effective(IEnumerable<string> found)
    {
        if (Searching) return found.Where(IsChecked).ToHashSet();
        if (AllByDefault) return _explicit.Count == 0 ? null : FilterExclusion.Exclude(_explicit);
        return new HashSet<string>(_explicit);
    }
}
