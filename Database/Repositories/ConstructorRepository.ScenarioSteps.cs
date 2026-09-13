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
    /// <param name="Collection">Имя коллекции — для строки отчета.</param>
    /// <param name="Trouble">Почему не вышло; <c>null</c> — все заведено.</param>
    public sealed record MadeRecords(int Created, string Collection, string? Trouble)
    {
        public static MadeRecords Bad(string trouble) => new(0, "", trouble);
    }

    /// <summary>
    /// Завести запись на каждую строку «имя поля → значение». Пишется ОБЫЧНЫМ путем —
    /// <c>AddRecord</c> и <c>SetFieldValue</c>: за него достается журнал изменений, календарный
    /// и задачный индексы — все, что видит человек на других экранах.
    ///
    /// <b>Все имена полей проверяются ДО первой записи.</b> Опечатка, найденная на сотой
    /// строке, оставила бы девяносто девять записей и вопрос, какие из них лишние. Поле зовется
    /// ИМЕНЕМ, а не ключом, — тем же, что видно в таблице; переименовали поле — шаг остановится
    /// и назовет, чего не хватает.
    /// </summary>
    public MadeRecords CreateRecordsIsolated(int collectionId,
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows, CancellationToken ct)
    {
        Demand(PermAction.CreateRecord, collectionId);

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

        var position = repo.NextPosition(collectionId);
        var created  = 0;

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            var record = repo.AddRecord(new DbRecord
            {
                CollectionId = collectionId,
                Title        = row.FirstOrDefault(v => Same(v.Key, titleLabel)).Value ?? "",
                Position     = position,
            });

            position += 1000;

            foreach (var (name, value) in row)
            {
                // Пустое значение при СОЗДАНИИ не пишется: очищать у новой записи нечего.
                if (Same(name, titleLabel) || value.Length == 0) continue;

                repo.SetFieldValue(record.Id!.Value, known[name.Trim()], value);
            }

            created++;
        }

        return new MadeRecords(created, collection, null);
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
