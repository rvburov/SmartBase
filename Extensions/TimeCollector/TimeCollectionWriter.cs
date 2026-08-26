using System.Globalization;
using System.Text.Json;
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
/// (<see cref="TimeSheetWriter"/>). Строки и план раскраски приходят готовыми из
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

    /// <summary>
    /// Формат колонки «Тр-ть»: «Число с разрядами», знаков после запятой — «Авто», десятичная
    /// запятая, разряды НЕ разделяются. Выставлять формат руками после каждого прогона незачем,
    /// поэтому колонка приходит настроенной.
    ///
    /// «Авто» (<c>Decimals = -1</c>, см. <see cref="NumberFormat"/>) — это «сколько есть, до шести
    /// знаков, лишние нули убраны». Фиксированная точность тут врала бы в обе стороны: два знака
    /// дописывают «12» до «12,00», как будто точность измерена, и режут «12,456» до «12,46»,
    /// теряя то, что в расчете есть. Трудоемкость приходит из ячеек с разной точностью, и
    /// показывать ее надо такой, какая она в расчете.
    ///
    /// Символ валюты и его сторона на этот вид формата не влияют, но в настройке хранятся
    /// всегда — берем значения по умолчанию, как их кладет окно свойства колонки.
    /// </summary>
    private static readonly NumberFormat NormFormat = new(
        NumberFormatKind.Grouped, Decimals: AutoDecimals, Thousands: false,
        CurrencySymbol: "₽", SymbolSuffix: true, DecimalSeparator: DecimalMark.Comma);

    /// <summary>«Знаков после запятой — Авто» в окне свойства колонки (см. <see cref="NumberFormat"/>).</summary>
    private const int AutoDecimals = -1;

    // Размер пакета заливки: пакет = одна транзакция, прогресс постится раз в пакет.
    // 10 000 — компромисс: реже транзакции, но на своде в 600 тыс. строк это 60 отчетов
    // о прогрессе, а не 30, и полоса не выглядит застывшей.
    private const int RecordBatch = 10_000;

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
            var norm = Field(repo, colId, NormFieldName, "number", 2000,
                             JsonSerializer.Serialize(NumberFormatValue.ToConfigObject(NormFormat)));
            // Дата свода — обычная колонка, а не событие: в календаре ей делать нечего
            // (у свода 600 тыс. строк, и это даты расчетов, а не встречи), поэтому тумблер
            // «Отображать в календаре» у поля выключен. Без autoCalendar: false репозиторий
            // включил бы его сам — первому полю-дате коллекции он ставит флаг по умолчанию.
            var date = Field(repo, colId, DateFieldName, "date",   3000, autoCalendar: false);
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

    private static int Field(ConstructorRepository repo, int colId, string name, string type, int position,
                             string? configJson = null, bool autoCalendar = true) =>
        repo.AddField(new DbField
        {
            CollectionId = colId, Name = name, FieldType = type, Position = position,
            ConfigJson = configJson ?? "{}",
        }, autoCalendar).Id!.Value;

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

        // Поля — в том порядке, в каком Values отдает Stream: он и есть контракт с BulkAddRecords.
        int[] fieldIds =
        [
            shell.DenoFieldId, shell.NormFieldId, shell.DateFieldId,
            shell.EfioFieldId, shell.ProjFieldId, shell.FileFieldId,
        ];

        var total = rows.Count;
        var ids = repo.BulkAddRecords(
            shell.CollectionId, fieldIds, Stream(rows), total, ct,
            new SyncProgress<long>(written => progress?.Report(new CollectProgress(
                from + (to - from) * written / Math.Max(1, total), "Запись строк",
                $"{written} из {total}"))),
            RecordBatch);

        Paint(repo, shell, ids, paints, progress, ct, to);
        return rows.Count;
    }

    /// <summary>
    /// Строки свода → строки массовой заливки, ЛЕНИВО: в памяти живет только текущий пакет,
    /// а не вторая копия свода на 600 тыс. строк. Порядок значений — тот же, что у списка полей
    /// в <see cref="FillRecords"/>. Пустые значения BulkAddRecords пропускает сам, поэтому
    /// условия «писать, только если не пусто» здесь не нужны — кроме даты, которая непуста
    /// всегда и потому есть у каждой записи (как и раньше).
    /// </summary>
    private static IEnumerable<BulkRecordRow> Stream(IReadOnlyList<TimeRow> rows)
    {
        foreach (var row in rows)
            yield return new BulkRecordRow(row.Name,
            [
                row.Deno,
                NormValue(row),
                DateStorage(row.Date),
                row.Efio,
                row.Proj,
                row.SourcePath,
            ]);
    }

    /// <summary>Прогресс, срабатывающий СИНХРОННО в потоке вызова. Штатный
    /// <see cref="Progress{T}"/> постит в контекст синхронизации, и отчеты о заливке приходили бы
    /// в UI пачкой после ее конца — полоса стояла бы всю запись.</summary>
    private sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }

    /// <summary>Трудоемкость для ячейки в каноне хранения поля «Число», нечисловой текст — как
    /// есть, как и в Excel-своде. Разделитель приводим здесь сами: массовая заливка не читает
    /// настройки поля (в отличие от <c>SetFieldValue</c>, который делал это запросом на КАЖДОЕ
    /// значение), а формат поля известен — его задает <see cref="NormFormat"/>, и разделитель
    /// в нем десятичная запятая.</summary>
    private static string NormValue(TimeRow row) =>
        row.Norm is { } value
            ? NumberFormatValue.NormalizeStorage(value.ToString(CultureInfo.InvariantCulture),
                                                 NormFormat.DecimalSeparator)
            : row.NormText;

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
    private static void Paint(ConstructorRepository repo, TimeShell shell, IReadOnlyList<int> ids,
                              TimeCollectorEngine.RowPaint[] paints,
                              IProgress<CollectProgress>? progress, CancellationToken ct, double from)
    {
        var yellow  = new List<int>();
        var red     = new List<int>();
        var normRed = new List<(int RecordId, string Key)>();
        var normKey = shell.NormFieldId.ToString();

        var count = Math.Min(ids.Count, paints.Length);
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

        // Массовые методы, а не поштучные SetRowStyle/SetCellStyle: те на каждый вызов читают
        // прежнее оформление и копят сущности в трекере EF — на 100 тыс. строк раскраска
        // занимала 46 из 54 секунд заливки. Коллекция только что создана, снимать нечего.
        progress?.Report(new CollectProgress(from, "Раскраска…", ""));
        repo.BulkSetRowStyle(shell.CollectionId, yellow, StyleAspect.Background, YellowHex, ct);
        repo.BulkSetRowStyle(shell.CollectionId, red, StyleAspect.Background, RedHex, ct);
        repo.BulkSetCellStyle(shell.CollectionId, normRed, StyleAspect.Background, RedHex, ct);
    }
}
