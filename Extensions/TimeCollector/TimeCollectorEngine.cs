using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using SmartBase.Utils;

namespace SmartBase.Extensions.TimeCollector;

/// <summary>Ошибка сборки свода с дружелюбным текстом — показывается в статусе окна.</summary>
public sealed class TimeCollectException(string message) : Exception(message);

/// <summary>
/// Строка свода. <paramref name="RNum"/> — номер строки исходного листа «Расшифровка» МИНУС
/// единица: ровно это число читает макрос шаблона (<c>row_num = Cells(row, COL_RNUM) + 1</c>),
/// когда открывает расчет по ссылке «&gt;&gt;&gt;».
/// </summary>
/// <param name="Norm">Трудоемкость числом; <c>null</c> — в ячейке был нечисловой текст
/// (он сохранен в <paramref name="NormText"/> и пишется в свод как есть).</param>
public sealed record TimeRow(
    int RNum, string Name, string Deno, double? Norm, string NormText,
    DateTime Date, string Efio, string Proj, string SourcePath);

/// <summary>Разобранное имя файла расчета «Проект_ГГГГ.ММ.ДД_ФИО.xlsm».</summary>
public sealed record CalcFileName(string Proj, DateTime Date, string Efio);

/// <summary>Что случилось с файлом расчета. <see cref="Ok"/> в журнал не попадает.</summary>
public enum FileStatus
{
    /// <summary>Прочитан без замечаний.</summary>
    Ok,
    /// <summary>Открыт другим пользователем (рядом служебный «~$»-файл) — прочитан с диска.</summary>
    OpenedByOther,
    /// <summary>Заблокирован другим процессом — прочитать не удалось.</summary>
    Locked,
    /// <summary>Нет прав доступа.</summary>
    NoAccess,
    /// <summary>Поврежден или не является файлом Excel.</summary>
    Corrupted,
    /// <summary>Нет листа «Расшифровка».</summary>
    NoSheet,
    /// <summary>Имя не по образцу «Проект_ГГГГ.ММ.ДД_ФИО.xlsm».</summary>
    BadName,
    /// <summary>Лист есть, но подходящих строк в нем нет.</summary>
    NoRows,
}

/// <summary>Замечание по одному файлу — строка журнала.</summary>
public sealed record FileIssue(string FileName, FileStatus Status, string Message);

/// <summary>Прогресс прогона. <paramref name="Fraction"/> <c>null</c> — фаза без известного
/// объема работы (полоса переводится в бегущий режим, чтобы не выглядеть застывшей).</summary>
public sealed record CollectProgress(double? Fraction, string Phase, string Info);

/// <summary>Итог: путь к созданному файлу, счетчики и список замечаний для журнала.</summary>
public sealed record CollectResult(
    string OutputPath, int TotalFiles, int ProcessedFiles, int RowCount, IReadOnlyList<FileIssue> Issues);

/// <summary>
/// Сборка сводной таблицы трудоемкости — логика без UI (порт <c>TimeCollector.main</c>).
/// Окно вызывает <see cref="Run"/>, репортя прогресс и отменяясь по токену.
///
/// Отличия от исходного макроса — намеренные, перечислены в
/// <c>docs/developer/features/extensions.md</c>: сортируются ВСЕ строки (в макросе из-за
/// <c>Header:=xlYes</c> первая строка данных оставалась несортированной), а файл, открытый
/// другим пользователем, читается с диска и помечается в журнале, а не пропускается целиком.
/// </summary>
public static class TimeCollectorEngine
{
    /// <summary>Лист шаблона, в который пишется свод.</summary>
    public const string TargetSheet = "Таблица";

    /// <summary>Сколько файлов читаем одновременно: на сетевой шаре ожидание диска тянется
    /// дольше разбора, поэтому четыре читателя дают кратный выигрыш, не выедая память.</summary>
    private const int MaxReaders = 4;

    // Границы фаз на шкале прогресса: чтение файлов — самая долгая часть на сетевой папке,
    // запись через ClosedXML — на больших сводах.
    private const double ReadTo   = 0.60;
    private const double ClearTo  = 0.65;
    private const double WriteTo  = 0.90;
    private const double FormatTo = 0.97;

    public static CollectResult Run(
        string folder, bool recursive, string templatePath, string outputDir,
        IProgress<CollectProgress>? progress, CancellationToken ct)
    {
        if (!Directory.Exists(folder))    throw new TimeCollectException("Папка с расчетами не найдена.");
        if (!File.Exists(templatePath))   throw new TimeCollectException("Файл-шаблон не найден.");
        if (!Directory.Exists(outputDir)) throw new TimeCollectException("Папка для сохранения не найдена.");

        progress?.Report(new CollectProgress(null, "Поиск файлов…", folder));
        var files = FindCalcFiles(folder, recursive);
        if (files.Count == 0)
            throw new TimeCollectException("В папке не найдено файлов расчетов (*.xlsm).");

        var results = ReadAll(files, progress, ct);

        var rows      = new List<TimeRow>();
        var issues    = new List<FileIssue>();
        var processed = 0;
        foreach (var result in results)
        {
            if (result is null) continue;                       // отменили до чтения этого файла
            if (result.Issue is not null) issues.Add(result.Issue);
            if (result.Rows.Count == 0) continue;
            rows.AddRange(result.Rows);
            processed++;
        }

        // Собирать нечего — в текст ошибки идут первые причины: без них остается гадать,
        // папка не та или файлы не читаются (полный разбор — в журнале окна).
        if (rows.Count == 0)
        {
            var reasons = string.Join("\n", issues.Take(3).Select(i => $"• {i.FileName} — {i.Message}"));
            throw new TimeCollectException(
                $"Ни в одном файле не нашлось подходящих строк.\n" +
                $"Просмотрено файлов: {files.Count}, с замечаниями: {issues.Count}." +
                (reasons.Length > 0 ? $"\n{reasons}" : ""));
        }

        var ordered = Sort(rows);
        var paints  = PlanColors(ordered);
        var hidden  = HiddenColumns(ordered);

        ct.ThrowIfCancellationRequested();
        progress?.Report(new CollectProgress(null, "Открываю шаблон…", Path.GetFileName(templatePath)));
        using var wb = OpenTemplate(templatePath);
        if (!wb.Worksheets.TryGetWorksheet(TargetSheet, out var ws))
            throw new TimeCollectException($"Лист «{TargetSheet}» не найден в файле-шаблоне.");

        TimeTableWriter.Fill(wb, ws, ordered, paints, hidden, progress, ct, ClearTo, WriteTo, FormatTo);

        progress?.Report(new CollectProgress(null, "Сохранение…", ""));
        var outputPath = FreePath(outputDir, $"Трудоемкость ({DateTime.Now:yyyy-MM-dd_HH-mm})", ".xlsm");
        try { wb.SaveAs(outputPath); }
        catch (Exception ex)
        {
            throw new TimeCollectException(
                $"Не удалось сохранить результат:\n{ex.Message}\n" +
                "Проверьте права на папку и что файл не открыт в Excel.");
        }

        progress?.Report(new CollectProgress(1, "Готово", ""));
        return new CollectResult(outputPath, files.Count, processed, ordered.Count, issues);
    }

    // ── Обход папки ───────────────────────────────────────────────────────────

    /// <summary>Файлы расчетов в папке: только <c>*.xlsm</c> и только имена БЕЗ «$» — то же
    /// условие, что в макросе (оно же отсекает служебные «~$»-файлы Excel). Скрытые и системные
    /// файлы пропускаются, недоступные подпапки обход не роняют.</summary>
    public static List<string> FindCalcFiles(string root, bool recursive)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible    = true,
            AttributesToSkip      = FileAttributes.Hidden | FileAttributes.System,
        };

        var files = new List<string>();
        foreach (var path in Directory.EnumerateFiles(root, "*", options))
        {
            var name = Path.GetFileName(path);
            if (name.Contains('$')) continue;
            if (!name.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase)) continue;
            files.Add(path);
        }

        files.Sort(StringComparer.CurrentCultureIgnoreCase);
        return files;
    }

    // ── Параллельное чтение ───────────────────────────────────────────────────

    // Результат по каждому файлу кладется в массив ПО ИНДЕКСУ файла: порядок сборки свода
    // и журнала не зависит от того, какой поток закончил раньше.
    private static CalcFileReader.FileReadResult?[] ReadAll(
        List<string> files, IProgress<CollectProgress>? progress, CancellationToken ct)
    {
        var results = new CalcFileReader.FileReadResult?[files.Count];
        var eta     = new EtaEstimator();
        var clock   = Stopwatch.StartNew();
        var done    = 0;
        var bad     = 0;

        var options = new ParallelOptions { MaxDegreeOfParallelism = MaxReaders, CancellationToken = ct };
        try
        {
            Parallel.For(0, files.Count, options, i =>
            {
                var result = CalcFileReader.Read(files[i], ct);
                results[i] = result;

                var count  = Interlocked.Increment(ref done);
                var issues = result.Issue is null ? Volatile.Read(ref bad) : Interlocked.Increment(ref bad);
                ReportRead(progress, eta, clock, count, files.Count, result.FileName, issues);
            });
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Any(e => e is OperationCanceledException))
        {
            throw new OperationCanceledException(ct);
        }

        return results;
    }

    private static void ReportRead(IProgress<CollectProgress>? progress, EtaEstimator eta, Stopwatch clock,
                                   int done, int total, string file, int issues)
    {
        if (progress is null) return;

        var fraction = total > 0 ? (double)done / total : 0;
        var info     = $"{done} из {total} · {file}";
        if (issues > 0) info += $" · с замечаниями: {issues}";

        // EtaEstimator хранит сглаженное среднее и зовется из четырех потоков — под замком.
        string? left;
        lock (eta) left = eta.Update(fraction, clock.Elapsed);
        if (left is not null) info += $" · {left}";

        progress.Report(new CollectProgress(ReadTo * fraction, "Чтение расчетов", info));
    }

    // ── Разбор имени файла ────────────────────────────────────────────────────

    /// <summary>
    /// «Проект_ГГГГ.ММ.ДД_ФИО.xlsm» → проект, дата, ФИО. Разбор ровно как в макросе
    /// (<c>TimeCollector.GetData</c>): проект — до ПЕРВОГО подчеркивания, дата — между первым и
    /// вторым, ФИО — после ПОСЛЕДНЕГО подчеркивания до последней точки. Не разобралось —
    /// <c>null</c>: макрос в этом месте падал на преобразовании даты и ронял весь прогон.
    /// </summary>
    public static CalcFileName? ParseFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        var first = fileName.IndexOf('_');
        if (first <= 0) return null;

        var second = fileName.IndexOf('_', first + 1);
        if (second < 0) return null;

        var lastDot = fileName.LastIndexOf('.');
        var lastSep = fileName.LastIndexOf('_');
        if (lastDot <= lastSep) return null;

        var proj = fileName[..first].Trim();
        var efio = fileName[(lastSep + 1)..lastDot].Trim();
        if (proj.Length == 0 || efio.Length == 0) return null;

        var dateText = fileName[(first + 1)..second].Trim();
        // Основной вид — «ГГГГ.ММ.ДД» (его же собирает CalculationLink); «ДД.ММ.ГГГГ» принимаем
        // на случай ручного переименования — путаницы нет, год виден по длине части.
        if (!DateTime.TryParseExact(dateText, ["yyyy.MM.dd", "dd.MM.yyyy"],
                                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return null;

        return new CalcFileName(proj, date, efio);
    }

    // ── Порядок строк ─────────────────────────────────────────────────────────

    /// <summary>
    /// Порядок свода: обозначение ↑, дата ↓, наименование ↑ — те же три ключа, что у
    /// <c>ApplyFastFormatting</c>. Текст сравнивается как в Excel (регистронезависимо, с учетом
    /// языка), в отличие от группировки при раскраске: там равенство побайтовое, как у
    /// оператора «=» в VBA.
    /// </summary>
    public static List<TimeRow> Sort(IEnumerable<TimeRow> rows) =>
        rows.OrderBy(r => r.Deno, StringComparer.CurrentCultureIgnoreCase)
            .ThenByDescending(r => r.Date)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    // ── Раскраска ─────────────────────────────────────────────────────────────

    /// <summary>Заливка строки свода.</summary>
    public enum RowFill
    {
        /// <summary>Светло-желтый — «четная» группа обозначения (ColorIndex 19 в макросе).</summary>
        Yellow,
        /// <summary>Белый — «нечетная» группа и строки без обозначения (ColorIndex 2).</summary>
        White,
        /// <summary>Красный — расхождение трудоемкости при совпавших проекте и дате (ColorIndex 3).</summary>
        Red,
    }

    /// <summary>План раскраски одной строки: заливка строки целиком и отдельная пометка ячейки
    /// «Тр-ть» (расхождение при РАЗНЫХ проекте или дате красит только ее).</summary>
    public sealed record RowPaint(RowFill Fill, bool NormRed);

    /// <summary>
    /// План раскраски свода — порт <c>SameProductsSameColor</c> + <c>SameNormColor</c>:
    /// <list type="bullet">
    /// <item>цвет чередуется на каждой смене обозначения; строка без обозначения — белая;</item>
    /// <item>у соседей с ОДИНАКОВЫМ обозначением и РАЗНОЙ трудоемкостью: совпали проект и дата —
    /// обе строки красные, иначе красными становятся только их ячейки «Тр-ть».</item>
    /// </list>
    /// Красный только добавляется и никогда не снимается, поэтому итог считается за один проход.
    /// <c>OperationsColor</c> не переносится: его цикл <c>For col = 10 To 8</c> при восьми
    /// колонках не выполняется ни разу.
    /// </summary>
    public static RowPaint[] PlanColors(IReadOnlyList<TimeRow> rows)
    {
        var count = rows.Count;
        if (count == 0) return [];

        var fills   = new RowFill[count];
        var normRed = new bool[count];

        var deno  = rows[0].Deno;
        var color = RowFill.Yellow;
        fills[0]  = deno.Length > 0 ? RowFill.Yellow : RowFill.White;

        for (var i = 1; i < count; i++)
        {
            var next = rows[i].Deno;

            if (deno.Length > 0)
            {
                var same = string.Equals(deno, next, StringComparison.Ordinal);
                if (!same) color = color == RowFill.Yellow ? RowFill.White : RowFill.Yellow;
                fills[i] = color;

                if (same && Norm(rows[i]) != Norm(rows[i - 1]))
                    MarkNormDiff(rows, i, fills, normRed);
            }
            else
            {
                fills[i] = RowFill.White;
            }

            deno = next;
        }

        var plan = new RowPaint[count];
        for (var i = 0; i < count; i++) plan[i] = new RowPaint(fills[i], normRed[i]);
        return plan;
    }

    // Нечисловая трудоемкость в макросе не переживала CDbl и шла в сравнение нулем.
    private static double Norm(TimeRow row) => row.Norm ?? 0;

    private static void MarkNormDiff(IReadOnlyList<TimeRow> rows, int i, RowFill[] fills, bool[] normRed)
    {
        var prev = rows[i - 1];
        var curr = rows[i];

        if (prev.Proj.Length == 0 || curr.Proj.Length == 0) return;
        if (prev.Date == default || curr.Date == default) return;

        if (string.Equals(prev.Proj, curr.Proj, StringComparison.Ordinal) && prev.Date == curr.Date)
        {
            fills[i]     = RowFill.Red;
            fills[i - 1] = RowFill.Red;
        }
        else
        {
            normRed[i]     = true;
            normRed[i - 1] = true;
        }
    }

    // ── Пустые колонки ────────────────────────────────────────────────────────

    /// <summary>
    /// Колонки свода, которые надо скрыть (порт <c>HideEmptyColumns</c>): те, где во всех
    /// строках пусто, плюс всегда служебная первая — в ней лежит номер строки исходного листа
    /// для перехода по ссылке.
    /// </summary>
    public static IReadOnlySet<int> HiddenColumns(IReadOnlyList<TimeRow> rows)
    {
        var hidden = new HashSet<int> { TimeTableWriter.ColRNum };
        if (rows.Count == 0) return hidden;

        if (rows.All(r => r.Name.Length == 0)) hidden.Add(TimeTableWriter.ColName);
        if (rows.All(r => r.Deno.Length == 0)) hidden.Add(TimeTableWriter.ColDeno);
        if (rows.All(r => r.Norm is null && r.NormText.Length == 0)) hidden.Add(TimeTableWriter.ColNorm);
        if (rows.All(r => r.Efio.Length == 0)) hidden.Add(TimeTableWriter.ColEfio);
        if (rows.All(r => r.Proj.Length == 0)) hidden.Add(TimeTableWriter.ColProj);
        return hidden;
    }

    /// <summary>
    /// Свободное имя в папке результата: занято — добавляется « (2)», « (3)»… Имя содержит
    /// время с точностью до минуты, а прогон на сетевой папке идет минутами: второй запуск
    /// в ту же минуту не должен молча затереть первый свод (тот же прием, что у имени
    /// коллекции в «Документах в коллекцию»).
    /// </summary>
    private static string FreePath(string dir, string baseName, string extension)
    {
        var path = Path.Combine(dir, baseName + extension);
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(dir, $"{baseName} ({i}){extension}");
        return path;
    }

    // ── Шаблон ────────────────────────────────────────────────────────────────

    private static XLWorkbook OpenTemplate(string path)
    {
        try { return new XLWorkbook(path); }
        catch (Exception ex)
        {
            throw new TimeCollectException(
                $"Не удалось открыть файл-шаблон:\n{Path.GetFileName(path)}\n{ex.Message}\n" +
                "Проверьте, что это корректный Excel (.xlsm) и он не открыт в другой программе.");
        }
    }
}
