using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Exporters;
using SmartBase.Core.Importers;
using SmartBase.Core.Models;
using SmartBase.Integrations;

namespace SmartBase.Database.Repositories;

/// <summary>
/// Укладка ответа сторонней программы в СУЩЕСТВУЮЩУЮ коллекцию по привязке
/// (<see cref="CollectionBinding"/>). Обычный импорт заводит новую коллекцию; обмен так работать
/// не может — он повторяется по расписанию, и каждый запуск обязан ложиться в ту же таблицу.
///
/// Записи пишутся ОБЫЧНЫМ путем — <c>AddRecord</c> и <c>SetFieldValue</c>, а не пакетной
/// вставкой сырым SQL, которой пользуется импорт файла. Обычный путь сохраняет журнал изменений, пересчет
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

        var reader = ColumnReader(binding, schema);
        var fields = FieldsOfBinding(binding);

        // Начальный снимок сохраняет контракт исчезнувшей строки: существовавшая запись,
        // удаленная соседом до чтения входа, пропускается, а не создается заново. Решение о
        // новом ключе ниже принимается уже на свежих данных внутри транзакции строки.
        var (index, duplicates) = binding.Matches ? BuildKeyIndex(binding) : ([], []);

        var created = 0;
        var updated = 0;
        var skipped = 0;
        var unclear = 0;
        var empty   = 0;
        var done    = 0;

        var position = NextPosition(binding.CollectionId);
        long? importLogId = null;

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var key = binding.Matches ? reader(row, binding.MatchColumn).Trim() : "";
            var committed = InCollectionWriteTransaction(binding.CollectionId, () =>
            {
                ct.ThrowIfCancellationRequested();
                ExchangeRowResult outcome;
                if (!binding.Matches)
                {
                    outcome = new(ExchangeRowChange.Created, Create(binding, reader, fields, row, position));
                }
                else if (key.Length == 0) outcome = new(ExchangeRowChange.Empty);
                else if (duplicates.Contains(key))
                {
                    outcome = new(ExchangeRowChange.Ambiguous);
                }
                else if (index.TryGetValue(key, out var original) && !Exists(original))
                {
                    outcome = new(ExchangeRowChange.Skipped);
                }
                else
                {
                    var matches = FindExchangeKey(binding.CollectionId, binding.MatchFieldId, key);
                    if (matches.Count > 1) outcome = new(ExchangeRowChange.Ambiguous);
                    else if (matches.Count == 1)
                    {
                        if (binding.UpdateExisting)
                        {
                            Update(binding, reader, fields, row, matches[0]);
                            outcome = new(ExchangeRowChange.Updated, matches[0]);
                        }
                        else outcome = new(ExchangeRowChange.Skipped);
                    }
                    // Ключ ранее существовал, но теперь относится к другому значению:
                    // не перезаписываем чужую правку и не создаем дубль прежней записи.
                    else if (index.ContainsKey(key)) outcome = new(ExchangeRowChange.Skipped);
                    else if (binding.CreateMissing)
                        outcome = new(ExchangeRowChange.Created, Create(binding, reader, fields, row, position));
                    else outcome = new(ExchangeRowChange.Skipped);
                }

                if (outcome.Change is ExchangeRowChange.Created or ExchangeRowChange.Updated)
                    ValidateExchangeFields(binding.CollectionId, fields.Select(field => field.FieldId));

                // Единственная строка импорта обновляется вместе с очередной входной
                // строкой. Финального SaveChanges после подтвержденных данных больше нет:
                // его поздний отказ раньше превращал сохраненную строку в ошибку вызова.
                var changed = outcome.Change is ExchangeRowChange.Created or ExchangeRowChange.Updated;
                var logId = importLogId;
                if (changed || logId is null)
                {
                    if (logId is long previous)
                        db.ChangeLog.Where(log => log.Id == previous).ExecuteDelete();
                    var log = Log("import", "collection", CollectionNameOf(binding.CollectionId) ?? "",
                        count: created + updated + (changed ? 1 : 0));
                    db.SaveChanges();
                    logId = log.Id;
                }
                return (Outcome: outcome, LogId: logId);
            });

            importLogId = committed.LogId;
            switch (committed.Outcome.Change)
            {
                case ExchangeRowChange.Created:
                    position += 1000;
                    created++;
                    if (binding.Matches) index[key] = committed.Outcome.RecordId!.Value;
                    break;
                case ExchangeRowChange.Updated:
                    updated++;
                    index[key] = committed.Outcome.RecordId!.Value;
                    break;
                case ExchangeRowChange.Ambiguous: unclear++; break;
                case ExchangeRowChange.Empty: empty++; break;
                default:
                    skipped++;
                    if (binding.Matches) index.Remove(key);
                    break;
            }

            // Запись уже сохранена — держать ее в трекере EF нечем и незачем. Тот же прием, что
            // у переноса коллекции между базами (ChangeTracker.Clear в цикле), и по той же
            // причине, только здесь она стоила дороже всего.
            //
            // ЧТО БЫЛО. Каждая уложенная запись оставляла за собой отслеживаемые сущности —
            // саму запись, ее значения, строки журнала изменений, — а EF на КАЖДОМ SaveChanges
            // обходит весь накопленный список. К двухтысячной записи он обходил десятки тысяч
            // сущностей, и время росло квадратично. Замер на этой машине:
            //
            //   250 записей — 13 мс на запись, 500 — 12 мс, 1000 — 22 мс, 2000 — 42 мс, 5000 — 104 мс.
            //
            // То есть сто тысяч записей ушли бы не в минуты, а в десятки часов, и виноват был бы
            // не объем данных, а эта строка, которой не было.
            db.ChangeTracker.Clear();

            progress?.Report(++done);
        }

        if (importLogId is null)
            InCollectionWriteTransaction(binding.CollectionId, () =>
            {
                LogNow("import", "collection", CollectionNameOf(binding.CollectionId) ?? "", count: 0);
                return 0;
            });
        return new BindingResult(created, updated, skipped, unclear, empty);
    }

    /// <summary>Применить привязку в фоне на своем подключении.</summary>
    public BindingResult ApplyRowsIsolated(CollectionBinding binding, ImportSchema schema,
        IEnumerable<ImportRow> rows, CancellationToken ct, IProgress<int>? progress = null)
    {
        using var ctx = new AppDbContext(db.DbPath);
        return new ConstructorRepository(ctx).ApplyRows(binding, schema, rows, ct, progress);
    }

    /// <summary>Выполнить работу расширения на отдельном подключении.</summary>
    public T UseIsolated<T>(int collectionId, Func<ConstructorRepository, T> work)
    {
        using var ctx = new AppDbContext(db.DbPath);
        return work(new ConstructorRepository(ctx));
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
    private (Dictionary<string, int> Index, HashSet<string> Duplicates) BuildKeyIndex(CollectionBinding binding) =>
        KeyIndex(binding.CollectionId, binding.MatchFieldId);

    /// <summary>
    /// То же без привязки — по коллекции и свойству ключа (<c>null</c> — название записи).
    /// Отдельный вход нужен удалению по ключу: у него привязки нет, только колонка.
    /// </summary>
    private (Dictionary<string, int> Index, HashSet<string> Duplicates) KeyIndex(
        int collectionId, int? keyFieldId)
    {
        var key = keyFieldId is int fieldId
            ? fieldId.ToString()
            : TitleKey;

        var values = GetColumnValues(collectionId, key);

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

    private enum ExchangeRowChange { Created, Updated, Skipped, Ambiguous, Empty }
    private sealed record ExchangeRowResult(ExchangeRowChange Change, int? RecordId = null);

    /// <summary>
    /// Свежий поиск ключа внутри транзакции строки. Снимок всей колонки не годится для
    /// конкурентного upsert: сосед мог создать такой ключ после начала выгрузки. SQL
    /// ограничивает кандидатов, итоговое сравнение сохраняет OrdinalIgnoreCase контракта.
    /// Двух совпадений достаточно для однозначного отказа без выбора произвольной записи.
    /// </summary>
    private List<int> FindExchangeKey(int collectionId, int? fieldId, string key)
    {
        var values = fieldId is int id
            ? db.DbFieldValues.AsNoTracking()
                .Where(value => value.FieldId == id && value.Record.CollectionId == collectionId)
                .Select(value => new { RecordId = value.RecordId, Key = value.Value })
            : db.DbRecords.AsNoTracking().Where(record => record.CollectionId == collectionId)
                .Select(record => new { RecordId = record.Id, Key = record.Title });
        if (db.IsPostgres)
        {
            var literal = key.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            var pattern = "%" + literal + "%";
            values = values.Where(value => EF.Functions.ILike(value.Key, pattern, "\\"));
        }
        else
        {
            values = values.Where(value => SqlText.ContainsIgnoreCase(value.Key, key));
        }
        return values.AsEnumerable()
            .Where(value => string.Equals(value.Key.Trim(), key, StringComparison.OrdinalIgnoreCase))
            .Select(value => value.RecordId).Distinct().Take(2).ToList();
    }

    private void ValidateExchangeFields(int collectionId, IEnumerable<int> fieldIds)
    {
        var expected = fieldIds.ToHashSet();
        var current = db.DbFields.AsNoTracking()
            .Where(field => field.CollectionId == collectionId && expected.Contains(field.Id))
            .Select(field => new { field.Id, field.FieldType }).ToList();
        if (current.Count != expected.Count
            || current.Any(field => FieldTypeInfo.IsComputed(field.FieldType) || FieldTypeInfo.IsRelation(field.FieldType)))
            throw new ImportException("Свойства коллекции изменились во время записи — проверьте привязку.");
    }

    // ── Запись ────────────────────────────────────────────────────────────────

    private int Create(
        CollectionBinding binding,
        Func<ImportRow, string, string> reader,
        List<(string Column, int FieldId)> fields,
        ImportRow row,
        int position)
    {
        var titleColumn = binding.Columns.FirstOrDefault(c => c.FieldId is null)?.Column;

        var record = AddRecord(new DbRecord
        {
            CollectionId = binding.CollectionId,
            Title        = titleColumn is null ? "" : reader(row, titleColumn),
            Position     = position,
        });

        foreach (var (column, fieldId) in fields)
            SetFieldValue(record.Id!.Value, fieldId, reader(row, column));

        return record.Id!.Value;
    }

    /// <summary>
    /// Жива ли еще запись. Спрашивается на КАЖДОМ обновлении, и это не перестраховка: ключи
    /// собираются одним чтением в начале укладки, база лежит в общей папке, а выгрузка идет
    /// минуты — запись, попавшую в индекс и удаленную соседом через минуту, обновить уже не во что.
    ///
    /// Без этой проверки такая строка считалась ОБНОВЛЕННОЙ: квитанция уезжала отправителю со
    /// словом «обновлено», не обновив ничего, и разошлось бы это только при сверке остатков.
    /// </summary>
    private bool Exists(int recordId) =>
        db.DbRecords.AsNoTracking().Any(r => r.Id == recordId);

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
    /// Читается на одну строку больше потолка: «влезло ровно столько» и «не влезло» — разные
    /// ответы, и отправить первые N из тысячи, промолчав об остальных, нельзя.
    /// </summary>
    /// <param name="binding">Что и под какими именами уходит.</param>
    /// <param name="limit">Максимум строк результата; int.MaxValue — вся выборка.</param>
    /// <param name="ct">Отмена.</param>
    /// <param name="records">Какие записи отправлять; <c>null</c> или пусто — всю коллекцию.
    /// Список приходит от точки вызова: человек выделил строки в таблице и позвал действие
    /// именно для них.</param>
    public OutgoingRows ReadOutgoingIsolated(OutgoingBinding binding, int limit,
                                             CancellationToken ct,
                                             IReadOnlyList<int>? records = null)
    {
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyPerformancePragmas();
        var isolated = new ConstructorRepository(ctx);

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

            columns.Add(column);
        }

        // Колонки отбора — сверх уходящих: отбирать можно по свойству, которое наружу не идет
        // («Отправлено» = пусто), и это обычный случай, а не исключение.
        var extra = new List<ExportColumn>();

        foreach (var test in binding.Tests)
        {
            if (columns.Any(c => Same(c.Header, test.Column)) ||
                extra.Any(c => Same(c.Header, test.Column))) continue;

            if (available.Values.FirstOrDefault(c => Same(c.Header, test.Column)) is not { } found)
                return OutgoingRows.Bad(
                    $"свойства «{test.Column}», по которому идет отбор, в коллекции "
                    + $"«{collection}» нет — отбор надо собрать заново");

            extra.Add(found);
        }

        // Отбор применяется ДО потолка, иначе он ничего не решает: потолок считается по числу
        // прочитанных строк, и коллекция на сто тысяч записей отказывала бы и с условием,
        // отбирающим три.
        var all   = columns.Concat(extra).ToList();
        var tests = binding.Tests
                           .Select(t => (Test: t, At: all.FindIndex(c => Same(c.Header, t.Column))))
                           .ToList();

        var take = limit >= int.MaxValue ? int.MaxValue : Math.Max(0, limit) + 1;
        var source = records is { Count: > 0 }
            ? isolated.ChosenRows(binding.CollectionId, all, records, tests, take, ct)
            : isolated.ReadExportRowsIsolated(binding.CollectionId, all, excludeHidden: false, ct)
                .Where(row => Fits(tests, row)).Take(take);
        var rows = new List<string[]>();
        // Технический размер запроса проверяется по мере чтения страниц, до сборки
        // тела. Счетчик использует тот же JSON-писатель без буфера данных.
        using var sizer = OutgoingBody.CreateRowSizer(binding, columns);
        long bytes = binding.Shape switch
        {
            OutgoingShape.Single => 0,
            OutgoingShape.Wrapped => 7 + System.Text.Json.JsonEncodedText.Encode(binding.Envelope.Trim(),
                System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping).EncodedUtf8Bytes.Length,
            _ => 2,
        };
        foreach (var sourceRow in source)
        {
            ct.ThrowIfCancellationRequested();
            var row = extra.Count > 0 ? sourceRow[..columns.Count] : sourceRow;
            if (!binding.Single && rows.Count > 0) bytes++;
            bytes += sizer.Measure(row);
            if (bytes > EgressHttp.MaxResponseBytes)
                return OutgoingRows.Bad("Тело запроса превышает технический размер 32 МБ. Сузьте отбор записей или состав полей.");
            rows.Add(row);
        }

        return new OutgoingRows(columns, rows, collection, null);
    }

    /// <summary>
    /// Проходит ли строка все условия отбора. Порядок значений — порядок колонок; колонку
    /// сопоставили условию один раз, а не на каждую строку.
    ///
    /// Колонки нет в строке (<c>At &lt; 0</c>) — строка НЕ проходит. Такого быть не должно:
    /// отсутствие свойства ловится раньше и объясняется словами. Но если уж случилось,
    /// «пропустить все» было бы худшим из ответов — отбор молча перестал бы отбирать.
    /// </summary>
    private static bool Fits(IReadOnlyList<(ScenarioCondition Test, int At)> tests, string[] row) =>
        tests.All(t => t.At >= 0 && t.At < row.Length && t.Test.HoldsValue(row[t.At]));

    /// <summary>Выбранные записи в порядке таблицы, с чтением значений только текущей страницы.</summary>
    private IEnumerable<string[]> ChosenRows(int collectionId, IReadOnlyList<ExportColumn> columns,
        IReadOnlyList<int> records, IReadOnlyList<(ScenarioCondition Test, int At)> tests,
        int take, CancellationToken ct)
    {
        var selected = db.DbRecords.AsNoTracking()
            .Where(r => r.CollectionId == collectionId && records.Contains(r.Id));
        var fields = columns.Where(c => c.FieldId.HasValue).Select(c => c.FieldId!.Value).Distinct().ToArray();
        var auditTypes = db.DbFields.AsNoTracking().Where(f => f.CollectionId == collectionId &&
            (f.FieldType == "created_at" || f.FieldType == "updated_at"
             || f.FieldType == "created_by" || f.FieldType == "updated_by"))
            .ToDictionary(f => f.Id, f => f.FieldType);
        int? afterPosition = null, afterId = null;
        var accepted = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var query = selected;
            if (afterPosition is int position && afterId is int id)
                query = query.Where(r => r.Position > position || r.Position == position && r.Id > id);
            var page = query.OrderBy(r => r.Position).ThenBy(r => r.Id).Take(200).ToList();
            if (page.Count == 0) yield break;
            var values = GetFieldValuesBatch(page.Select(r => r.Id).ToArray(), fields);
            foreach (var record in page)
            {
                ct.ThrowIfCancellationRequested();
                var mine = values.GetValueOrDefault(record.Id);
                var row = new string[columns.Count];
                for (var i = 0; i < columns.Count; i++)
                    row[i] = columns[i].Key == TitleKey ? record.Title
                        : columns[i].FieldId is int field
                            ? auditTypes.TryGetValue(field, out var auditType)
                                ? auditType switch
                                {
                                    "created_at" => record.CreatedAt ?? "", "updated_at" => record.UpdatedAt ?? "",
                                    "created_by" => record.CreatedBy ?? "", _ => record.UpdatedBy ?? "",
                                }
                                : mine?.GetValueOrDefault(field, "") ?? ""
                            : "";
                if (!Fits(tests, row)) continue;
                yield return row;
                if (++accepted >= take) yield break;
            }
            (afterPosition, afterId) = (page[^1].Position, page[^1].Id);
        }
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
