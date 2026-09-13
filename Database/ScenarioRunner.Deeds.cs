using System.Globalization;
using SmartBase.Core.Exporters;
using SmartBase.Core.Importers;
using SmartBase.Core.Importers.TableSources;
using SmartBase.Core.Models;
using SmartBase.Extensions;
using SmartBase.Integrations;

namespace SmartBase.Database;

/// <summary>
/// Шаги «создать запись» и «экспорт выборки» (docs/ROADMAP-API.md, шаг 11) — исполнители,
/// которых ждали триггер «календарное событие» и полный набор действий над данными, — и
/// действие-автоматизация «строки пришли → строки ушли» (шаг 13), которое умеет вместо строк
/// отдать оболочке готовый файл.
/// </summary>
public static partial class ScenarioRunner
{
    // ── Действие-автоматизация: строки → строки или файл ──────────────────────

    /// <summary>
    /// Автоматизация расширения над ВСЕЙ таблицей предыдущего шага (docs/ROADMAP-API.md, шаг 13).
    ///
    /// Отличие от действия-операции — в единице работы: операция идет по разу на строку, а
    /// автоматизации нужна вся выгрузка разом («свести по проектам» нельзя сделать построчно).
    /// Отличие от преобразователя — во входе: тому нужен текст файла, этой — строки.
    ///
    /// В базу автоматизация не пишет и подключения не получает: <see cref="ActionContext.Repo"/>
    /// у нее пуст ровно по той же причине, что у преобразователя, — спрашивать права на запись
    /// ради пересчета строк незачем.
    ///
    /// <b>Файл кладет ОБОЛОЧКА.</b> Расширение собирает байты и придумывает имя, но не касается
    /// диска: файл наружу — тот же транспорт, что и сеть, и дверь у него одна. Папку разрешает
    /// политика, след в журнале безопасности обязателен (<see cref="Keep"/>).
    /// </summary>
    private static async Task<StepOutcome> AutomationStepAsync(ScenarioStep      step,
                                                               IExtensionAction  action,
                                                               StepTable?        incoming,
                                                               string            scenario,
                                                               DateTimeOffset    now,
                                                               CancellationToken ct)
    {
        if (incoming is not { } table) return StepOutcome.Bad(NoTable(action));

        if (table.Rows.Count > StepTable.MaxRows)
            return StepOutcome.Bad(
                $"«{action.Name}»: строк {table.Rows.Count}, а за один запуск их обрабатывается "
                + $"не больше {StepTable.MaxRows}");

        // Настройки шага — как есть: подстановки {{колонка}} тут не при чем. Поле заполняется
        // один раз на шаг, а строк много, и «колонка из какой именно» ответа не имеет.
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, value) in step.Fields) values[key] = value;

        if (Unfilled(step, action) is { } unfilled) return StepOutcome.Bad(unfilled);

        ActionResult result;
        try
        {
            result = await Task.Run(
                () => action.Run(new ActionContext(null, 0, 0, now),
                                 new ActionInput(values, Rows: table.Rows)), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            // Расширение обязано возвращать отказ результатом, но обязанность — не гарантия.
            return StepOutcome.Bad($"«{action.Name}» сорвался: {error.Message}");
        }

        if (!result.Ok) return StepOutcome.Bad($"«{action.Name}»: {result.Explanation}");

        if (result.File is { } bytes)
        {
            try
            {
                return await Task.Run(() => Keep(step, action, bytes, result, table, scenario, now), ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                return StepOutcome.Bad($"«{action.Name}»: файл не записался — "
                                     + ExportErrors.Map(error, step.Folder).Message);
            }
        }

        // Строк не осталось — это не неудача, а «делать дальше нечего»: то же прочтение, что у
        // отбора, не нашедшего ни одной подходящей строки.
        if (result.Json.Trim() is "" or "[]")
            return StepOutcome.Halt($"«{action.Name}»: {result.Explanation}; строк не осталось, "
                                  + "дальше незачем");

        var source = new JsonTextSource(result.Json);

        ImportSchema schema;
        try
        {
            schema = await Task.Run(() => source.Analyze(null, ct), ct);
        }
        catch (ImportException error)
        {
            return StepOutcome.Bad($"«{action.Name}» вернул строки, которые не разобрались: "
                                 + error.Message);
        }

        if (schema.RowCount > StepTable.MaxRows)
            return StepOutcome.Bad(
                $"«{action.Name}»: вернулось строк {schema.RowCount}, а дальше по сценарию их "
                + $"идет не больше {StepTable.MaxRows}");

        var rows = await Task.Run(() => source.ReadRows(ct).ToList(), ct);

        // Записей шаг не заводит, поэтому в счет сценария не идет: те же строки посчитаются на
        // шаге, который их куда-то положит.
        return new StepOutcome(true, $"Действие «{action.Name}»: {result.Explanation}", 0,
                               StepTable.Of(schema, rows));
    }

    /// <summary>
    /// Положить собранный расширением файл — через те же рубежи, что и «экспорт выборки»:
    /// папка из корней политики (10.11) и обязательный след в журнале безопасности.
    ///
    /// Метка конфиденциальности сюда не приходит, и это не упущение: в потоке шагов лежат
    /// строки, принесенные запросом, папкой обмена или триггером, а не прочитанные из
    /// коллекции, — рубеж меток стоит там, где данные ИЗ БАЗЫ выходят наружу
    /// (<c>ExportSourceIsolated</c>).
    /// </summary>
    private static StepOutcome Keep(ScenarioStep step, IExtensionAction action, byte[] bytes,
                                    ActionResult result, StepTable table,
                                    string scenario, DateTimeOffset now)
    {
        // Повторная проверка после Blocker: политику могли сменить, пока сценарий ждал тика.
        var folder = EgressGate.CheckFolder(step.Folder);
        if (!folder.Allowed) return StepOutcome.Bad($"«{action.Name}»: папка файла — "
                                                  + folder.Explanation);

        if (!ExportTrace.Ready) return StepOutcome.Bad(ExportTrace.NoJournal);

        Directory.CreateDirectory(step.Folder);

        var path = Path.Combine(step.Folder, Stamped(result.FileName, now));

        File.WriteAllBytes(path, bytes);

        var traced = new ExportedCollection(step.DbId, "строки сценария", table.Rows.Count,
                                            table.Columns, ConfidentialityLabel.Open);

        if (!ExportTrace.Note(path, [traced], now, scenario))
        {
            try { File.Delete(path); } catch { /* best-effort */ }
            return StepOutcome.Bad(ExportTrace.NotWritten);
        }

        return new StepOutcome(true,
            $"Действие «{action.Name}»: {result.Explanation} → {Path.GetFileName(path)}",
            result.Rows);
    }

    /// <summary>
    /// Имя файла с моментом: «Строки 2026-09-13 0830.xlsx». Момент добавляет оболочка, а не
    /// расширение, — по той же причине, что и в экспорте: выгрузка повторяется по расписанию, и
    /// вчерашний файл не должен молча пропадать под сегодняшним.
    ///
    /// Путь из имени вырезается целиком: расширение называет ФАЙЛ, а не место — место выбрано
    /// шагом и разрешено политикой, и «..\..\» в имени не должно уводить из него никуда.
    /// </summary>
    private static string Stamped(string wanted, DateTimeOffset now)
    {
        var name = Path.GetFileName((wanted ?? "").Trim());

        if (name.Length == 0) name = "Строки.xlsx";

        foreach (var forbidden in Path.GetInvalidFileNameChars())
            name = name.Replace(forbidden, '_');

        var stamp = now.ToLocalTime().ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture);

        return Path.GetFileNameWithoutExtension(name) + " " + stamp + Path.GetExtension(name);
    }

    /// <summary>Автоматизации нечего обрабатывать — строки приносит запрос, папка или триггер.</summary>
    private static string NoTable(IExtensionAction action) =>
        $"«{action.Name}» работает со строками, а строк нет: поставьте перед ним шаг, который их "
        + "приносит, — запрос в коллекцию или разбор файла";

    // ── Шаг «создать запись» ──────────────────────────────────────────────────

    /// <summary>
    /// Запись на каждую строку предыдущего шага; шагу без строк — одна запись из постоянных
    /// значений. Значения полей — выражения с подстановками <c>{{колонка}}</c>, и ВСЕ они
    /// считаются ДО первой записи, как у действия расширения: опечатка в имени колонки на
    /// сотой строке оставила бы девяносто девять записей и вопрос, какие из них лишние.
    /// </summary>
    private static async Task<StepOutcome> RecordStepAsync(ScenarioStep                   step,
                                                           IReadOnlyList<DatabaseManager> databases,
                                                           StepTable?                     incoming,
                                                           CancellationToken              ct)
    {
        if (databases.FirstOrDefault(d => d.DbId == step.DbId) is not { } target)
            return StepOutcome.Bad("база данных для создания записи сейчас не открыта");

        var table = incoming ?? StepTable.Empty;

        if (table.Rows.Count > StepTable.MaxRows)
            return StepOutcome.Bad(
                $"создание записи: строк {table.Rows.Count}, а за один запуск их заводится "
                + $"не больше {StepTable.MaxRows}");

        var rows = new List<IReadOnlyDictionary<string, string>>(table.Rows.Count);

        foreach (var row in table.Rows)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (field, expression) in step.Fields)
            {
                var value = ScenarioValues.Apply(expression, row, out var unknown);

                if (unknown.Count > 0)
                    return StepOutcome.Bad(
                        $"создание записи: в поле «{field}» названы колонки, которых нет: "
                        + string.Join(", ", unknown)
                        + (table.Columns.Count > 0
                            ? "; в строке есть: " + string.Join(", ", table.Columns)
                            : "; предыдущий шаг строк не передал"));

                values[field] = value;
            }

            rows.Add(values);
        }

        try
        {
            var made = await Task.Run(
                () => target.Constructor.WriteRecordsIsolated(
                    step.CollectionId, rows, ct, step.MatchField, step.CreateMissing), ct);

            var what = step.Matching ? "обновление записи" : "создание записи";

            if (made.Trouble is { } trouble)
                return StepOutcome.Bad(what + ": " + trouble);

            // В отчет идут все четыре цифры, а не одно «заведено»: пропущенные по пустому ключу
            // и неоднозначные — это и есть ответ на вопрос «почему записей меньше, чем строк».
            return new StepOutcome(true,
                char.ToUpperInvariant(what[0]) + what[1..]
                + $": {made.Describe()} в «{made.Collection}»",
                made.Created + made.Updated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            // Права человека за машиной и прочие беды базы — отказом словами, а не падением
            // службы сценариев.
            return StepOutcome.Bad(
                (step.Matching ? "обновление записи: " : "создание записи: ") + error.Message);
        }
    }

    // ── Шаг «экспорт выборки» ─────────────────────────────────────────────────

    /// <summary>
    /// Выборка коллекции в файл: формат пишут ГОТОВЫЕ писатели экспорта, папку разрешает
    /// политика (10.11), метки стоят вторым рубежом, а след в журнале безопасности обязателен —
    /// с той же оговоркой, что у ручного экспорта (<see cref="ExportTrace"/>): журнал
    /// спрашивается до файла, строка пишется после, не легла — файл удаляется.
    /// </summary>
    private static async Task<StepOutcome> ExportStepAsync(ScenarioStep                   step,
                                                           IReadOnlyList<DatabaseManager> databases,
                                                           string                         scenario,
                                                           DateTimeOffset                 now,
                                                           CancellationToken              ct)
    {
        if (databases.FirstOrDefault(d => d.DbId == step.DbId) is not { } target)
            return StepOutcome.Bad("база данных для выгрузки сейчас не открыта");

        // Повторная проверка после Blocker: политику могли сменить, пока сценарий ждал тика.
        var folder = EgressGate.CheckFolder(step.Folder);
        if (!folder.Allowed) return StepOutcome.Bad("папка выгрузки: " + folder.Explanation);

        if (!ExportTrace.Ready) return StepOutcome.Bad(ExportTrace.NoJournal);

        try
        {
            return await Task.Run(() => Export(step, target, scenario, now, ct), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            return StepOutcome.Bad("экспорт выборки: "
                                 + ExportErrors.Map(error, step.Folder).Message);
        }
    }

    /// <summary>Сама выгрузка — в фоне: чтение страницами, запись потоком.</summary>
    private static StepOutcome Export(ScenarioStep step, DatabaseManager target,
                                      string scenario, DateTimeOffset now, CancellationToken ct)
    {
        var source = target.Constructor.ExportSourceIsolated(step.CollectionId);

        if (source.Trouble is { } stopped) return StepOutcome.Bad("экспорт выборки: " + stopped);

        // Имена колонок отбора проверяются ДО файла — по той же причине, что у шага «отбор»:
        // опечатка, прочитанная как «пусто», выгрузила бы ноль строк и не сказала почему.
        var headers = source.Columns.Select(c => c.Header).ToList();

        var unknown = step.Tests.Select(t => t.Column)
                          .Where(c => !headers.Contains(c, StringComparer.OrdinalIgnoreCase))
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .ToList();

        if (unknown.Count > 0)
            return StepOutcome.Bad(
                "экспорт выборки: в отборе названы колонки, которых нет: "
                + string.Join(", ", unknown) + "; в коллекции есть: "
                + string.Join(", ", headers));

        var format = step.Format.Trim().ToLowerInvariant() switch
        {
            "csv"  => ExportFormat.Csv,
            "json" => ExportFormat.Json,
            _      => ExportFormat.Xlsx,
        };

        // Имя файла — коллекция и момент: выгрузка повторяется по расписанию, и вчерашний
        // файл не должен молча пропадать под сегодняшним. Совпадение имен разведет планер.
        var spec = new ExportSpec(
            step.Folder, format,
            [new ExportCollectionRef(step.DbId, step.CollectionId, source.Collection, target.Name)],
            FileName: source.Collection + " "
                    + now.ToLocalTime().ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture),
            Manual: false, SheetName: null, ExcludeColumns: "",
            NoHeaderRow: false, IncludeHidden: false, SingleFile: false);

        var unit = ExportPlanner.Plan(spec)[0];

        // Индексы колонок условий — один раз, а не на каждую строку.
        var tests = step.Tests
            .Select(t => (Test: t, At: headers.FindIndex(
                h => string.Equals(h, t.Column, StringComparison.OrdinalIgnoreCase))))
            .ToList();

        var kept        = 0;
        var lost        = 0;
        var fileCreated = false;

        try
        {
            using var writer = unit.OpenWriter();
            fileCreated = true;

            writer.BeginTable(unit.Tables[0].TableName, source.Columns, writeHeader: true);

            foreach (var row in target.Constructor.ReadExportRowsIsolated(
                         step.CollectionId, source.Columns, excludeHidden: true, ct))
            {
                if (!Fits(tests, row)) { lost++; continue; }

                writer.WriteRow(row);
                kept++;
            }

            writer.EndTable();
            writer.Finish();
        }
        catch
        {
            // Недописанный файл не оставляется: половина выгрузки хуже ее отсутствия.
            if (fileCreated) try { File.Delete(unit.FilePath); } catch { /* best-effort */ }
            throw;
        }

        // След — после файла (объем и отпечаток появляются только у готового), но без следа
        // файл не живет: не записалось — не выгружаем, дословно как у ручного экспорта.
        var traced = new ExportedCollection(step.DbId, source.Collection, kept,
                                            headers, source.Label);

        if (!ExportTrace.Note(unit.FilePath, [traced], now, scenario))
        {
            try { File.Delete(unit.FilePath); } catch { /* best-effort */ }
            return StepOutcome.Bad(ExportTrace.NotWritten);
        }

        var line = $"Экспорт «{source.Collection}»: строк {kept}"
                 + (lost > 0 ? $", отсеяно условиями {lost}" : "")
                 + $" → {Path.GetFileName(unit.FilePath)}";

        return new StepOutcome(true, line, kept);
    }

    /// <summary>Проходит ли строка выгрузки все условия отбора.</summary>
    private static bool Fits(IReadOnlyList<(ScenarioCondition Test, int At)> tests, string[] row)
    {
        foreach (var (test, at) in tests)
            if (!test.HoldsValue(at >= 0 && at < row.Length ? row[at] : ""))
                return false;

        return true;
    }
}
