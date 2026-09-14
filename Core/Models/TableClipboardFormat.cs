using System.Text;

namespace SmartBase.Core.Models;

/// <summary>
/// Формат прямоугольника ячеек в системном буфере обмена — общий для табличных редакторов:
/// значения строки разделены табуляцией, строки — переводом строки. Значение, внутри которого
/// есть табуляция, перевод строки или кавычка, берется в двойные кавычки, а кавычки внутри
/// удваиваются (правило CSV из RFC 4180, только разделитель — табуляция).
///
/// Благодаря этому скопированное здесь вставляется в табличный редактор готовыми столбцами, а
/// скопированное там — разбирается здесь. Класс ничего не знает ни про базу, ни про типы полей:
/// на входе и выходе просто матрица строк, поэтому он проверяется тестами целиком.
/// </summary>
public static class TableClipboardFormat
{
    private const char Tab = '\t';

    // Метка порядка байтов (U+FEFF). Кодом, а не символом в кавычках: в исходнике она невидима.
    private const char ByteOrderMark = (char)0xFEFF;

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
    /// Текст из буфера обмена в матрицу значений. Пустой текст и текст из одних пробелов (без
    /// табуляций и переводов строки) — пустая матрица: вставлять нечего. Строки могут быть разной
    /// длины: табличные редакторы обрезают хвостовые пустые значения, и выравнивать их здесь
    /// нельзя — иначе вставка затерла бы лишние ячейки.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];

        // Метка порядка байтов в начале — след кодировки, а не данные: иначе она прилипла бы к
        // первому значению, и первое вставленное название начиналось бы с невидимого символа.
        if (text[0] == ByteOrderMark) text = text[1..];

        // Одни пробелы — не таблица, а случайный мусор в буфере. Разобрать их как одно значение
        // значило бы одним Ctrl+V по выделенному столбцу стереть все, что в нем было.
        if (text.AsSpan().Trim(' ').IsEmpty) return [];

        return ParseCore(text, literalQuotes: null);
    }

    // literalQuotes — позиции открывающих кавычек, которые так и не закрылись: при повторном
    // проходе они читаются как обычный символ. Незакрытая кавычка иначе проглотила бы весь хвост
    // буфера в одно значение — с табуляциями и переводами строк внутри.
    private static List<IReadOnlyList<string>> ParseCore(string text, HashSet<int>? literalQuotes)
    {
        var rows       = new List<IReadOnlyList<string>>();
        var row        = new List<string>();
        var value      = new StringBuilder();
        var quoted     = false;   // мы внутри значения в кавычках
        var started    = false;   // значение началось с кавычки (иначе кавычка — обычный символ)
        var quoteStart = -1;      // где открылась текущая кавычка

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
                case '"' when value.Length == 0 && !started && literalQuotes?.Contains(i) != true:
                    quoted     = true;
                    started    = true;
                    quoteStart = i;
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

        // Текст кончился внутри кавычек — кавычка была не экранированием, а просто символом
        // (так поступают и табличные редакторы). Разбираем заново, считая ее обычной.
        if (quoted)
        {
            var literal = literalQuotes is null ? new HashSet<int>() : new HashSet<int>(literalQuotes);
            literal.Add(quoteStart);
            return ParseCore(text, literal);
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
