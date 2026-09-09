using System.Globalization;
using System.Text;

namespace SmartBase.Core.Importers.TableSources;

/// <summary>
/// Регистрация legacy-кодировок (Windows-1251 для CSV, кодировки старых .xls) — в .NET их нет из
/// коробки. Один раз на процесс: вызывается из Program.Main и, для консольных харнессов, из
/// самих источников перед чтением.
/// </summary>
public static class EncodingSetup
{
    private static bool _done;

    public static void Ensure()
    {
        if (_done) return;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _done = true;
    }
}

/// <summary>
/// Общие правила источников импорта: уникальные имена свойств, нумерация «Без названия»,
/// разбор номера/буквы столбца, форматирование значений Excel, определение кодировки и
/// разделителя CSV, форвард-обертка прогресса.
/// </summary>
public static class SourceHelpers
{
    // ── Имена и названия ──────────────────────────────────────────────────────

    /// <summary>
    /// Имена в порядке столбцов: пустые → «Без названия» (или «Без названия 1/2…» при нескольких),
    /// повторы дедуплицируются добавлением номера. Возвращает «номер столбца → итоговое имя».
    /// </summary>
    public static Dictionary<int, string> AssignUniqueNames(List<(int Col, string Raw)> entries)
    {
        var emptyCount = entries.Count(e => string.IsNullOrEmpty(e.Raw));
        var used   = new HashSet<string>(StringComparer.Ordinal);
        var result = new Dictionary<int, string>();
        var emptyIdx = 0;

        foreach (var (col, raw) in entries)
        {
            string baseName;
            if (string.IsNullOrEmpty(raw))
            {
                emptyIdx++;
                baseName = emptyCount == 1 ? "Без названия" : $"Без названия {emptyIdx}";
            }
            else baseName = raw;

            var name = baseName;
            var n = 2;
            while (!used.Add(name)) name = $"{baseName} {n++}";
            result[col] = name;
        }

        return result;
    }

    /// <summary>
    /// Пустые названия записей → «Без названия» (одна такая запись) или «Без названия N»
    /// (несколько; счетчик — из прохода 1). Строки не пропускаются, порядок сохраняется.
    /// </summary>
    public static IEnumerable<ImportRow> WithTitles(IEnumerable<ImportRow> rows, long emptyTitleCount)
    {
        long i = 0;
        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.Title)) { yield return row; continue; }
            i++;
            yield return row with { Title = emptyTitleCount == 1 ? "Без названия" : $"Без названия {i}" };
        }
    }

    /// <summary>«1»/«A» → номер столбца (1-based). Бросает дружелюбную ошибку при неверном вводе.</summary>
    public static int ParseColumn(string raw, string label)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0)
            throw new ImportException($"{label}: укажите номер (например, 1) или букву (например, A).");

        if (int.TryParse(s, out var num))
        {
            if (num < 1) throw new ImportException($"{label}: номер столбца должен быть ≥ 1.");
            return num;
        }

        // Буквенный адрес Excel: A → 1, Z → 26, AA → 27 … Только латиница.
        var n  = 0L;
        var ok = s.Length > 0;
        foreach (var ch in s.ToUpperInvariant())
        {
            if (ch is < 'A' or > 'Z') { ok = false; break; }
            n = n * 26 + (ch - 'A' + 1);
            if (n > int.MaxValue / 2) { ok = false; break; }
        }
        if (ok && n >= 1) return (int)n;

        throw new ImportException($"{label}: укажите номер (например, 1) или букву (например, A).");
    }

    // ── Исключения листов/строк/столбцов (ручной режим Excel) ────────────────

    /// <summary>Названия исключаемых листов: через запятую/точку с запятой, без учета регистра.</summary>
    public static HashSet<string> ParseSheetExclusions(string? raw)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in Split(raw)) set.Add(part);
        return set;
    }

    /// <summary>Исключаемые строки: номера и диапазоны («2, 5-7»). Ошибка ввода — дружелюбная.</summary>
    public static RangeSet ParseRowExclusions(string? raw) =>
        ParseRanges(raw, s =>
        {
            if (long.TryParse(s, out var v) && v >= 1) return v;
            throw new ImportException(
                $"Исключить строки: «{s}» — укажите номера строк или диапазоны (например, 2, 5-7).");
        });

    /// <summary>Исключаемые столбцы: номера, буквы и диапазоны («B, D-F, 8»).</summary>
    public static RangeSet ParseColumnExclusions(string? raw) =>
        ParseRanges(raw, s => ParseColumn(s, "Исключить столбцы"));

    private static RangeSet ParseRanges(string? raw, Func<string, long> parseOne)
    {
        var set = new RangeSet();
        foreach (var part in Split(raw))
        {
            var dash = part.IndexOf('-');
            if (dash > 0 && dash < part.Length - 1)
            {
                var from = parseOne(part[..dash].Trim());
                var to   = parseOne(part[(dash + 1)..].Trim());
                if (to < from) (from, to) = (to, from);
                set.Add(from, to);
            }
            else
            {
                var v = parseOne(part);
                set.Add(v, v);
            }
        }
        return set;
    }

    private static string[] Split(string? raw) =>
        (raw ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ── Значения и ключи JSON ─────────────────────────────────────────────────
    //
    // Источников JSON два — файл (потоковый) и ответ чужой программы (уже в памяти), — и
    // решать «какой ключ считать названием» они обязаны ОДИНАКОВО. Иначе один и тот же набор
    // записей, пришедший файлом и по сети, ложился бы в коллекцию по-разному.

    /// <summary>
    /// Ключ названия записи: «Название» → name → title (без учета регистра, в этом порядке),
    /// иначе первый ключ первого объекта. <c>null</c> — ключей нет вовсе.
    /// </summary>
    public static string? PickJsonTitleKey(List<string> keys, string? firstKey)
    {
        foreach (var candidate in new[] { "название", "name", "title" })
        {
            var hit = keys.FirstOrDefault(k => string.Equals(k, candidate, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }

        return firstKey ?? (keys.Count > 0 ? keys[0] : null);
    }

    /// <summary>
    /// Значение JSON текстом: строка как есть, null → пусто, числа и булевы — сырым токеном
    /// (чтобы не терять точность на округлении), вложенный объект или массив — сырым JSON.
    /// </summary>
    public static string FormatJsonValue(System.Text.Json.JsonElement value) => value.ValueKind switch
    {
        System.Text.Json.JsonValueKind.String => value.GetString() ?? "",

        System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined => "",

        _ => value.GetRawText(),
    };

    // ── Значения Excel ────────────────────────────────────────────────────────

    /// <summary>
    /// Текст ячейки Excel для импорта. Даты — фиксированный «дд.MM.гггг» (со временем —
    /// «дд.MM.гггг чч:мм»), совпадает с форматом DateValue: импортированный столбец без потерь
    /// переводится в тип «дата». Числа — «G15» инвариантно (15 значащих цифр — столько хранит
    /// Excel; без двоичного шума и с точкой-разделителем). Числовой формат ячейки (проценты,
    /// валюта) НЕ применяется — сохраняется само значение. null и ошибки ячеек → "".
    /// </summary>
    public static string FormatExcelValue(object? value) => value switch
    {
        null          => "",
        string s      => s,
        DateTime dt   => dt.TimeOfDay == TimeSpan.Zero
                             ? dt.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
                             : dt.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
        double d      => d.ToString("G15", CultureInfo.InvariantCulture),
        float f       => ((double)f).ToString("G15", CultureInfo.InvariantCulture),
        bool b        => b ? "TRUE" : "FALSE",
        TimeSpan ts   => ts.ToString(@"hh\:mm\:ss"),
        decimal m     => m.ToString(CultureInfo.InvariantCulture),
        _             => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    // ── CSV: кодировка и разделитель ──────────────────────────────────────────

    /// <summary>
    /// Кодировка и разделитель CSV по первым 64 КБ файла: BOM → UTF-8/UTF-16; иначе строгая
    /// проверка UTF-8 (оборванный на границе сэмпла многобайтовый символ не считается ошибкой),
    /// невалидный UTF-8 → Windows-1251 (частая кодировка русских CSV из старого Excel/1С).
    /// Разделитель — по первой непустой строке: «;» (RU-Excel), таб или запятая.
    /// </summary>
    public static (Encoding Encoding, char Delimiter) SniffCsv(string path)
    {
        EncodingSetup.Ensure();

        byte[] sample;
        int n;
        long fileLen;
        using (var fs = ImportErrors.OpenRead(path))
        {
            fileLen = fs.Length;
            if (fileLen == 0) throw new ImportException("Файл пустой — нет данных для импорта.");
            sample = new byte[64 * 1024];
            n = 0;
            while (n < sample.Length)
            {
                var k = fs.Read(sample, n, sample.Length - n);
                if (k == 0) break;
                n += k;
            }
        }

        var enc = DetectEncoding(sample, n, partial: fileLen > n);

        var text = enc.GetString(sample, 0, n);
        var line = text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
        int Count(char c) => line.Count(ch => ch == c);
        var semicolons = Count(';');
        var tabs       = Count('\t');
        var commas     = Count(',');
        var delim = semicolons > 0 && semicolons >= commas && semicolons >= tabs ? ';'
                  : tabs > 0 && tabs >= commas ? '\t'
                  : ',';
        return (enc, delim);
    }

    private static Encoding DetectEncoding(byte[] buf, int n, bool partial)
    {
        if (n >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) return Encoding.UTF8;
        if (n >= 2 && buf[0] == 0xFF && buf[1] == 0xFE) return Encoding.Unicode;            // UTF-16 LE
        if (n >= 2 && buf[0] == 0xFE && buf[1] == 0xFF) return Encoding.BigEndianUnicode;   // UTF-16 BE

        // Сэмпл мог оборваться посреди многобайтового символа — не считать хвост ошибкой.
        var cut = n;
        if (partial)
        {
            var i = n - 1;
            while (i >= 0 && i >= n - 4 && (buf[i] & 0b1100_0000) == 0b1000_0000) i--;   // continuation-байты
            if (i >= 0 && buf[i] >= 0xC0)
            {
                var need = buf[i] >= 0xF0 ? 4 : buf[i] >= 0xE0 ? 3 : 2;
                if (i + need > n) cut = i;
            }
        }

        try
        {
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buf, 0, cut);
            return Encoding.UTF8;
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1251);
        }
    }

    // ── Прогресс ──────────────────────────────────────────────────────────────

    /// <summary>Прогресс «только вперед»: позиция ZIP-потока Excel между листами может прыгать
    /// назад — наружу уходят только возрастающие значения.</summary>
    public static IProgress<double>? ForwardOnly(IProgress<double>? inner) =>
        inner is null ? null : new ForwardProgress(inner);

    private sealed class ForwardProgress(IProgress<double> inner) : IProgress<double>
    {
        private double _best;

        public void Report(double value)
        {
            if (value <= _best) return;
            _best = value;
            inner.Report(value);
        }
    }
}

/// <summary>
/// Набор диапазонов номеров (исключаемые строки/столбцы): «4-9, 11, 14-28». Хранится списком
/// диапазонов, а не HashSet — диапазон вроде «1-1000000» не должен материализовывать миллион
/// чисел; Contains — линейный проход по немногим диапазонам (проверка на каждую строку дешевая).
/// </summary>
public sealed class RangeSet
{
    private readonly List<(long From, long To)> _ranges = [];

    public bool IsEmpty => _ranges.Count == 0;

    internal void Add(long from, long to) => _ranges.Add((from, to));

    public bool Contains(long value)
    {
        foreach (var (from, to) in _ranges)
            if (value >= from && value <= to)
                return true;
        return false;
    }
}
