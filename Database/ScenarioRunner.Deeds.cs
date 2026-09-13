using System.Globalization;
using SmartBase.Core.Exporters;
using SmartBase.Integrations;

namespace SmartBase.Database;

/// <summary>
/// Шаги «создать запись» и «экспорт выборки» (docs/ROADMAP-API.md, шаг 11) — исполнители,
/// которых ждали триггер «календарное событие» и полный набор действий над данными.
/// </summary>
public static partial class ScenarioRunner
{
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
                () => target.Constructor.CreateRecordsIsolated(step.CollectionId, rows, ct), ct);

            if (made.Trouble is { } trouble)
                return StepOutcome.Bad("создание записи: " + trouble);

            return new StepOutcome(true,
                $"Создание записи: заведено {made.Created} в «{made.Collection}»", made.Created);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            // Права человека за машиной и прочие беды базы — отказом словами, а не падением
            // службы сценариев.
            return StepOutcome.Bad("создание записи: " + error.Message);
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
