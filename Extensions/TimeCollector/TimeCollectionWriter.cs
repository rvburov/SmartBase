using System.Globalization;
using System.Threading;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Extensions.TimeCollector;

/// <summary>Каркас созданной коллекции: ее id, итоговое имя и id пользовательских полей.</summary>
public sealed record TimeShell(
    int CollectionId, string CollectionName,
    int DenoFieldId, int NormFieldId, int DateFieldId,
    int EfioFieldId, int ProjFieldId, int FileFieldId);

/// <summary>
/// Выгрузка свода трудоемкости в коллекцию программы — второй приемник рядом с .xlsm-шаблоном
/// (<see cref="TimeTableWriter"/>). Строки и план раскраски приходят готовыми из
/// <see cref="TimeCollectorEngine.Collect"/>, здесь только запись в базу.
///
/// Шаги разнесены так же, как в расширении «Документы в коллекцию» (образец —
/// <c>DocsCollectionBuilder</c>), и по той же причине:
/// <list type="number">
/// <item><see cref="CreateShell"/> — коллекция и колонки на ОБЩЕМ репозитории (UI-поток: там же
/// срабатывает проверка права «Создавать коллекции»);</item>
/// <item><see cref="FillRecords"/> — записи, значения и раскраска на ОТДЕЛЬНОМ подключении в
/// фоне: общий контекст EF принадлежит UI-потоку и делить его с фоном нельзя.</item>
/// </list>
///
/// Схема коллекции повторяет колонки Excel-свода: «Наименование» — переименованная СИСТЕМНАЯ
/// колонка записи (Title), «Обозначение КД»/«ФИО»/«Проект» — текст, «Тр-ть» — число,
/// «Дата» — дата, «Файл расчета» — тип «файл» с путем к исходному расчету (в Excel его роль
/// играет колонка «Ссылка» с формулой HYPERLINK). Служебной колонки с номером строки
/// источника здесь нет: она нужна только макросу шаблона для перехода по «&gt;&gt;&gt;».
/// </summary>
public static class TimeCollectionWriter
{
    // Ключ системной колонки записи (та же строка, что в ConstructorRepository/TableRowVm).
    private const string TitleKey = "__title__";

    /// <summary>Подписи колонок создаваемой коллекции — те же, что в шапке Excel-свода.</summary>
    public const string TitleColumnName = "Наименование";
    public const string DenoFieldName   = "Обозначение КД";
    public const string NormFieldName   = "Тр-ть";
    public const string DateFieldName   = "Дата";
    public const string EfioFieldName   = "ФИО";
    public const string ProjFieldName   = "Проект";
    public const string FileFieldName   = "Файл расчета";

    /// <summary>Имя коллекции по умолчанию — как имя файла свода, с датой и временем прогона:
    /// два свода за день различимы, а занятое имя все равно получит номер (см. UniqueName).</summary>
    public static string DefaultCollectionName() =>
        $"Трудоемкость ({DateTime.Now:yyyy-MM-dd_HH-mm})";

    // Цвета раскраски — из палитры приложения (Core/Models/AppPalette): «Желтый» и «Красный».
    // Excel-свод красится своими ColorIndex-цветами макроса, а коллекция — теми же цветами,
    // которыми пользователь красит строки руками; отрисовка приглушает фон сама.
    private const string YellowHex = "#FFCC00";
    private const string RedHex    = "#FF3B30";

    // Прогресс постится в UI-поток, поэтому не на каждую запись: на десятках тысяч строк
    // поток сообщений тормозит диспетчер сильнее самой вставки.
    private const int ProgressEvery = 25;

    // Как часто чистить трекер изменений EF. Запись идет публичным API (AddRecord +
    // SetFieldValue), и контекст копит ВСЕ созданные сущности: без сброса DetectChanges на
    // каждом сохранении обходит растущий граф, и время заливки растет квадратично.
    private const int ClearTrackerEvery = 200;

    // Стили ставятся пачками: список id уходит в SQL параметрами (IN), и одним вызовом на
    // десятки тысяч записей запрос упирается в лимит переменных SQLite.
    private const int StyleBatch = 500;

    // ── 1. Каркас коллекции (общий репозиторий, UI-поток) ─────────────────────

    /// <summary>
    /// Создать коллекцию и ее колонки. Имя занято — добавляется « (2)», « (3)»… Любая ошибка
    /// после создания коллекции удаляет ее, чтобы не осталось полупустого каркаса.
    /// </summary>
    public static TimeShell CreateShell(ConstructorRepository repo, string collectionName)
    {
        var existing = repo.GetAllCollections();
        var taken    = existing.Select(c => c.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var name     = UniqueName(collectionName, taken);
        var position = existing.Count == 0 ? 1000 : existing.Max(c => c.Position) + 1000;

        var col   = repo.AddCollection(new DbCollection { Name = name, Position = position });
        var colId = col.Id!.Value;

        try
        {
            // Системную колонку записи переименовываем — физически это Title.
            repo.SetSystemFieldName(colId, TitleKey, TitleColumnName, "Название");

            var deno = Field(repo, colId, DenoFieldName, "text",   1000);
            var norm = Field(repo, colId, NormFieldName, "number", 2000);
            var date = Field(repo, colId, DateFieldName, "date",   3000);
            var efio = Field(repo, colId, EfioFieldName, "text",   4000);
            var proj = Field(repo, colId, ProjFieldName, "text",   5000);
            // Поле «файл» без правила авто-привязки (ConfigJson пуст): пути проставляем сами —
            // расчеты лежат в разных папках, а правило умеет ровно одну папку и один формат.
            var file = Field(repo, colId, FileFieldName, "file",   6000);

            // Порядок колонок — как в шапке Excel-свода.
            repo.SaveColumnOrder(colId,
                [TitleKey, deno.ToString(), norm.ToString(), date.ToString(),
                 efio.ToString(), proj.ToString(), file.ToString()]);

            return new TimeShell(colId, name, deno, norm, date, efio, proj, file);
        }
        catch
        {
            TryDelete(repo, colId);
            throw;
        }
    }

    private static int Field(ConstructorRepository repo, int colId, string name, string type, int position) =>
        repo.AddField(new DbField
        {
            CollectionId = colId, Name = name, FieldType = type, Position = position,
        }).Id!.Value;

    private static string UniqueName(string baseName, HashSet<string> taken)
    {
        var name = string.IsNullOrWhiteSpace(baseName) ? DefaultCollectionName() : baseName.Trim();
        if (!taken.Contains(name)) return name;
        for (var i = 2; ; i++)
        {
            var candidate = $"{name} ({i})";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    /// <summary>Убрать недособранную коллекцию (отмена/ошибка). Ошибку удаления глотаем —
    /// важнее показать исходную причину.</summary>
    public static void TryDelete(ConstructorRepository repo, int collectionId)
    {
        try { repo.DeleteCollectionIsolated(collectionId); } catch { /* best-effort */ }
    }

    // ── 2. Записи и раскраска (отдельное подключение, фон) ─────────────────────

    /// <summary>
    /// Наполнить коллекцию строками свода на СВОЕМ подключении к <paramref name="dbPath"/> и
    /// разложить раскраску. Порядок записей — порядок свода (обозначение ↑, дата ↓,
    /// наименование ↑), позиции с шагом 1000: между строками остается место для вставки.
    /// Возвращает число созданных записей; отмена бросает <see cref="OperationCanceledException"/>
    /// (вызывающий убирает коллекцию через <see cref="TryDelete"/>).
    /// <paramref name="from"/>…<paramref name="to"/> — участок шкалы прогресса под запись,
    /// остаток до 1 занимает раскраска.
    /// </summary>
    public static int FillRecords(string dbPath, TimeShell shell, IReadOnlyList<TimeRow> rows,
                                  TimeCollectorEngine.RowPaint[] paints,
                                  IProgress<CollectProgress>? progress, CancellationToken ct,
                                  double from = 0.70, double to = 0.95)
    {
        using var ctx = new AppDbContext(dbPath);
        ctx.ApplyBulkPerformancePragmas();
        var repo = new ConstructorRepository(ctx);

        // Шаг позиции 1000 оставляет место для вставки строк; на неправдоподобно больших сводах
        // падаем до 1, чтобы (i+1)*step не переполнил int (как в импорте).
        var step = rows.Count > 2_000_000 ? 1 : 1000;
        var ids  = new int[rows.Count];

        for (var i = 0; i < rows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var row = rows[i];

            var record = repo.AddRecord(new DbRecord
            {
                CollectionId = shell.CollectionId,
                Title        = row.Name,
                Position     = (i + 1) * step,
            });
            var recordId = record.Id!.Value;
            ids[i] = recordId;

            if (row.Deno.Length > 0) repo.SetFieldValue(recordId, shell.DenoFieldId, row.Deno);

            var norm = NormValue(row);
            if (norm.Length > 0) repo.SetFieldValue(recordId, shell.NormFieldId, norm);

            repo.SetFieldValue(recordId, shell.DateFieldId, DateStorage(row.Date));

            if (row.Efio.Length > 0)       repo.SetFieldValue(recordId, shell.EfioFieldId, row.Efio);
            if (row.Proj.Length > 0)       repo.SetFieldValue(recordId, shell.ProjFieldId, row.Proj);
            if (row.SourcePath.Length > 0) repo.SetFieldValue(recordId, shell.FileFieldId, row.SourcePath);

            if ((i + 1) % ClearTrackerEvery == 0) ctx.ChangeTracker.Clear();

            if (i % ProgressEvery == 0 || i == rows.Count - 1)
                progress?.Report(new CollectProgress(
                    from + (to - from) * (i + 1) / rows.Count, "Запись строк",
                    $"{i + 1} из {rows.Count}"));
        }

        Paint(repo, shell, ids, paints, progress, ct, to);
        return rows.Count;
    }

    /// <summary>Трудоемкость для ячейки: число — инвариантной строкой (десятичный разделитель
    /// поля приведет <c>SetFieldValue</c>), нечисловой текст — как есть, как и в Excel-своде.</summary>
    private static string NormValue(TimeRow row) =>
        row.Norm is { } value ? value.ToString(CultureInfo.InvariantCulture) : row.NormText;

    /// <summary>Дата расчета в каноне хранения поля «дата» (<see cref="DateValue"/>): день
    /// целиком, как его показывает и правит ячейка таблицы.</summary>
    private static string DateStorage(DateTime date) =>
        new DateValue(date.Date, date.Date.Add(DateValue.EndOfDay), AllDay: true).ToStorage();

    // ── Раскраска ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Разложить план раскраски (<see cref="TimeCollectorEngine.PlanColors"/>) по оформлению
    /// таблицы: заливка строки — на строку записи, пометка расхождения — на ячейку «Тр-ть».
    /// Белые строки не трогаем: «без заливки» и есть дефолт таблицы.
    /// </summary>
    private static void Paint(ConstructorRepository repo, TimeShell shell, int[] ids,
                              TimeCollectorEngine.RowPaint[] paints,
                              IProgress<CollectProgress>? progress, CancellationToken ct, double from)
    {
        var yellow  = new List<int>();
        var red     = new List<int>();
        var normRed = new List<(int RecordId, string Key)>();
        var normKey = shell.NormFieldId.ToString();

        var count = Math.Min(ids.Length, paints.Length);
        for (var i = 0; i < count; i++)
        {
            switch (paints[i].Fill)
            {
                case TimeCollectorEngine.RowFill.Yellow: yellow.Add(ids[i]); break;
                case TimeCollectorEngine.RowFill.Red:    red.Add(ids[i]);    break;
            }
            if (paints[i].NormRed) normRed.Add((ids[i], normKey));
        }

        if (yellow.Count == 0 && red.Count == 0 && normRed.Count == 0) return;

        progress?.Report(new CollectProgress(from, "Раскраска…", ""));
        PaintRows(repo, shell.CollectionId, yellow, YellowHex, ct);
        PaintRows(repo, shell.CollectionId, red, RedHex, ct);
        PaintCells(repo, shell.CollectionId, normRed, RedHex, ct);
    }

    private static void PaintRows(ConstructorRepository repo, int collectionId,
                                  List<int> recordIds, string hex, CancellationToken ct)
    {
        for (var i = 0; i < recordIds.Count; i += StyleBatch)
        {
            ct.ThrowIfCancellationRequested();
            repo.SetRowStyle(collectionId, recordIds.GetRange(i, Math.Min(StyleBatch, recordIds.Count - i)),
                             StyleAspect.Background, hex);
        }
    }

    private static void PaintCells(ConstructorRepository repo, int collectionId,
                                   List<(int RecordId, string Key)> cells, string hex, CancellationToken ct)
    {
        for (var i = 0; i < cells.Count; i += StyleBatch)
        {
            ct.ThrowIfCancellationRequested();
            repo.SetCellStyle(collectionId, cells.GetRange(i, Math.Min(StyleBatch, cells.Count - i)),
                              StyleAspect.Background, hex);
        }
    }
}
