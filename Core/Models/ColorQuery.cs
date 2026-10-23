namespace SmartBase.Core.Models;

/// <summary>
/// Отбор и сортировка таблицы по цвету — как «Фильтр по цвету» и «Сортировка по цвету» табличных
/// редакторов. Цвет — тот, что ВИДЕН в ячейке колонки: свой цвет ячейки, иначе цвет строки или
/// колонки, выставленный позже (<see cref="RowStyle.Resolve"/>). Фон и цвет текста — отдельные
/// условия.
///
/// Условие живет в тех же местах, что отбор по значениям и сортировка, — особыми ключами, а не
/// отдельным полем: так его без правок подхватывают запоминание отбора за коллекцией, снятие отбора
/// по удаленной колонке и подсветка воронки в шапке.
///
/// <list type="bullet">
/// <item>отбор — ключ <c>color:&lt;колонка&gt;</c> в словаре отбора, значения — метки цвета;</item>
/// <item>сортировка — ключ <c>color:&lt;колонка&gt;|&lt;метка&gt;</c> вместо ключа колонки;
///       «по возрастанию» — записи этого цвета сверху, «по убыванию» — снизу; остальные записи
///       сохраняют порядок таблицы.</item>
/// </list>
///
/// Метка цвета — <c>bg:#RRGGBB</c> (фон), <c>fg:#RRGGBB</c> (текст); <c>bg:</c> — без заливки,
/// <c>fg:</c> — стандартный цвет текста.
/// </summary>
public static class ColorQuery
{
    private const string Prefix    = "color:";
    private const string SortSplit = "|";
    private const string BgMark    = "bg:";
    private const string FgMark    = "fg:";

    /// <summary>Ключ отбора по цвету в словаре отбора таблицы.</summary>
    public static string FilterKey(string columnKey) => Prefix + columnKey;

    /// <summary>Ключ сортировки по цвету (вместо ключа колонки).</summary>
    public static string SortKey(string columnKey, string token) => Prefix + columnKey + SortSplit + token;

    /// <summary>Это ключ условия по цвету (отбора или сортировки).</summary>
    public static bool IsColorKey(string? key) => key is not null && key.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Колонка ключа: у условия по цвету — его колонка, у обычного ключа — он сам. Так
    /// воронка в шапке и снятие условий по удаленной колонке работают одинаково для всех.</summary>
    public static string ColumnOf(string key)
    {
        if (!IsColorKey(key)) return key;
        var rest = key[Prefix.Length..];
        var bar  = rest.IndexOf(SortSplit, StringComparison.Ordinal);
        return bar < 0 ? rest : rest[..bar];
    }

    /// <summary>Разобрать ключ сортировки по цвету. false — это не он (обычная сортировка).</summary>
    public static bool TryParseSortKey(string? key, out string columnKey, out string token)
    {
        columnKey = token = "";
        if (!IsColorKey(key)) return false;
        var rest = key![Prefix.Length..];
        var bar  = rest.IndexOf(SortSplit, StringComparison.Ordinal);
        if (bar <= 0 || !TryParseToken(rest[(bar + 1)..], out _, out _)) return false;
        columnKey = rest[..bar];
        token     = rest[(bar + 1)..];
        return true;
    }

    /// <summary>Метка цвета: аспект и hex; пустой hex — «без заливки» или «стандартный цвет текста».</summary>
    public static string Token(StyleAspect aspect, string? hex) =>
        (aspect == StyleAspect.Text ? FgMark : BgMark) + (AppPalette.IsHex(hex) ? hex!.ToUpperInvariant() : "");

    public static bool TryParseToken(string? token, out StyleAspect aspect, out string? hex)
    {
        aspect = StyleAspect.Background;
        hex    = null;
        if (token is null) return false;
        string rest;
        if (token.StartsWith(BgMark, StringComparison.Ordinal)) rest = token[BgMark.Length..];
        else if (token.StartsWith(FgMark, StringComparison.Ordinal)) { aspect = StyleAspect.Text; rest = token[FgMark.Length..]; }
        else return false;
        if (rest.Length == 0) return true;
        if (!AppPalette.IsHex(rest)) return false;
        hex = rest.ToUpperInvariant();
        return true;
    }

    /// <summary>Видимый цвет ячейки (<paramref name="effective"/> — уже сведенный каскад) совпадает
    /// с меткой. Сравнение без учета регистра hex.</summary>
    public static bool Matches(RowStyle effective, string token)
    {
        if (!TryParseToken(token, out var aspect, out var hex)) return false;
        var value = aspect == StyleAspect.Text ? effective.Fg : effective.Bg;
        return hex is null ? value is null : string.Equals(value, hex, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Метки обоих аспектов видимого цвета ячейки — для списка цветов колонки.</summary>
    public static (string Background, string Text) TokensOf(RowStyle effective) =>
        (Token(StyleAspect.Background, effective.Bg), Token(StyleAspect.Text, effective.Fg));

    /// <summary>Как назвать цвет метки человеку: имя из палитры, «Без заливки», «Стандартный» или hex.</summary>
    public static string Name(string token)
    {
        if (!TryParseToken(token, out var aspect, out var hex)) return token;
        if (hex is null) return aspect == StyleAspect.Text ? "Стандартный" : "Без заливки";
        foreach (var swatch in AppPalette.Colors)
            if (string.Equals(swatch.Hex, hex, StringComparison.OrdinalIgnoreCase)) return swatch.Name;
        return hex;
    }
}
