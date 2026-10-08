namespace SmartBase.Core.Models;

/// <summary>Чем набор на выделенной ячейке открывает ее правку.</summary>
public enum CellTypingStart
{
    /// <summary>Набор ячейку не открывает: тип правится не текстом (галочка, файл, вычисляемые)
    /// или набранное ему не подходит (буква в числе или в дате, пробел в списке).</summary>
    None,

    /// <summary>Поле ввода, в котором вместо прежнего значения — набранное: текст, ссылка, число,
    /// «Название».</summary>
    Text,

    /// <summary>Раскрытый список с пунктом, найденным по набранному: список, статус, пользователь.</summary>
    List,

    /// <summary>Ручной ввод даты, начатый набранной цифрой.</summary>
    Date,

    /// <summary>Раскрытый выбор с набранным в поле поиска: мультисписок (отбор пунктов), связь
    /// (отбор записей по названию).</summary>
    Picker,
}

/// <summary>
/// Правка ячейки набором, как в электронных таблицах: на выделенной ячейке начинают печатать, и
/// набранное сразу встает в нее вместо прежнего значения. Здесь — правила без сетки: какой правкой
/// набор открывает ячейку данного типа и какой пункт списка выбирает набранное. Таблица только
/// переводит ввод в эти вызовы и показывает результат (<c>TableView.CellTyping.cs</c>).
/// </summary>
public static class CellTyping
{
    /// <summary>
    /// Какой правкой набранное <paramref name="typed"/> открывает ячейку типа
    /// <paramref name="fieldType"/>. Число открывают цифры, минус и разделитель дробной части,
    /// дату — цифра, список, мультисписок и связь — любой видимый символ. Управляющие символы
    /// правку не открывают: это клавиши, а не текст (Ctrl+Backspace, например, приходит символом
    /// DEL).
    /// </summary>
    public static CellTypingStart Start(string? fieldType, string? typed)
    {
        if (string.IsNullOrEmpty(typed) || typed.Any(char.IsControl)) return CellTypingStart.None;
        if (FieldTypeInfo.IsComputed(fieldType)) return CellTypingStart.None;
        return fieldType switch
        {
            "checkbox" or "file" => CellTypingStart.None,
            "number" => IsPartialNumber(typed) ? CellTypingStart.Text : CellTypingStart.None,
            "date"   => char.IsAsciiDigit(typed[0]) ? CellTypingStart.Date : CellTypingStart.None,
            "select" or "status" or "user" =>
                string.IsNullOrWhiteSpace(typed) ? CellTypingStart.None : CellTypingStart.List,
            "multiselect" or "relation" =>
                string.IsNullOrWhiteSpace(typed) ? CellTypingStart.None : CellTypingStart.Picker,
            _ => CellTypingStart.Text,
        };
    }

    /// <summary>
    /// Допустимое (возможно, незаконченное) число: цифры, один разделитель дробной части (точка
    /// или запятая), ведущий минус — «», «-», «12», «12,», «-3.5». Им фильтруется посимвольный
    /// ввод в числовую ячейку, им же решается, открывает ли набор числовую ячейку.
    /// </summary>
    public static bool IsPartialNumber(string s)
    {
        if (s.Length == 0) return true;
        var i = s[0] == '-' ? 1 : 0;
        var seps = 0;
        for (; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '.' or ',') { if (++seps > 1) return false; }
            else if (!char.IsDigit(c)) return false;
        }
        return true;
    }

    /// <summary>
    /// Пункт списка для набора, как в списках системы. <paramref name="term"/> — что набрано в этой
    /// правке до сих пор, <paramref name="typed"/> — новый символ, <paramref name="current"/> —
    /// выбранный пункт (-1 — никакой). Набранное целиком ищется в начале пунктов: «в», затем «вы»
    /// уточняют до «Выполнено». Не подошло ни к одному — набор начинается заново с нового символа,
    /// а если выбранный пункт уже начинается с него, берется следующий на тот же символ: повтор
    /// одной буквы перебирает пункты на нее. Регистр не важен, «ё» равна «е».
    /// </summary>
    /// <returns>Набранное после этого символа и найденный пункт (-1 — ничего не подошло, выбор не
    /// меняется).</returns>
    public static (string Term, int Index) FindOption(IReadOnlyList<string> options, string term, string typed, int current)
    {
        var longer = term + typed;
        var hit = IndexOf(options, longer);
        if (hit >= 0 || term.Length == 0) return (longer, hit);

        var key  = Fold(typed);
        var from = current >= 0 && current < options.Count && Fold(options[current]).StartsWith(key, StringComparison.Ordinal)
            ? current + 1
            : 0;
        for (var k = 0; k < options.Count; k++)
        {
            var i = (from + k) % options.Count;
            if (Fold(options[i]).StartsWith(key, StringComparison.Ordinal)) return (typed, i);
        }
        return (typed, -1);
    }

    /// <summary>Первый пункт, начинающийся с <paramref name="term"/> (регистр не важен, «ё» равна
    /// «е»); -1 — такого нет или набранное пусто.</summary>
    public static int IndexOf(IReadOnlyList<string> options, string term)
    {
        if (term.Length == 0) return -1;
        var key = Fold(term);
        for (var i = 0; i < options.Count; i++)
            if (Fold(options[i]).StartsWith(key, StringComparison.Ordinal)) return i;
        return -1;
    }

    // Вид для сравнения: нижний регистр, «ё» как «е» — набирают обычно без «ё».
    private static string Fold(string s)
    {
        var chars = s.ToLowerInvariant().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (chars[i] == 'ё') chars[i] = 'е';
        return new string(chars);
    }
}
