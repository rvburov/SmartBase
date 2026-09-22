using System.Globalization;
using System.IO;
using System.Text;
using ExcelDataReader;
using SmartBase.Core.Importers.TableSources;

namespace SmartBase.Extensions.TimeCollector;

/// <summary>
/// Список операций из «Операции.xlsx» — той же книги, из которой макросы расшифровки заполняют
/// лист «Операции» расчета при каждом его открытии (<c>Init.InitConst</c>). Колонка C строки
/// операции выбирается выпадающим списком из этого листа, поэтому названия в расчетах почти
/// всегда совпадают со списком.
///
/// Нужен колонкам по операциям в коллекции (<see cref="OperationColumns"/>): порядок колонок —
/// порядок листа «Операции», а лист «Исправление» (пары «старое название → правильное»)
/// сводит устаревшие названия в колонку правильного. Файл читается при каждом прогоне, так
/// что правка списка действует сразу.
///
/// В обоих листах первая строка — заголовок: макрос читал их с <c>HDR=yes</c>.
/// </summary>
public sealed class OperationCatalog
{
    /// <summary>Лист со списком операций: колонка A — название, B — вид работ.</summary>
    public const string ListSheet = "Операции";

    /// <summary>Лист исправлений: колонка A — старое название, B — правильное.</summary>
    public const string FixSheet = "Исправление";

    private readonly Dictionary<string, int>    _index = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _fixes = new(StringComparer.Ordinal);

    /// <summary>Названия в порядке листа, без повторов и лишних пробелов.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>
    /// Список из готовых названий. Повторы (в настоящем списке «Сборочная» встречается трижды)
    /// схлопываются в первое вхождение; пустые строки пропускаются.
    /// </summary>
    public OperationCatalog(IEnumerable<string> names, IEnumerable<(string From, string To)>? fixes = null)
    {
        var list = new List<string>();
        foreach (var raw in names)
        {
            var name = Clean(raw);
            if (name.Length == 0 || !_index.TryAdd(Key(name), list.Count)) continue;
            list.Add(name);
        }
        Names = list;

        foreach (var (from, to) in fixes ?? [])
        {
            var target = Clean(to);
            if (target.Length == 0 || Clean(from).Length == 0) continue;
            _fixes.TryAdd(Key(from), target);
        }
    }

    /// <summary>Место названия в списке или -1, если его там нет.</summary>
    public int IndexOf(string name) => _index.TryGetValue(Key(name), out var index) ? index : -1;

    /// <summary>Правильное название по листу «Исправление» или <c>null</c>, если исправления нет.
    /// Как и в макросе (<c>GetJob</c>), спрашивать стоит только про название, которого нет в
    /// списке: исправление — запасной путь, а не замена списка.</summary>
    public string? Fix(string name) => _fixes.TryGetValue(Key(name), out var target) ? target : null;

    /// <summary>
    /// Ключ сравнения названий: без лишних пробелов, без учета регистра и разницы «е»/«ё».
    /// Названия набираются и выпадающим списком, и руками, а в самом списке есть «приёмосдаточные»
    /// с «ё» — из-за таких мелочей одна операция не должна расходиться на две колонки.
    /// </summary>
    public static string Key(string name) =>
        Clean(name).ToLowerInvariant().Replace('ё', 'е');

    /// <summary>Название для показа: края обрезаны, пробелы внутри схлопнуты до одного (в списке
    /// есть «Сборочно-монтажная   » с хвостом пробелов).</summary>
    public static string Clean(string name)
    {
        var text = name.Trim();
        if (!text.Any(char.IsWhiteSpace)) return text;

        var sb    = new StringBuilder(text.Length);
        var space = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) { space = true; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    // ── Чтение книги ──────────────────────────────────────────────────────────

    /// <summary>
    /// Прочитать список из книги. Отказ — <see cref="TimeCollectException"/> с путем в тексте:
    /// список проверяется ДО чтения расчетов, и узнать про недоступный файл нужно сразу, а не
    /// через десять минут обхода папки. Нет листа «Исправление» — не беда: исправлений просто нет.
    /// </summary>
    public static OperationCatalog Load(string path)
    {
        if (!File.Exists(path))
            throw new TimeCollectException($"Файл списка операций не найден:\n{path}");

        List<string>? names = null;
        var fixes = new List<(string, string)>();

        try
        {
            // Тот же прием, что у чтения расчетов (см. CalcFileReader): кодировки для
            // ExcelDataReader и чтение книги, открытой в Excel, без конфликта.
            EncodingSetup.Ensure();
            using var fs     = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = ExcelReaderFactory.CreateReader(fs);

            do
            {
                if (IsSheet(reader, ListSheet))
                    names = ReadColumns(reader).Select(r => r.A).ToList();
                else if (IsSheet(reader, FixSheet))
                    fixes.AddRange(ReadColumns(reader));
            }
            while (reader.NextResult());
        }
        catch (Exception ex)
        {
            throw new TimeCollectException(
                $"Не удалось прочитать файл списка операций:\n{path}\n{ex.Message}", ex);
        }

        if (names is null)
            throw new TimeCollectException($"В файле списка операций нет листа «{ListSheet}»:\n{path}");

        var catalog = new OperationCatalog(names, fixes);
        if (catalog.Names.Count == 0)
            throw new TimeCollectException($"Лист «{ListSheet}» в файле списка операций пуст:\n{path}");

        return catalog;
    }

    private static bool IsSheet(IExcelDataReader reader, string name) =>
        string.Equals(reader.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase);

    // Колонки A и B листа, без первой строки — заголовка.
    private static IEnumerable<(string A, string B)> ReadColumns(IExcelDataReader reader)
    {
        var rows  = new List<(string, string)>();
        var first = true;
        while (reader.Read())
        {
            if (first) { first = false; continue; }
            if (reader.FieldCount == 0) continue;
            rows.Add((Cell(reader, 0), reader.FieldCount > 1 ? Cell(reader, 1) : ""));
        }
        return rows;
    }

    private static string Cell(IExcelDataReader reader, int column) =>
        Convert.ToString(reader.GetValue(column), CultureInfo.CurrentCulture) ?? "";
}
