using SmartBase.Core.Exporters;
using SmartBase.Core.Models;
using SmartBase.Integrations;

namespace SmartBase.Database.Repositories;

/// <summary>
/// Опоры шагов сценария «создать запись» и «экспорт выборки» (docs/ROADMAP-API.md, шаг 11).
///
/// Оба метода — на ОТДЕЛЬНОМ подключении: шаги идут в фоне, а общий контекст принадлежит
/// интерфейсу. Права спрашиваются на публичной точке входа, как у всех обменных оберток:
/// изолированный клон репозитория прав не несет.
/// </summary>
public partial class ConstructorRepository
{
    // ── Создание записей ──────────────────────────────────────────────────────

    /// <summary>
    /// Чем кончилось создание записей.
    /// </summary>
    /// <param name="Created">Заведено записей.</param>
    /// <param name="Updated">Обновлено найденных по ключу.</param>
    /// <param name="Skipped">Пропущено: ключ пуст либо найденное трогать не велено.</param>
    /// <param name="Ambiguous">Пропущено из-за неоднозначности: под ключ подошло несколько
    /// записей, и какую из них править — программа не угадывает.</param>
    /// <param name="Collection">Имя коллекции — для строки отчета.</param>
    /// <param name="Trouble">Почему не вышло; <c>null</c> — все записано.</param>
    public sealed record MadeRecords(int Created, string Collection, string? Trouble,
                                     int Updated = 0, int Skipped = 0, int Ambiguous = 0)
    {
        public static MadeRecords Bad(string trouble) => new(0, "", trouble);

        /// <summary>Итог словами: только то, что случилось, — нули не перечисляются.</summary>
        public string Describe()
        {
            var parts = new List<string>();

            if (Created   > 0) parts.Add($"заведено {Created}");
            if (Updated   > 0) parts.Add($"обновлено {Updated}");
            if (Skipped   > 0) parts.Add($"пропущено {Skipped}");
            if (Ambiguous > 0) parts.Add($"неоднозначных {Ambiguous}");

            return parts.Count == 0 ? "изменений нет" : string.Join(", ", parts);
        }
    }

    /// <summary>
    /// Записать строку «имя поля → значение» в коллекцию: завести новую запись либо обновить
    /// найденную по ключу. Пишется ОБЫЧНЫМ путем — <c>AddRecord</c> и <c>SetFieldValue</c>: за
    /// него достается журнал изменений, календарный и задачный индексы — все, что видит человек
    /// на других экранах.
    ///
    /// <b>Все имена полей проверяются ДО первой записи.</b> Опечатка, найденная на сотой
    /// строке, оставила бы девяносто девять записей и вопрос, какие из них лишние. Поле зовется
    /// ИМЕНЕМ, а не ключом, — тем же, что видно в таблице; переименовали поле — шаг остановится
    /// и назовет, чего не хватает.
    ///
    /// <b>Ключ пуст — только создание</b>, и это прежнее поведение шага: раздел 2 обещал
    /// «создать/обновить запись», а сценарий умел одно создание, и «разобрать сообщение →
    /// обновить статус» заводило дубли при каждом запуске.
    ///
    /// Правила сопоставления те же, что у укладки ответа (<c>CollectionBinding</c>), и это не
    /// совпадение — вопрос один и тот же:
    ///
    /// <list type="bullet">
    ///   <item><b>Пустой ключ в строке</b> — пропуск. Завести такую запись значило бы плодить
    ///   дубли при каждом следующем запуске.</item>
    ///   <item><b>Под ключ подошло несколько записей</b> — пропуск и счет отдельной цифрой.
    ///   Какую из них править, программа не угадывает: угаданная неверно правка тихо портит
    ///   чужую запись.</item>
    ///   <item><b>Не нашлось ничего</b> — заводится новая, если это разрешено
    ///   <paramref name="createMissing"/>. Запрет нужен обмену, где запись обязана уже
    ///   существовать: там появление новой означает, что ключ разошелся, и молча заводить ее —
    ///   худшее, что можно сделать.</item>
    /// </list>
    ///
    /// <b>Пустое значение при ОБНОВЛЕНИИ очищает поле</b>, в отличие от создания, где очищать
    /// нечего. Иначе «статус стал пустым» нечем было бы передать.
    /// </summary>
    /// <param name="collectionId">Коллекция, в которую пишем.</param>
    /// <param name="rows">Строки «имя поля → значение».</param>
    /// <param name="ct">Отмена.</param>
    /// <param name="matchField">Имя поля-ключа; пусто — только создание. Название записи
    /// называется так же, как оно подписано в коллекции.</param>
    /// <param name="createMissing">Заводить ли запись, которой не нашлось.</param>
    public MadeRecords WriteRecordsIsolated(int collectionId,
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows, CancellationToken ct,
        string matchField = "", bool createMissing = true)
    {
        var matching = !string.IsNullOrWhiteSpace(matchField);

        // Права спрашиваются по тому, что шаг СОБИРАЕТСЯ делать. Обновление без права правки —
        // это правка чужих данных мимо запрета, а создание без права создания — обход его же.
        if (matching)      Demand(PermAction.EditRecord,   collectionId);
        if (createMissing) Demand(PermAction.CreateRecord, collectionId);

        using var ctx = new AppDbContext(db.DbPath);
        var repo = new ConstructorRepository(ctx);

        var collection = repo.CollectionName(collectionId);
        if (collection.Length == 0)
            return MadeRecords.Bad("коллекции, в которую пишет шаг, больше нет");

        var titleLabel = repo.GetSystemFieldNames(collectionId)
                             .GetValueOrDefault(TitleKey, "Название");

        // Вычисляемые и связи обменом не заполняются — ровно как у входящего сообщения.
        var known = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in repo.GetFields(collectionId))
            if (field.Id is int id && !FieldTypeInfo.IsComputed(field.FieldType)
                                   && !FieldTypeInfo.IsRelation(field.FieldType))
                known.TryAdd(field.Name.Trim(), id);

        foreach (var name in rows.SelectMany(r => r.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Same(name, titleLabel) || known.ContainsKey(name.Trim())) continue;

            return MadeRecords.Bad(
                $"поля «{name}» в коллекции «{collection}» нет; есть: "
                + string.Join(", ", new[] { titleLabel }.Concat(known.Keys)));
        }

        // Ключ ищется среди тех же имен, что и поля: название записи либо обычное свойство.
        // Названо имя, которого нет, — отказ словами ДО первой записи, как и у полей.
        int? keyFieldId = null;

        if (matching)
        {
            var key = matchField.Trim();

            if (Same(key, titleLabel))                          keyFieldId = null;
            else if (known.TryGetValue(key, out var id))         keyFieldId = id;
            else return MadeRecords.Bad(
                $"поля-ключа «{key}» в коллекции «{collection}» нет; есть: "
                + string.Join(", ", new[] { titleLabel }.Concat(known.Keys)));
        }

        var (index, duplicates) = matching
            ? repo.KeyIndex(collectionId, keyFieldId)
            : ([], []);

        var position  = repo.NextPosition(collectionId);
        var created   = 0;
        var updated   = 0;
        var skipped   = 0;
        var ambiguous = 0;

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            var title = row.FirstOrDefault(v => Same(v.Key, titleLabel)).Value ?? "";

            if (matching)
            {
                var key = (keyFieldId is int fieldId
                    ? row.FirstOrDefault(v => known.TryGetValue(v.Key.Trim(), out var at)
                                           && at == fieldId).Value
                    : title) ?? "";

                key = key.Trim();

                // Пустой ключ — сопоставлять не по чему. Завести такую запись значило бы плодить
                // дубли при каждом следующем запуске.
                if (key.Length == 0)         { skipped++;   continue; }
                if (duplicates.Contains(key)) { ambiguous++; continue; }

                if (index.TryGetValue(key, out var found))
                {
                    Fill(repo, found, row, known, titleLabel, title, clearing: true);
                    updated++;
                    continue;
                }

                if (!createMissing) { skipped++; continue; }
            }

            var record = repo.AddRecord(new DbRecord
            {
                CollectionId = collectionId,
                Title        = title,
                Position     = position,
            });

            position += 1000;

            Fill(repo, record.Id!.Value, row, known, titleLabel, title, clearing: false);

            // Заведенное в этом же запуске попадает в индекс: две строки с одним ключом в одной
            // пачке — это одна запись, обновленная дважды, а не два дубля.
            if (matching)
            {
                var key = (keyFieldId is int fieldId
                    ? row.FirstOrDefault(v => known.TryGetValue(v.Key.Trim(), out var at)
                                           && at == fieldId).Value
                    : title) ?? "";

                if (key.Trim() is { Length: > 0 } fresh) index[fresh] = record.Id!.Value;
            }

            created++;
        }

        return new MadeRecords(created, collection, null, updated, skipped, ambiguous);
    }

    /// <summary>
    /// Разложить значения строки по полям записи.
    /// </summary>
    /// <param name="clearing">Писать ли пустые значения. При ОБНОВЛЕНИИ — да: иначе «статус стал
    /// пустым» нечем передать. При создании — нет: очищать у новой записи нечего.</param>
    private static void Fill(ConstructorRepository repo, int recordId,
                             IReadOnlyDictionary<string, string> row,
                             IReadOnlyDictionary<string, int> known,
                             string titleLabel, string title, bool clearing)
    {
        if (clearing) repo.SetRecordTitle(recordId, title);

        foreach (var (name, value) in row)
        {
            if (Same(name, titleLabel))                continue;
            if (!clearing && value.Length == 0)         continue;

            repo.SetFieldValue(recordId, known[name.Trim()], value);
        }
    }

    // ── Источник выгрузки ─────────────────────────────────────────────────────

    /// <summary>
    /// Что уходит в файл выгрузки.
    /// </summary>
    /// <param name="Columns">Видимые колонки коллекции в порядке таблицы.</param>
    /// <param name="Collection">Имя коллекции — для имени файла и следа.</param>
    /// <param name="Label">Самая строгая метка среди базы и колонок — в след журнала
    /// безопасности.</param>
    /// <param name="Trouble">Почему выгружать нельзя; <c>null</c> — можно.</param>
    public sealed record ExportSource(IReadOnlyList<ExportColumn> Columns, string Collection,
                                      ConfidentialityLabel Label, string? Trouble)
    {
        public static ExportSource Bad(string trouble) =>
            new([], "", ConfidentialityLabel.Open, trouble);
    }

    /// <summary>
    /// Колонки коллекции под автоматическую выгрузку — с обеими метками контура вторым рубежом,
    /// как у отправки (<see cref="ReadOutgoingIsolated"/>). Берутся ВИДИМЫЕ колонки — то, что
    /// человек видит в таблице; помеченная среди них останавливает шаг словами, а спрятать ее
    /// из выгрузки можно тем же способом, что и с экрана, — скрыв колонку.
    /// </summary>
    public ExportSource ExportSourceIsolated(int collectionId)
    {
        Demand(PermAction.ViewCollection, collectionId);

        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyPerformancePragmas();
        var repo = new ConstructorRepository(ctx);

        var label  = repo.GetDatabaseLabel();
        var closed = EgressGate.CheckDatabase(label);
        if (!closed.Allowed) return ExportSource.Bad(closed.Explanation);

        var collection = repo.CollectionName(collectionId);
        if (collection.Length == 0)
            return ExportSource.Bad("коллекции, которую выгружает шаг, больше нет");

        var columns = repo.GetExportColumns(collectionId, includeHidden: false);
        if (columns.Count == 0)
            return ExportSource.Bad($"в коллекции «{collection}» нет видимых колонок");

        var strictest = label;

        foreach (var column in columns)
        {
            var mark     = Confidentiality.FromConfig(column.ConfigJson);
            var decision = EgressGate.CheckField(mark);

            if (!decision.Allowed)
                return ExportSource.Bad($"свойство «{column.Header}»: {decision.Explanation}");

            strictest = Confidentiality.Strictest(strictest, mark);
        }

        return new ExportSource(columns, collection, strictest, null);
    }

    private static bool Same(string? left, string? right) =>
        string.Equals((left ?? "").Trim(), (right ?? "").Trim(),
                      StringComparison.OrdinalIgnoreCase);
}
