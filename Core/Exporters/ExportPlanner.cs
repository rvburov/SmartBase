using System.Text;
using SmartBase.Core.Exporters.TableWriters;

namespace SmartBase.Core.Exporters;

/// <summary>Одна таблица юнита: коллекция и имя листа (Excel; CSV/JSON имя не используют).</summary>
public sealed record ExportTable(ExportCollectionRef Collection, string TableName);

/// <summary>Единица экспорта: один файл. Обычно таблица одна; в режиме «все коллекции в один
/// файл» (Excel) — лист на коллекцию. OpenWriter создаёт файл — вызывается лениво,
/// в начале обработки юнита (fail-fast прав/диска до чтения данных).</summary>
public sealed record ExportUnit(string FilePath, IReadOnlyList<ExportTable> Tables, Func<ITableWriter> OpenWriter);

/// <summary>
/// Разворачивает заявку диалога в список юнитов «один файл — одна коллекция» (или один файл
/// с листами при SingleFile). Здесь же — санитайз имён (в названиях коллекций допустимы
/// \/:*?"&lt;&gt;|) и уникализация путей «Имя (2).xlsx» против диска И между юнитами
/// (коллекции разных БД могут совпадать по имени). Вызывать в фоне: трогает диск.
/// Зеркало <see cref="Importers.ImportPlanner"/>.
/// </summary>
public static class ExportPlanner
{
    /// <summary>Для текстов ошибок и подсказок.</summary>
    public const string SupportedList = ".xlsx, .xlsm, .csv, .json";

    public static string Extension(ExportFormat f) => f switch
    {
        ExportFormat.Xlsx => ".xlsx",
        ExportFormat.Xlsm => ".xlsm",
        ExportFormat.Csv  => ".csv",
        _                 => ".json",
    };

    public static bool IsExcel(ExportFormat f) => f is ExportFormat.Xlsx or ExportFormat.Xlsm;

    public static IReadOnlyList<ExportUnit> Plan(ExportSpec spec)
    {
        if (spec.Collections.Count == 0)
            throw new ExportException("Не выбрано ни одной коллекции.");

        var ext   = Extension(spec.Format);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var units = new List<ExportUnit>();

        if (spec.SingleFile && IsExcel(spec.Format))
        {
            var baseName = SanitizeFileName(string.IsNullOrWhiteSpace(spec.FileName) ? "Экспорт" : spec.FileName!);
            var path     = UniquePath(spec.Folder, baseName, ext, taken);
            var used     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tables   = spec.Collections
                .Select(c => new ExportTable(c, UniqueSheetName(SanitizeSheetName(c.CollectionName), used)))
                .ToList();
            units.Add(new ExportUnit(path, tables, () => OpenWriter(spec.Format, path)));
            return units;
        }

        var single = spec.Collections.Count == 1;
        foreach (var c in spec.Collections)
        {
            var baseName = SanitizeFileName(single && !string.IsNullOrWhiteSpace(spec.FileName)
                ? spec.FileName!
                : c.CollectionName);
            var path  = UniquePath(spec.Folder, baseName, ext, taken);
            var sheet = single && !string.IsNullOrWhiteSpace(spec.SheetName)
                ? SanitizeSheetName(spec.SheetName!)
                : SanitizeSheetName(c.CollectionName);
            units.Add(new ExportUnit(path, [new ExportTable(c, sheet)], () => OpenWriter(spec.Format, path)));
        }
        return units;
    }

    private static ITableWriter OpenWriter(ExportFormat format, string path) => format switch
    {
        ExportFormat.Xlsx => new XlsxTableWriter(path, macroEnabled: false),
        ExportFormat.Xlsm => new XlsxTableWriter(path, macroEnabled: true),
        ExportFormat.Csv  => new CsvTableWriter(path),
        _                 => new JsonTableWriter(path),
    };

    // ── Имена ────────────────────────────────────────────────────────────────

    /// <summary>Имена устройств Windows: файл «CON.csv» не создаётся/ведёт себя странно.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Недопустимые символы → «_», крайние точки/пробелы срезаются, длина ≤100,
    /// пусто → «Коллекция», имена устройств получают префикс «_».</summary>
    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(ch < ' ' || invalid.Contains(ch) ? '_' : ch);

        var s = sb.ToString().Trim(' ', '.');
        if (s.Length > 100) s = s[..100].Trim(' ', '.');
        if (s.Length == 0) return "Коллекция";
        return ReservedNames.Contains(s) ? "_" + s : s;
    }

    /// <summary>Символы, запрещённые в именах листов Excel ([]:*?/\), → «_»; апострофы по краям
    /// срезаются (Excel не даёт), длина ≤31, пусто → «Лист1».</summary>
    public static string SanitizeSheetName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(ch < ' ' || ch is '[' or ']' or ':' or '*' or '?' or '/' or '\\' ? '_' : ch);

        var s = sb.ToString().Trim().Trim('\'').Trim();
        if (s.Length > 31) s = s[..31].TrimEnd();
        return s.Length == 0 ? "Лист1" : s;
    }

    /// <summary>Свободный путь «Имя.ext» → «Имя (2).ext» → … против диска и набора taken
    /// (уже спланированных юнитов); выбранный путь добавляется в taken.</summary>
    public static string UniquePath(string folder, string baseName, string ext, HashSet<string> taken)
    {
        for (var n = 1; ; n++)
        {
            var file = n <= 1 ? baseName + ext : $"{baseName} ({n}){ext}";
            var path = Path.Combine(folder, file);
            if (taken.Contains(path) || File.Exists(path)) continue;
            taken.Add(path);
            return path;
        }
    }

    /// <summary>«Имя (2)», «Имя (3)»… — уникализация имён ЛИСТОВ между коллекциями одного
    /// файла (продолжения «Имя 2» при переполнении делает сам писатель).</summary>
    private static string UniqueSheetName(string baseName, HashSet<string> used)
    {
        if (used.Add(baseName)) return baseName;
        for (var n = 2; ; n++)
        {
            var suffix    = $" ({n})";
            var candidate = (baseName.Length + suffix.Length <= 31
                ? baseName
                : baseName[..(31 - suffix.Length)].TrimEnd()) + suffix;
            if (used.Add(candidate)) return candidate;
        }
    }

    /// <summary>Имена свойств через запятую/точку с запятой; несовпавшие молча игнорируются
    /// (выбор может охватывать коллекции с разными наборами полей).</summary>
    public static HashSet<string> ParseColumnExclusions(string? raw)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return set;
        foreach (var part in raw.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            set.Add(part);
        return set;
    }
}
