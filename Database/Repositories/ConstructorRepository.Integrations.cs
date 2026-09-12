using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Exporters;
using SmartBase.Core.Importers;
using SmartBase.Core.Models;
using SmartBase.Integrations;

namespace SmartBase.Database.Repositories;

/// <summary>
/// Укладка ответа чужой программы в СУЩЕСТВУЮЩУЮ коллекцию по привязке
/// (<see cref="CollectionBinding"/>). Обычный импорт заводит новую коллекцию; обмен так работать
/// не может — он повторяется по расписанию, и каждый запуск обязан ложиться в ту же таблицу.
///
/// Записи пишутся ОБЫЧНЫМ путем — <c>AddRecord</c> и <c>SetFieldValue</c>, а не пакетной
/// вставкой сырым SQL, которой пользуется импорт файла. Причина в объемах: файл приносит
/// миллионы строк, а один запуск обмена ограничен потолком политики — там тысячи. За обычный
/// путь при этом достается все, что к нему прилагается: журнал изменений, права, пересчет
/// календарного и задачного индексов, авто-привязка файлов. Пакетная вставка все это обходит,
/// и обмен молча ломал бы то, что человек видит на других экранах.
/// </summary>
public partial class ConstructorRepository
{
    /// <summary>
    /// Применить строки ответа к коллекции. Возвращает, что именно случилось с каждой строкой —
    /// молчаливый пропуск половины записей ищут потом неделю.
    /// </summary>
    /// <param name="binding">Привязка: колонки, ключ сопоставления, что делать с найденным.</param>
    /// <param name="schema">Схема источника — из нее берутся имена колонок.</param>
    /// <param name="rows">Строки в порядке источника.</param>
    /// <param name="ct">Отмена.</param>
    /// <param name="progress">Сколько строк обработано.</param>
    public BindingResult ApplyRows(
        CollectionBinding      binding,
        ImportSchema           schema,
        IEnumerable<ImportRow> rows,
        CancellationToken      ct,
        IProgress<int>?        progress = null)
    {
        var problems = binding.Problems();
        if (problems.Count > 0)
            throw new ImportException("Привязка не годится: " + string.Join("; ", problems));

        if (!db.DbCollections.AsNoTracking().Any(c => c.Id == binding.CollectionId))
            throw new ImportException("Коллекция не найдена — возможно, ее удалили.");

        // Права спрашиваются ДО первой записи, хотя их проверит и каждый AddRecord отдельно:
        // отказ на середине оставил бы коллекцию наполовину обновленной, а обмен повторяется
        // по расписанию — разбираться в таком состоянии потом некому.
        DemandRights(binding);

        var reader = ColumnReader(binding, schema);
        var fields = FieldsOfBinding(binding);

        // Ключ строится ОДНИМ чтением одной колонки, а не запросом на каждую строку: строк
        // ответа тысячи, и тысяча запросов к большой коллекции заняла бы больше, чем сам обмен.
        // Плата — память под одну колонку коллекции; для обмена это приемлемо, а сопоставление
        // с коллекцией в миллионы записей — отдельная задача, которой пока нет.
        var (index, duplicates) = binding.Matches ? BuildKeyIndex(binding) : ([], []);

        var created = 0;
        var updated = 0;
        var skipped = 0;
        var unclear = 0;
        var empty   = 0;
        var done    = 0;

        var position = NextPosition(binding.CollectionId);

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            if (!binding.Matches)
            {
                Create(binding, reader, fields, row, ref position);
                created++;
            }
            else
            {
                var key = reader(row, binding.MatchColumn).Trim();

                if (key.Length == 0)
                {
                    // Пустой ключ — сопоставлять не по чему. Завести такую запись значило бы
                    // плодить дубли при каждом следующем запуске.
                    empty++;
                }
                else if (duplicates.Contains(key))
                {
                    unclear++;
                }
                else if (index.TryGetValue(key, out var recordId))
                {
                    if (binding.UpdateExisting) { Update(binding, reader, fields, row, recordId); updated++; }
                    else skipped++;
                }
                else if (binding.CreateMissing)
                {
                    var id = Create(binding, reader, fields, row, ref position);
                    index[key] = id;   // в одном ответе ключ может встретиться дважды
                    created++;
                }
                else
                {
                    skipped++;
                }
            }

            progress?.Report(++done);
        }

        var result = new BindingResult(created, updated, skipped, unclear, empty);

        LogNow("import", "collection", CollectionNameOf(binding.CollectionId) ?? "", count: created + updated);

        return result;
    }

    /// <summary>
    /// То же на ОТДЕЛЬНОМ подключении — для вызова с фонового потока. Общий контекст с фонового
    /// потока трогать нельзя: его параллельно использует интерфейс, и EF отвечает на это
    /// «A second operation was started on this context instance» (см. <c>ImportStreamedCollectionIsolated</c>).
    ///
    /// Права спрашиваются ЗДЕСЬ, на публичной точке входа: изолированный клон репозитория прав
    /// не несет, и внутри проверять их уже нечем.
    /// </summary>
    public BindingResult ApplyRowsIsolated(
        CollectionBinding      binding,
        ImportSchema           schema,
        IEnumerable<ImportRow> rows,
        CancellationToken      ct,
        IProgress<int>?        progress = null)
    {
        DemandRights(binding);

        using var ctx = new AppDbContext(db.DbPath);
        return new ConstructorRepository(ctx).ApplyRows(binding, schema, rows, ct, progress);
    }

    /// <summary>
    /// Отдельное подключение к той же базе — для работы, которую ведет не интерфейс: шаг
    /// сценария выполняет действие расширения в фоне, а расширение пишет записи само
    /// (<c>Extensions/ExtensionAction.cs</c>).
    ///
    /// Подключение открывается ОДНО на всю работу, а не на строку: сообщений в пачке бывает
    /// несколько сотен, и подключение на каждое стоило бы дороже самой записи.
    ///
    /// Права спрашиваются ЗДЕСЬ, на публичной точке входа, — как и у
    /// <see cref="ApplyRowsIsolated"/>: изолированный клон репозитория прав не несет, и внутри
    /// проверять их уже нечем. Спрашивается право заводить записи: действие-операция затем и
    /// зовется, чтобы завести запись, а действий, которые только читают, в контракте нет.
    /// </summary>
    /// <param name="collectionId">Коллекция, с которой будет работать вызванная работа.</param>
    /// <param name="work">Что сделать на отдельном подключении.</param>
    public T UseIsolated<T>(int collectionId, Func<ConstructorRepository, T> work)
    {
        Demand(PermAction.CreateRecord, collectionId);

        using var ctx = new AppDbContext(db.DbPath);
        return work(new ConstructorRepository(ctx));
    }

    /// <summary>
    /// Права, без которых привязку применять нечем. Спрашивается только то, что привязка
    /// собирается делать: право заводить записи не нужно тому, кто выбрал «только обновлять».
    /// </summary>
    private void DemandRights(CollectionBinding binding)
    {
        if (!binding.Matches || binding.CreateMissing)
            Demand(PermAction.CreateRecord, binding.CollectionId);

        if (binding.Matches && binding.UpdateExisting)
            Demand(PermAction.EditRecord, binding.CollectionId);
    }

    // ── Чтение колонок источника ──────────────────────────────────────────────

    /// <summary>
    /// Как достать значение колонки из строки. Колонок у источника две породы: название (оно
    /// лежит отдельно) и свойства (по порядку имен схемы), поэтому обращение по имени готовится
    /// один раз, а не разбирается на каждой строке.
    /// </summary>
    private static Func<ImportRow, string, string> ColumnReader(CollectionBinding binding, ImportSchema schema)
    {
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < schema.FieldNames.Count; i++) byName[schema.FieldNames[i]] = i;

        var titleHeader = schema.TitleHeader ?? "";

        // Колонка привязки, которой нет в ответе, — не тихая пустота, а ошибка: иначе поле
        // молча заполнялось бы пустотой при каждом запуске.
        foreach (var column in binding.Columns)
            if (!byName.ContainsKey(column.Column)
                && !string.Equals(column.Column, titleHeader, StringComparison.OrdinalIgnoreCase))
                throw new ImportException(
                    $"В ответе нет колонки «{column.Column}» — привязка составлена для другого ответа.");

        return (row, name) =>
        {
            if (string.Equals(name, titleHeader, StringComparison.OrdinalIgnoreCase)) return row.Title ?? "";

            return byName.TryGetValue(name, out var i) && i < row.FieldValues.Count
                ? row.FieldValues[i] ?? ""
                : "";
        };
    }

    /// <summary>Свойства коллекции, в которые кладем; попутно проверяется, что они еще есть.</summary>
    private List<(string Column, int FieldId)> FieldsOfBinding(CollectionBinding binding)
    {
        var known = db.DbFields.AsNoTracking()
            .Where(f => f.CollectionId == binding.CollectionId)
            .Select(f => f.Id)
            .ToHashSet();

        var fields = new List<(string, int)>();

        foreach (var column in binding.Columns)
        {
            if (column.FieldId is not int fieldId) continue;

            if (!known.Contains(fieldId))
                throw new ImportException(
                    $"Свойства для колонки «{column.Column}» в коллекции больше нет — поправьте привязку.");

            fields.Add((column.Column, fieldId));
        }

        return fields;
    }

    // ── Ключ сопоставления ────────────────────────────────────────────────────

    /// <summary>
    /// Значение ключа → запись. Повторяющиеся значения выносятся отдельно: править одну из
    /// нескольких одинаковых записей наугад хуже, чем не трогать ни одной и сказать об этом.
    /// </summary>
    private (Dictionary<string, int> Index, HashSet<string> Duplicates) BuildKeyIndex(CollectionBinding binding)
    {
        var key = binding.MatchFieldId is int fieldId
            ? fieldId.ToString()
            : TitleKey;

        var values = GetColumnValues(binding.CollectionId, key);

        var index      = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (recordId, raw) in values)
        {
            var value = (raw ?? "").Trim();
            if (value.Length == 0) continue;

            if (!index.TryAdd(value, recordId)) duplicates.Add(value);
        }

        return (index, duplicates);
    }

    // ── Запись ────────────────────────────────────────────────────────────────

    private int Create(
        CollectionBinding binding,
        Func<ImportRow, string, string> reader,
        List<(string Column, int FieldId)> fields,
        ImportRow row,
        ref int position)
    {
        var titleColumn = binding.Columns.FirstOrDefault(c => c.FieldId is null)?.Column;

        var record = AddRecord(new DbRecord
        {
            CollectionId = binding.CollectionId,
            Title        = titleColumn is null ? "" : reader(row, titleColumn),
            Position     = position,
        });

        position += 1000;

        foreach (var (column, fieldId) in fields)
            SetFieldValue(record.Id!.Value, fieldId, reader(row, column));

        return record.Id!.Value;
    }

    private void Update(
        CollectionBinding binding,
        Func<ImportRow, string, string> reader,
        List<(string Column, int FieldId)> fields,
        ImportRow row,
        int recordId)
    {
        var titleColumn = binding.Columns.FirstOrDefault(c => c.FieldId is null)?.Column;

        if (titleColumn is not null)
        {
            var record = GetRecord(recordId);
            var title  = reader(row, titleColumn);

            if (record is not null && record.Title != title)
            {
                record.Title = title;
                UpdateRecord(record);
            }
        }

        foreach (var (column, fieldId) in fields)
            SetFieldValue(recordId, fieldId, reader(row, column));
    }

    // ── Наружу: строки коллекции под исходящую привязку ───────────────────────

    /// <summary>
    /// Строки коллекции, готовые уйти наружу.
    /// </summary>
    /// <param name="Columns">Колонки в порядке полей привязки — из них берется ТИП значения.</param>
    /// <param name="Rows">Значения строк в том же порядке. Может прийти на одну больше
    /// потолка — по ней вызывающий и узнает, что записи в потолок не влезли.</param>
    /// <param name="Collection">Имя коллекции — для сообщений человеку.</param>
    /// <param name="Trouble">Почему отправлять нечего; <c>null</c> — все в порядке.</param>
    public sealed record OutgoingRows(
        IReadOnlyList<ExportColumn> Columns,
        IReadOnlyList<string[]>     Rows,
        string                      Collection,
        string?                     Trouble)
    {
        public static OutgoingRows Bad(string trouble) => new([], [], "", trouble);
    }

    /// <summary>
    /// Прочитать записи коллекции под исходящую привязку — на ОТДЕЛЬНОМ подключении: зовут это
    /// и из фонового сценария, а общий контекст принадлежит интерфейсу.
    ///
    /// Здесь стоят обе метки конфиденциальности, и стоят они ВТОРЫМ рубежом. Первый — окно
    /// настройки: помеченное свойство в списке не появляется вовсе (10.6). Но метку могли
    /// поставить после того, как привязку собрали, — и тогда отправка обязана встать, а не
    /// уносить поле, которое кто-то только что закрыл.
    ///
    /// Читается на одну строку больше потолка: «влезло ровно столько» и «не влезло» — разные
    /// ответы, и отправить первые N из тысячи, промолчав об остальных, нельзя.
    /// </summary>
    /// <param name="binding">Что и под какими именами уходит.</param>
    /// <param name="limit">Потолок строк за запуск — из политики организации.</param>
    /// <param name="ct">Отмена.</param>
    public OutgoingRows ReadOutgoingIsolated(OutgoingBinding binding, int limit, CancellationToken ct)
    {
        // Права — на публичной точке входа, как у ApplyRowsIsolated: изолированный клон
        // репозитория прав не несет. Отправка наружу начинается с чтения, и права на чтение
        // коллекции — то самое, чего требует эта половина.
        Demand(PermAction.ViewCollection, binding.CollectionId);

        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyPerformancePragmas();
        var isolated = new ConstructorRepository(ctx);

        var closed = EgressGate.CheckDatabase(isolated.GetDatabaseLabel());
        if (!closed.Allowed) return OutgoingRows.Bad(closed.Explanation);

        var collection = isolated.CollectionName(binding.CollectionId);
        if (collection.Length == 0)
            return OutgoingRows.Bad("коллекция, записи которой уходят, удалена");

        // includeHidden: скрытая колонка — это настройка ПОКАЗА таблицы, а привязка названа
        // поименно и человеком. Молча выбросить из нее поле, спрятанное в другом экране,
        // значило бы отправлять не то, что показано в окне настройки.
        var available = isolated.GetExportColumns(binding.CollectionId, includeHidden: true)
                                .ToDictionary(c => c.Key, StringComparer.Ordinal);

        var columns = new List<ExportColumn>(binding.Fields.Count);

        foreach (var field in binding.Fields)
        {
            var key = field.FieldId?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                   ?? TitleKey;

            if (!available.TryGetValue(key, out var column))
                return OutgoingRows.Bad(
                    $"свойства, уходившего под именем «{field.Name}», в коллекции "
                    + $"«{collection}» больше нет — привязку надо собрать заново");

            var mark = EgressGate.CheckField(Confidentiality.FromConfig(column.ConfigJson));

            if (!mark.Allowed)
                return OutgoingRows.Bad($"свойство «{column.Header}»: {mark.Explanation}");

            columns.Add(column);
        }

        var rows = ReadExportRowsIsolated(binding.CollectionId, columns, excludeHidden: false, ct)
                   .Take(Math.Max(0, limit) + 1)
                   .ToList();

        return new OutgoingRows(columns, rows, collection, null);
    }

    /// <summary>Позиция для первой новой записи — в конец коллекции, как при обычном добавлении.</summary>
    private int NextPosition(int collectionId)
    {
        var max = db.DbRecords.AsNoTracking()
            .Where(r => r.CollectionId == collectionId)
            .Select(r => (int?)r.Position)
            .Max() ?? 0;

        return max + 1000;
    }
}
