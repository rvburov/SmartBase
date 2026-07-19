namespace SmartBase.Utils;

/// <summary>
/// Компактный текст «до → после» для журнала изменений (change_log.Detail).
/// Готовится ПРИ ЗАПИСИ и хранится уже свёрнутым: короткие значения целиком
/// («123» → «456»), у длинных отрезаются совпадающие начало и конец, остаётся
/// изменившийся кусок с небольшим контекстом и «…» по краям («…срок оплаты 30…» →
/// «…срок оплаты 45…») — журнал не раздувается даже правками огромных текстов.
/// </summary>
public static class ChangeDetail
{
    // Короткие значения показываются целиком; порог совпадает с типичной шириной ячейки.
    private const int FullLimit = 40;
    // Символов совпадающего контекста вокруг изменившегося куска.
    private const int Context = 15;
    // Максимум фрагмента одной стороны; длиннее — середина сворачивается в « … ».
    private const int WindowMax = 60;
    // Страховочный предел всей строки Detail.
    private const int DetailMax = 200;

    /// <summary>Переименование: «Старое» → «Новое» (длинные имена сворачиваются).</summary>
    public static string Rename(string oldName, string newName) => Diff(oldName, newName);

    /// <summary>
    /// Правка значения: «{владелец} ({поле}: «до» → «после»)». Владелец (имя записи)
    /// может быть пустым — тогда только «{поле}: …».
    /// </summary>
    public static string Value(string owner, string field, string oldValue, string newValue)
    {
        var change = $"{field}: {Diff(oldValue, newValue)}";
        var text   = owner.Length > 0 ? $"{Clip(owner, FullLimit)} ({change})" : change;
        return Clip(text, DetailMax);
    }

    /// <summary>«до» → «после» с сворачиванием длинных текстов до изменившегося куска.</summary>
    public static string Diff(string oldValue, string newValue)
    {
        var a = Normalize(oldValue);
        var b = Normalize(newValue);

        string fragA, fragB;
        if (a.Length <= FullLimit && b.Length <= FullLimit)
        {
            fragA = a;
            fragB = b;
        }
        else
        {
            // Совпадающий префикс и суффикс (суффикс не залезает на префикс).
            var prefix = 0;
            var maxP   = Math.Min(a.Length, b.Length);
            while (prefix < maxP && a[prefix] == b[prefix]) prefix++;

            var suffix = 0;
            var maxS   = maxP - prefix;
            while (suffix < maxS && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]) suffix++;

            fragA = Window(a, prefix, a.Length - suffix);
            fragB = Window(b, prefix, b.Length - suffix);
        }

        return Clip($"{Quote(fragA)} → {Quote(fragB)}", DetailMax);
    }

    private static string Quote(string s) => s.Length == 0 ? "(пусто)" : $"«{s}»";

    // Изменившийся кусок [from..to) + контекст по краям; «…» там, где обрезано.
    // Слишком широкое окно (правки в разных местах длинного текста) сворачивается
    // посередине: «…в офисе … оплаты 30…».
    private static string Window(string text, int from, int to)
    {
        var start = Math.Max(0, from - Context);
        var end   = Math.Min(text.Length, to + Context);
        var frag  = text[start..end];
        if (frag.Length > WindowMax)
        {
            var half = WindowMax / 2 - 2;
            frag = $"{frag[..half]} … {frag[^half..]}";
        }
        return $"{(start > 0 ? "…" : "")}{frag}{(end < text.Length ? "…" : "")}";
    }

    // Переводы строк схлопываются — Detail всегда одна строка таблицы.
    private static string Normalize(string s) =>
        s.Contains('\n') ? s.Replace("\r", "").Replace('\n', ' ').Trim() : s.Trim();

    private static string Clip(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";
}
