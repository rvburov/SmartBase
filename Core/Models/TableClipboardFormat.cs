using System.Text;

namespace SmartBase.Core.Models;

/// <summary>
/// Формат прямоугольника ячеек в системном буфере обмена — тот же, что у Excel и Гугл-таблиц:
/// значения строки разделены табуляцией, строки — переводом строки. Значение, внутри которого
/// есть табуляция, перевод строки или кавычка, берется в двойные кавычки, а кавычки внутри
/// удваиваются (правило CSV из RFC 4180, только разделитель — табуляция).
///
/// Благодаря этому скопированное здесь вставляется в Excel готовыми столбцами, а скопированное
/// в Excel — разбирается здесь. Класс ничего не знает ни про базу, ни про типы полей: на входе
/// и выходе просто матрица строк, поэтому он проверяется тестами целиком.
/// </summary>
public static class TableClipboardFormat
{
    private const char Tab = '\t';

    /// <summary>Матрица значений (строки × колонки) в текст для буфера обмена.</summary>
    public static string Build(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        for (var r = 0; r < rows.Count; r++)
        {
            if (r > 0) sb.Append('\n');
            var row = rows[r];
            for (var c = 0; c < row.Count; c++)
            {
                if (c > 0) sb.Append(Tab);
                sb.Append(Escape(row[c]));
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Текст из буфера обмена в матрицу значений. Пустой и пробельный текст — пустая матрица
    /// (вставлять нечего). Строки могут быть разной длины: Excel обрезает хвостовые пустые
    /// значения, и выравнивать их здесь нельзя — иначе вставка затерла бы лишние ячейки.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];

        var rows    = new List<IReadOnlyList<string>>();
        var row     = new List<string>();
        var value   = new StringBuilder();
        var quoted  = false;   // мы внутри значения в кавычках
        var started = false;   // значение началось с кавычки (иначе кавычка — обычный символ)

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (quoted)
            {
                if (ch != '"') { value.Append(ch); continue; }
                // Удвоенная кавычка внутри значения — это одна кавычка.
                if (i + 1 < text.Length && text[i + 1] == '"') { value.Append('"'); i++; continue; }
                quoted = false;
                continue;
            }

            switch (ch)
            {
                case '"' when value.Length == 0 && !started:
                    quoted  = true;
                    started = true;
                    continue;

                case Tab:
                    row.Add(value.ToString());
                    value.Clear();
                    started = false;
                    continue;

                // \r\n и одиночный \r — тоже конец строки: буфер приходит и из Windows-программ.
                case '\r' when i + 1 < text.Length && text[i + 1] == '\n':
                    i++;
                    goto case '\n';

                case '\r':
                case '\n':
                    row.Add(value.ToString());
                    value.Clear();
                    started = false;
                    rows.Add(row);
                    row = [];
                    continue;

                default:
                    value.Append(ch);
                    continue;
            }
        }

        // Хвост: последняя строка без перевода в конце. Текст, закончившийся переводом строки,
        // лишней пустой строки не дает — иначе вставка затирала бы строку под блоком.
        if (value.Length > 0 || row.Count > 0)
        {
            row.Add(value.ToString());
            rows.Add(row);
        }

        return rows;
    }

    private static string Escape(string? value)
    {
        var v = value ?? "";
        if (v.IndexOf(Tab) < 0 && v.IndexOf('\n') < 0 && v.IndexOf('\r') < 0 && v.IndexOf('"') < 0)
            return v;
        return '"' + v.Replace("\"", "\"\"") + '"';
    }
}
