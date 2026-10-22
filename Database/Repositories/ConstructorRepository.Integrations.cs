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
                    // Запись могла ИСЧЕЗНУТЬ, пока шла укладка: база лежит в общей папке, ключи
                    // собираются одним чтением в начале, а выгрузка идет минуты. Обновлять
                    // удаленную запись не во что, и считать это обновлением нельзя — квитанция
                    // сказала бы «обновлено», не обновив ничего.
                    if (!Exists(recordId))
                    {
                        index.Remove(key);
                        skipped++;
                    }
                    else if (binding.UpdateExisting)
                    {
                        Update(binding, reader, fields, row, recordId);
                        updated++;
                    }
                    else
                    {
                        skipped++;
                    }
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

        var result = new BindingResult(created, updated, skipped, unclear, empty);

        LogNow("import", "collection", CollectionNameOf(binding.CollectionId) ?? "", count: created + updated);

        return result;
    }

    /// <summary>
    /// То же на ОТДЕЛЬНОМ подключении — для вызова с фонового потока. Общий контекст с фонового
    /// потока трогать нельзя: его параллельно использует интерфейс, и EF отвечает на это
    /// «A second operation was started on this context instance» (см. <c>ImportStreamedCollectionIsolated</c>).
    ///
    /// Права ручной записи спрашиваются ЗДЕСЬ, на публичной точке входа. Автоподстановка
    /// наследует исходные права отдельно: ее отложенная работа проверит их перед записью.
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
        return new ConstructorRepository(ctx) { FileAutoPermissions = AutoFilePermissions }
            .ApplyRows(binding, schema, rows, ct, progress);
    }

    /// <summary>
    /// Отдельное подключение к той же базе — для работы, которую ведет не интерфейс: шаг
    /// сценария выполняет действие расширения в фоне, а расширение пишет записи само
    /// (<c>Extensions/ExtensionAction.cs</c>).
    ///
    /// Подключение открывается ОДНО на всю работу, а не на строку: сообщений в пачке бывает
    /// несколько сотен, и подключение на каждое стоило бы дороже самой записи.
    ///
    /// Права ручной записи спрашиваются ЗДЕСЬ, на публичной точке входа, — как и у
    /// <see cref="ApplyRowsIsolated"/>. По умолчанию спрашивается право заводить записи;
    /// исходные ограничения автоподстановки передаются клону независимо от этого контракта.
    /// Для операции, выбирающей права по входным данным, checkInside передает живой кеш прав
    /// клону: его мутаторы проверяют свои права, а чтение проверяет само действие.
    /// </summary>
    /// <param name="collectionId">Коллекция, с которой будет работать вызванная работа.</param>
    /// <param name="work">Что сделать на отдельном подключении.</param>
    /// <param name="checkInside">Проверять права внутри операции вместо общего CreateRecord.</param>
    public T UseIsolated<T>(int collectionId, Func<ConstructorRepository, T> work, bool checkInside = false)
    {
        if (!checkInside) Demand(PermAction.CreateRecord, collectionId);

        using var ctx = new AppDbContext(db.DbPath);
        return work(new ConstructorRepository(ctx)
        {
            Permissions = checkInside ? Permissions : null,
            FileAutoPermissions = AutoFilePermissions,
        });
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
    /// <param name="records">Какие записи отправлять; <c>null</c> или пусто — всю коллекцию.
    /// Список приходит от точки вызова: человек выделил строки в таблице и позвал действие
    /// именно для них.</param>
    public OutgoingRows ReadOutgoingIsolated(OutgoingBinding binding, int limit,
                                             CancellationToken ct,
                                             IReadOnlyList<int>? records = null)
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

        // Колонки отбора — сверх уходящих: отбирать можно по свойству, которое наружу не идет
        // («Отправлено» = пусто), и это обычный случай, а не исключение. Помеченные свойства сюда
        // тоже не попадают: условие по закрытому полю не выносит значение наружу, но выносит
        // ЗНАНИЕ о нем — по тому, какие записи ушли, оно и восстанавливается.
        var extra = new List<ExportColumn>();

        foreach (var test in binding.Tests)
        {
            if (columns.Any(c => Same(c.Header, test.Column)) ||
                extra.Any(c => Same(c.Header, test.Column))) continue;

            if (available.Values.FirstOrDefault(c => Same(c.Header, test.Column)) is not { } found)
                return OutgoingRows.Bad(
                    $"свойства «{test.Column}», по которому идет отбор, в коллекции "
                    + $"«{collection}» нет — отбор надо собрать заново");

            var mark = EgressGate.CheckField(Confidentiality.FromConfig(found.ConfigJson));

            if (!mark.Allowed)
                return OutgoingRows.Bad(
                    $"отбор идет по свойству «{found.Header}»: {mark.Explanation}");

            extra.Add(found);
        }

        // Отбор применяется ДО потолка, иначе он ничего не решает: потолок считается по числу
        // прочитанных строк, и коллекция на сто тысяч записей отказывала бы и с условием,
        // отбирающим три.
        var all   = columns.Concat(extra).ToList();
        var tests = binding.Tests
                           .Select(t => (Test: t, At: all.FindIndex(c => Same(c.Header, t.Column))))
                           .ToList();

        var take = Math.Max(0, limit) + 1;

        var rows = records is { Count: > 0 }
            ? isolated.ChosenRows(binding.CollectionId, all, records, tests, take)
            : ReadExportRowsIsolated(binding.CollectionId, all, excludeHidden: false, ct)
              .Where(row => Fits(tests, row))
              .Take(take)
              .ToList();

        // Колонки отбора уходили только ради условий — обрезаем их, иначе они попали бы в тело.
        if (extra.Count > 0)
            rows = [.. rows.Select(row => row[..columns.Count])];

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

    /// <summary>
    /// Значения выбранных записей в порядке колонок. Порядок строк — тот же, что в таблице
    /// (Position, Id), а не тот, в каком человек их выделял: выделение мышью порядка не имеет, и
    /// отправить одни и те же записи двумя разными списками значило бы получить два разных тела.
    ///
    /// Записи не из этой коллекции отбрасываются молча: выделение живет в таблице, а коллекцию
    /// за это время могли переключить.
    ///
    /// <b>Отбор привязки действует и здесь</b>, хотя записи выбрал человек руками. Причина не в
    /// строгости: отбор входит в отпечаток направления, то есть в то, что подтвердили
    /// мастер-паролем. Пропусти его у кнопки — и ручная отправка уносила бы записи, которых
    /// согласованный обмен не касается, оставаясь при этом тем же самым направлением.
    /// </summary>
    private List<string[]> ChosenRows(int collectionId, IReadOnlyList<ExportColumn> columns,
                                      IReadOnlyList<int> records,
                                      IReadOnlyList<(ScenarioCondition Test, int At)> tests,
                                      int take)
    {
        // Отбор идет ПОСЛЕ чтения, а не срезает выборку в SQL: условия работают над значениями
        // полей, которые лежат в отдельной таблице и собираются пачкой ниже. Выделение человека
        // и так ограничено экраном, поэтому читать «на всякий случай больше» здесь нечего —
        // берем ровно то, что выделено, и отсеиваем.
        var chosen = db.DbRecords.AsNoTracking()
                       .Where(r => r.CollectionId == collectionId && records.Contains(r.Id))
                       .OrderBy(r => r.Position).ThenBy(r => r.Id)
                       .ToList();

        var values = GetFieldValuesBatch([.. chosen.Select(r => r.Id)]);

        var rows = new List<string[]>(chosen.Count);

        foreach (var record in chosen)
        {
            var mine = values.GetValueOrDefault(record.Id);
            var row  = new string[columns.Count];

            for (var i = 0; i < columns.Count; i++)
                row[i] = columns[i].Key == TitleKey
                    ? record.Title
                    : columns[i].FieldId is int field && mine is not null
                        ? mine.GetValueOrDefault(field, "")
                        : "";

            if (!Fits(tests, row)) continue;

            rows.Add(row);
            if (rows.Count >= take) break;
        }

        return rows;
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
