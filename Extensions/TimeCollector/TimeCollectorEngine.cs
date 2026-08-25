using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Serilog;
using SmartBase.Utils;

namespace SmartBase.Extensions.TimeCollector;

/// <summary>Ошибка сборки свода с дружелюбным текстом — показывается в статусе окна.
/// <paramref name="inner"/> — исходный сбой: текст пользователю дружелюбный, но в logs.db
/// должна попасть настоящая причина со стеком, иначе разбирать нечего.</summary>
public sealed class TimeCollectException(string message, Exception? inner = null)
    : Exception(message, inner);

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
    /// <summary>Открыт другим пользователем прямо сейчас — прочитан с диска, то есть без его
    /// несохраненных правок. Рядом лежит служебный «~$»-файл, и книга ДЕЙСТВИТЕЛЬНО занята
    /// (проверено попыткой открыть ее монопольно).</summary>
    OpenedByOther,
    /// <summary>Рядом лежит служебный «~$»-файл, но книга свободна: маркер остался от прошлого
    /// сеанса (Excel уронили) или приехал вместе с копией папки. На данные не влияет —
    /// замечанием не считается (см. <see cref="FileIssueRank.IsProblem"/>), в журнал идет одной
    /// строкой со счетчиком.</summary>
    StaleLock,
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

/// <summary>Замечание ли это на самом деле. Осиротевший «~$» (<see cref="FileStatus.StaleLock"/>)
/// — нет: строки такого файла в своде полны и свежи, и в счетчиках прогона он не участвует,
/// иначе полсотни таких «замечаний» прятали бы настоящие.</summary>
public static class FileIssueRank
{
    public static bool IsProblem(this FileIssue issue) => issue.Status != FileStatus.StaleLock;
}

/// <summary>Прогресс прогона. <paramref name="Fraction"/> <c>null</c> — фаза без известного
/// объема работы (полоса переводится в бегущий режим, чтобы не выглядеть застывшей).</summary>
public sealed record CollectProgress(double? Fraction, string Phase, string Info);

/// <summary>Итог: путь к созданному файлу, счетчики и список замечаний для журнала.</summary>
public sealed record CollectResult(
    string OutputPath, int TotalFiles, int ProcessedFiles, int RowCount, IReadOnlyList<FileIssue> Issues);

/// <summary>
/// Прочитанный свод — общая часть обоих способов выгрузки (файл Excel и коллекция программы):
/// строки в конечном порядке, план их раскраски и счетчики с замечаниями. Все, что зависит от
/// приемника (шаблон, скрытые колонки, поля коллекции), считается уже после.
/// </summary>
public sealed record CollectedTable(
    List<TimeRow> Rows, TimeCollectorEngine.RowPaint[] Paints,
    int TotalFiles, int ProcessedFiles, IReadOnlyList<FileIssue> Issues);

/// <summary>
/// Сборка сводной таблицы трудоемкости — логика без UI (порт <c>TimeCollector.main</c>).
/// Чтение папки и сборку строк дает <see cref="Collect"/>; дальше свод либо пишется в
/// .xlsm-шаблон (<see cref="Run"/> — порт макроса целиком), либо становится коллекцией
/// программы (<see cref="TimeCollectionWriter"/>). Окно зовет нужную ветку, репортя прогресс
/// и отменяясь по токену.
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

    /// <summary>
    /// Сколько файлов читаем одновременно. На сетевой шаре читатель почти все время ждет
    /// ввод-вывод, а не считает, поэтому потоков имеет смысл держать БОЛЬШЕ, чем ядер: пока
    /// одни ждут шару, другие разбирают уже полученное. Прежняя жесткая четверка на машине
    /// с восемью ядрами оставляла половину простаивать.
    ///
    /// Потолок в 16 — не про процессор, а про шару и память: каждый читатель держит открытый
    /// файл и разобранную таблицу общих строк книги, и полсотни одновременных чтений сетевую
    /// папку скорее душат, чем ускоряют.
    /// </summary>
    private static readonly int MaxReaders = Math.Clamp(Environment.ProcessorCount * 2, 4, 16);

    // Границы фаз на шкале прогресса ВЫГРУЗКИ В EXCEL: чтение файлов — самая долгая часть на
    // сетевой папке, запись свода — на больших сводах. Отдельной фазы сохранения нет: писатель
    // потоковый и пишет сразу в результат. У выгрузки в коллекцию свои границы
    // (см. TimeCollectionWriter): фаз там другое число и стоят они иначе.
    private const double ReadTo  = 0.60;
    private const double ClearTo = 0.65;
    private const double WriteTo = 0.99;

    /// <summary>
    /// Прочитать папку с расчетами и собрать свод — общая часть обеих выгрузок. Дальше строки
    /// либо пишутся в .xlsm-шаблон (<see cref="Run"/>), либо становятся записями коллекции
    /// (<see cref="TimeCollectionWriter"/>). <paramref name="readTo"/> — доля шкалы прогресса,
    /// которую занимает чтение: остаток шкалы делит между своими фазами уже приемник.
    /// </summary>
    public static CollectedTable Collect(
        string folder, bool recursive, IProgress<CollectProgress>? progress, CancellationToken ct,
        double readTo = ReadTo)
    {
        if (!Directory.Exists(folder)) throw new TimeCollectException("Папка с расчетами не найдена.");

        progress?.Report(new CollectProgress(null, "Поиск файлов…", folder));
        var files = FindCalcFiles(folder, recursive);
        if (files.Count == 0)
            throw new TimeCollectException("В папке не найдено файлов расчетов (*.xlsm).");

        var results = ReadAll(files, progress, ct, readTo);

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
            var problems = issues.Where(i => i.IsProblem()).ToList();
            var reasons  = string.Join("\n", problems.Take(3).Select(i => $"• {i.FileName} — {i.Message}"));
            throw new TimeCollectException(
                $"Ни в одном файле не нашлось подходящих строк.\n" +
                $"Просмотрено файлов: {files.Count}, с замечаниями: {problems.Count}." +
                (reasons.Length > 0 ? $"\n{reasons}" : ""));
        }

        var ordered = Sort(rows);
        return new CollectedTable(ordered, PlanColors(ordered), files.Count, processed, issues);
    }

    /// <summary>Собрать свод и записать его в .xlsm-шаблон — выгрузка в файл Excel.</summary>
    public static CollectResult Run(
        string folder, bool recursive, string templatePath, string outputDir,
        IProgress<CollectProgress>? progress, CancellationToken ct)
    {
        // Приемник проверяем до долгого чтения папки: узнать про недоступный шаблон через
        // десять минут обхода сетевой шары — худшее из возможного.
        if (!File.Exists(templatePath))   throw new TimeCollectException("Файл-шаблон не найден.");
        if (!Directory.Exists(outputDir)) throw new TimeCollectException("Папка для сохранения не найдена.");

        var table   = Collect(folder, recursive, progress, ct);
        var ordered = table.Rows;
        var paints  = table.Paints;
        var hidden  = HiddenColumns(ordered);

        ct.ThrowIfCancellationRequested();

        // Результат пишется СРАЗУ в свой файл, а не собирается в памяти и сохраняется в конце:
        // писатель потоковый (см. TimeSheetWriter), и отдельной фазы «Сохранение…» у него нет.
        var outputPath = FreePath(outputDir, $"Трудоемкость ({DateTime.Now:yyyy-MM-dd_HH-mm})", ".xlsm");
        try
        {
            TimeSheetWriter.Write(templatePath, outputPath, ordered, paints, hidden,
                                  progress, ct, ClearTo, WriteTo);
        }
        catch (TimeCollectException)                { Discard(outputPath); throw; }
        catch (OperationCanceledException)          { Discard(outputPath); throw; }
        catch (Exception ex)
        {
            Discard(outputPath);
            Log.Error(ex, "Сводная трудоемкость: свод не записан. Результат {Output:l}, шаблон {Template:l}",
                      outputPath, templatePath);
            throw new TimeCollectException(SaveFailure(ex, templatePath, outputPath), ex);
        }

        progress?.Report(new CollectProgress(1, "Готово", ""));
        return new CollectResult(outputPath, table.TotalFiles, table.ProcessedFiles,
                                 ordered.Count, table.Issues);
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
        List<string> files, IProgress<CollectProgress>? progress, CancellationToken ct, double readTo)
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
                // Осиротевший «~$» в счетчик замечаний не идет: файл прочитан целиком и свежим.
                var issues = result.Issue?.IsProblem() == true
                    ? Interlocked.Increment(ref bad)
                    : Volatile.Read(ref bad);
                ReportRead(progress, eta, clock, count, files.Count, result.FileName, issues, readTo);
            });
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Any(e => e is OperationCanceledException))
        {
            throw new OperationCanceledException(ct);
        }

        return results;
    }

    private static void ReportRead(IProgress<CollectProgress>? progress, EtaEstimator eta, Stopwatch clock,
                                   int done, int total, string file, int issues, double readTo)
    {
        if (progress is null) return;

        var fraction = total > 0 ? (double)done / total : 0;
        var info     = $"{done} из {total} · {file}";
        if (issues > 0) info += $" · с замечаниями: {issues}";

        // EtaEstimator хранит сглаженное среднее и зовется из четырех потоков — под замком.
        string? left;
        lock (eta) left = eta.Update(fraction, clock.Elapsed);
        if (left is not null) info += $" · {left}";

        progress.Report(new CollectProgress(readTo * fraction, "Чтение расчетов", info));
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
        var hidden = new HashSet<int> { TimeSheetWriter.ColRNum };
        if (rows.Count == 0) return hidden;

        if (rows.All(r => r.Name.Length == 0)) hidden.Add(TimeSheetWriter.ColName);
        if (rows.All(r => r.Deno.Length == 0)) hidden.Add(TimeSheetWriter.ColDeno);
        if (rows.All(r => r.Norm is null && r.NormText.Length == 0)) hidden.Add(TimeSheetWriter.ColNorm);
        if (rows.All(r => r.Efio.Length == 0)) hidden.Add(TimeSheetWriter.ColEfio);
        if (rows.All(r => r.Proj.Length == 0)) hidden.Add(TimeSheetWriter.ColProj);
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

    /// <summary>
    /// Почему не записался свод. Общий текст «проверьте права и что файл не открыт» верен не
    /// всегда: писатель ВСЕ ВРЕМЯ ЗАПИСИ читает шаблон (части книги копируются из него на лету),
    /// и обрыв связи с папкой шаблона выглядит как сбой сохранения. Поэтому после сбоя смотрим,
    /// что из двух путей пропало, — вопрос «куда смотреть» решается прямо в сообщении.
    /// </summary>
    private static string SaveFailure(Exception ex, string templatePath, string outputPath)
    {
        var head = $"Не удалось сохранить результат:\n{outputPath}\n{ex.Message}";

        if (!File.Exists(templatePath))
            return $"{head}\n\nФайл-шаблон стал недоступен уже во время записи:\n{templatePath}\n" +
                   "Проверьте связь с папкой, где он лежит (сетевой диск отваливается на долгих " +
                   "прогонах), и что его не пересохранили в Excel. Копия шаблона на локальном " +
                   "диске такой обрыв исключает.";

        var dir = Path.GetDirectoryName(outputPath);
        if (dir is not null && !Directory.Exists(dir))
            return $"{head}\n\nПапка для результата исчезла во время записи:\n{dir}";

        return $"{head}\n\nПроверьте права на папку, свободное место и что файл не открыт в Excel. " +
               "Свод содержит макросы шаблона — его мог забрать антивирус.";
    }

    /// <summary>Убрать недописанный результат: писатель пишет прямо в файл, поэтому после
    /// отмены или ошибки на диске остался бы обрезанный .xlsm, который выглядит как готовый
    /// свод. Ошибку удаления глотаем — важнее показать исходную причину.</summary>
    private static void Discard(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }
}
