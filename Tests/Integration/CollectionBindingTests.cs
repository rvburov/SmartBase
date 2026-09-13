using SmartBase.Core.Importers;
using SmartBase.Core.Importers.TableSources;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Integrations;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Укладка ответа чужой программы в существующую коллекцию (<c>ConstructorRepository.ApplyRows</c>).
///
/// Обычный импорт заводит новую коллекцию; обмен так работать не может — он повторяется по
/// расписанию, и каждый запуск обязан ложиться в ту же таблицу. Отсюда и главные проверки:
/// повторный запуск с теми же данными НЕ плодит дубли, а неоднозначный ключ не дает править
/// запись наугад.
/// </summary>
public sealed class CollectionBindingTests
{
    /// <summary>
    /// Ключ базы данных в привязке. Укладка его не проверяет — репозиторий И ЕСТЬ база; ключ
    /// нужен экрану, чтобы выбрать, в какую базу нести ответ. Здесь он просто должен быть
    /// заполнен: непроставленный ключ привязка считает ошибкой.
    /// </summary>
    private const int Db = 1;

    /// <summary>Коллекция с двумя свойствами и привязка к ней по номеру.</summary>
    private static (int CollectionId, int NumberId, int StateId) Collection(ConstructorRepository repo)
    {
        var collection = SampleData.AddCollection(repo, "Заказы");
        var number     = SampleData.AddField(repo, collection.Id!.Value, "Номер");
        var state      = SampleData.AddField(repo, collection.Id!.Value, "Статус", position: 2000);

        return (collection.Id!.Value, number.Id!.Value, state.Id!.Value);
    }

    private static (ImportSchema Schema, List<ImportRow> Rows) Source(string json)
    {
        var source = new JsonTextSource(json);
        var schema = source.Analyze(null, CancellationToken.None);
        return (schema, [.. source.ReadRows(CancellationToken.None)]);
    }

    private static BindingResult Apply(
        ConstructorRepository repo, CollectionBinding binding, string json)
    {
        var (schema, rows) = Source(json);
        return repo.ApplyRows(binding, schema, rows, CancellationToken.None);
    }

    private static string Value(ConstructorRepository repo, int recordId, int fieldId) =>
        repo.GetFieldValues(recordId).TryGetValue(fieldId, out var value) ? value : "";

    // ── Обычная укладка ───────────────────────────────────────────────────────

    [Fact]
    public void Rows_become_records_with_title_and_fields()
    {
        using var db = new TempDatabase();
        var (collectionId, numberId, stateId) = Collection(db.Constructor);

        var binding = new CollectionBinding(Db, collectionId,
        [
            new BindingColumn("Название", null),
            new BindingColumn("Номер",    numberId),
            new BindingColumn("Статус",   stateId),
        ]);

        var result = Apply(db.Constructor, binding, """
            [{"Название":"Молоток","Номер":"А-1","Статус":"новый"},
             {"Название":"Отвертка","Номер":"А-2","Статус":"новый"}]
            """);

        Assert.Equal(2, result.Created);
        Assert.Equal(0, result.Updated);

        var records = db.Constructor.GetRecords(collectionId);
        Assert.Equal(["Молоток", "Отвертка"], records.Select(r => r.Title));
        Assert.Equal("А-1", Value(db.Constructor, records[0].Id!.Value, numberId));
        Assert.Equal("новый", Value(db.Constructor, records[0].Id!.Value, stateId));
    }

    /// <summary>Колонку можно и не переносить: чужая программа отдает лишнее почти всегда.</summary>
    [Fact]
    public void Columns_outside_the_binding_are_not_transferred()
    {
        using var db = new TempDatabase();
        var (collectionId, numberId, _) = Collection(db.Constructor);

        var binding = new CollectionBinding(Db, collectionId,
            [new BindingColumn("Название", null), new BindingColumn("Номер", numberId)]);

        Apply(db.Constructor, binding, """[{"Название":"Молоток","Номер":"А-1","Лишнее":"мусор"}]""");

        var record = Assert.Single(db.Constructor.GetRecords(collectionId));
        Assert.Single(db.Constructor.GetFieldValues(record.Id!.Value));
    }

    // ── Повторный запуск ──────────────────────────────────────────────────────

    /// <summary>
    /// Главное свойство обмена: второй запуск с теми же данными не плодит дубли, а обновляет
    /// заведенное. Без этого ежечасная выгрузка за сутки превращает коллекцию в помойку.
    /// </summary>
    [Fact]
    public void Second_run_updates_instead_of_duplicating()
    {
        using var db = new TempDatabase();
        var (collectionId, numberId, stateId) = Collection(db.Constructor);

        var binding = new CollectionBinding(Db, collectionId,
        [
            new BindingColumn("Название", null),
            new BindingColumn("Номер",    numberId),
            new BindingColumn("Статус",   stateId),
        ],
        MatchColumn: "Номер");

        Apply(db.Constructor, binding, """[{"Название":"Молоток","Номер":"А-1","Статус":"новый"}]""");

        var second = Apply(db.Constructor, binding,
            """[{"Название":"Молоток большой","Номер":"А-1","Статус":"отгружен"}]""");

        Assert.Equal(0, second.Created);
        Assert.Equal(1, second.Updated);

        var record = Assert.Single(db.Constructor.GetRecords(collectionId));
        Assert.Equal("Молоток большой", record.Title);
        Assert.Equal("отгружен", Value(db.Constructor, record.Id!.Value, stateId));
    }

    /// <summary>Сопоставлять можно и по названию — тогда ключ лежит в самой записи.</summary>
    [Fact]
    public void Matching_by_title_works_too()
    {
        using var db = new TempDatabase();
        var (collectionId, numberId, _) = Collection(db.Constructor);

        var binding = new CollectionBinding(Db, collectionId,
            [new BindingColumn("Название", null), new BindingColumn("Номер", numberId)],
            MatchColumn: "Название");

        Apply(db.Constructor, binding, """[{"Название":"Молоток","Номер":"А-1"}]""");
        var second = Apply(db.Constructor, binding, """[{"Название":"Молоток","Номер":"А-9"}]""");

        Assert.Equal(1, second.Updated);
        var record = Assert.Single(db.Constructor.GetRecords(collectionId));
        Assert.Equal("А-9", Value(db.Constructor, record.Id!.Value, numberId));
    }

    /// <summary>Один и тот же ключ дважды в ОДНОМ ответе — вторая строка обновляет первую.</summary>
    [Fact]
    public void Repeated_key_inside_one_answer_updates_the_new_record()
    {
        using var db = new TempDatabase();
        var (collectionId, numberId, stateId) = Collection(db.Constructor);

        var binding = new CollectionBinding(Db, collectionId,
        [
            new BindingColumn("Название", null),
            new BindingColumn("Номер",    numberId),
            new BindingColumn("Статус",   stateId),
        ],
        MatchColumn: "Номер");

        var result = Apply(db.Constructor, binding, """
            [{"Название":"Молоток","Номер":"А-1","Статус":"новый"},
             {"Название":"Молоток","Номер":"А-1","Статус":"отгружен"}]
            """);

        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Updated);

        var record = Assert.Single(db.Constructor.GetRecords(collectionId));
        Assert.Equal("отгружен", Value(db.Constructor, record.Id!.Value, stateId));
    }

    // ── Что делать с найденным и ненайденным ──────────────────────────────────

    [Fact]
    public void Update_can_be_switched_off()
    {
        using var db = new TempDatabase();
        var (collectionId, numberId, stateId) = Collection(db.Constructor);

        var columns = new BindingColumn[]
        {
            new("Название", null), new("Номер", numberId), new("Статус", stateId),
        };

        Apply(db.Constructor, new CollectionBinding(Db, collectionId, columns, MatchColumn: "Номер"),
            """[{"Название":"Молоток","Номер":"А-1","Статус":"новый"}]""");

        var result = Apply(db.Constructor,
            new CollectionBinding(Db, collectionId, columns, MatchColumn: "Номер", UpdateExisting: false),
            """[{"Название":"Молоток","Номер":"А-1","Статус":"отгружен"}]""");

        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Updated);

        var record = Assert.Single(db.Constructor.GetRecords(collectionId));
        Assert.Equal("новый", Value(db.Constructor, record.Id!.Value, stateId));
    }

    [Fact]
    public void Creation_can_be_switched_off()
    {
        using var db = new TempDatabase();
        var (collectionId, numberId, _) = Collection(db.Constructor);

        var result = Apply(db.Constructor,
            new CollectionBinding(Db, collectionId,
                [new BindingColumn("Название", null), new BindingColumn("Номер", numberId)],
                MatchColumn: "Номер", CreateMissing: false),
            """[{"Название":"Молоток","Номер":"А-1"}]""");

        Assert.Equal(1, result.Skipped);
        Assert.Empty(db.Constructor.GetRecords(collectionId));
    }

    /// <summary>
    /// Пустой ключ не заводит запись: иначе каждый следующий запуск плодил бы для нее дубль.
    /// </summary>
    [Fact]
    public void Empty_key_is_counted_and_skipped()
    {
        using var db = new TempDatabase();
        var (collectionId, numberId, _) = Collection(db.Constructor);

        var result = Apply(db.Constructor,
            new CollectionBinding(Db, collectionId,
                [new BindingColumn("Название", null), new BindingColumn("Номер", numberId)],
                MatchColumn: "Номер"),
            """[{"Название":"Молоток","Номер":""},{"Название":"Отвертка","Номер":"А-2"}]""");

        Assert.Equal(1, result.Empty);
        Assert.Equal(1, result.Created);
        Assert.Single(db.Constructor.GetRecords(collectionId));
    }

    /// <summary>
    /// В коллекции две записи с одинаковым ключом — править одну из них наугад хуже, чем не
    /// трогать ни одной и сказать об этом.
    /// </summary>
    [Fact]
    public void Ambiguous_key_touches_nothing()
    {
        using var db = new TempDatabase();
        var (collectionId, numberId, stateId) = Collection(db.Constructor);

        foreach (var title in new[] { "Первый", "Второй" })
        {
            var record = SampleData.AddRecord(db.Constructor, collectionId, title);
            db.Constructor.SetFieldValue(record.Id!.Value, numberId, "А-1");
            db.Constructor.SetFieldValue(record.Id!.Value, stateId, "новый");
        }

        var result = Apply(db.Constructor,
            new CollectionBinding(Db, collectionId,
                [new BindingColumn("Название", null), new BindingColumn("Номер", numberId),
                 new BindingColumn("Статус", stateId)],
                MatchColumn: "Номер"),
            """[{"Название":"Молоток","Номер":"А-1","Статус":"отгружен"}]""");

        Assert.Equal(1, result.Ambiguous);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Created);

        foreach (var record in db.Constructor.GetRecords(collectionId))
            Assert.Equal("новый", Value(db.Constructor, record.Id!.Value, stateId));
    }

    /// <summary>Без ключа сопоставления каждая строка ответа — новая запись.</summary>
    [Fact]
    public void Without_a_key_every_row_is_a_new_record()
    {
        using var db = new TempDatabase();
        var (collectionId, numberId, _) = Collection(db.Constructor);

        var binding = new CollectionBinding(Db, collectionId,
            [new BindingColumn("Название", null), new BindingColumn("Номер", numberId)]);

        Apply(db.Constructor, binding, """[{"Название":"Молоток","Номер":"А-1"}]""");
        Apply(db.Constructor, binding, """[{"Название":"Молоток","Номер":"А-1"}]""");

        Assert.Equal(2, db.Constructor.GetRecords(collectionId).Count);
    }

    // ── Привязка, которая больше не подходит ──────────────────────────────────

    /// <summary>
    /// Колонка исчезла из ответа — это ошибка, а не тихая пустота: иначе поле обнулялось бы при
    /// каждом запуске, и заметили бы это нескоро.
    /// </summary>
    [Fact]
    public void Column_missing_from_the_answer_is_an_error()
    {
        using var db = new TempDatabase();
        var (collectionId, numberId, _) = Collection(db.Constructor);

        var error = Assert.Throws<ImportException>(() => Apply(db.Constructor,
            new CollectionBinding(Db, collectionId,
                [new BindingColumn("Название", null), new BindingColumn("Номер", numberId)]),
            """[{"Название":"Молоток"}]"""));

        Assert.Contains("Номер", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Свойство удалили из коллекции — привязку надо поправить, а не молчать.</summary>
    [Fact]
    public void Field_removed_from_the_collection_is_an_error()
    {
        using var db = new TempDatabase();
        var (collectionId, numberId, _) = Collection(db.Constructor);

        db.Constructor.DeleteField(numberId);

        var error = Assert.Throws<ImportException>(() => Apply(db.Constructor,
            new CollectionBinding(Db, collectionId,
                [new BindingColumn("Название", null), new BindingColumn("Номер", numberId)]),
            """[{"Название":"Молоток","Номер":"А-1"}]"""));

        Assert.Contains("Номер", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_collection_is_an_error()
    {
        using var db = new TempDatabase();

        var error = Assert.Throws<ImportException>(() => Apply(db.Constructor,
            new CollectionBinding(Db, 404, [new BindingColumn("Название", null)]),
            """[{"Название":"Молоток"}]"""));

        Assert.Contains("Коллекция не найдена", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Негодная привязка до записи не доходит.</summary>
    [Fact]
    public void Broken_binding_is_refused()
    {
        using var db = new TempDatabase();
        var (collectionId, numberId, _) = Collection(db.Constructor);

        var error = Assert.Throws<ImportException>(() => Apply(db.Constructor,
            new CollectionBinding(Db, collectionId,
                [new BindingColumn("Название", null), new BindingColumn("Номер", numberId)],
                MatchColumn: "Нет такой"),
            """[{"Название":"Молоток","Номер":"А-1"}]"""));

        Assert.Contains("не переносится", error.Message, StringComparison.Ordinal);
    }

    // ── Отдельное подключение и права ─────────────────────────────────────────

    /// <summary>
    /// Укладка с фонового потока идет на СВОЕМ подключении: общий контекст в это время
    /// занят интерфейсом. Здесь проверяется, что путь тот же и записи доходят.
    /// </summary>
    [Fact]
    public void Isolated_apply_writes_the_same_way()
    {
        using var db = new TempDatabase();
        var (collectionId, numberId, _) = Collection(db.Constructor);

        var (schema, rows) = Source("""
            [{"Название":"Молоток","Номер":"А-1"},
             {"Название":"Отвертка","Номер":"А-2"}]
            """);

        var result = db.Constructor.ApplyRowsIsolated(
            new CollectionBinding(Db, collectionId,
                [new BindingColumn("Название", null), new BindingColumn("Номер", numberId)]),
            schema, rows, CancellationToken.None);

        Assert.Equal(2, result.Created);

        var titles = db.Constructor.GetRecords(collectionId).Select(r => r.Title).ToList();
        Assert.Contains("Молоток", titles);
        Assert.Contains("Отвертка", titles);
    }

    /// <summary>
    /// Права спрашиваются ДО первой записи, а не по ходу: отказ на середине оставил бы
    /// коллекцию наполовину обновленной, а обмен повторяется по расписанию — разбираться в
    /// таком состоянии потом некому.
    /// </summary>
    [Fact]
    public void Rights_are_asked_before_the_first_record()
    {
        using var db = new TempDatabase();
        var (collectionId, numberId, _) = Collection(db.Constructor);

        var userId = db.Permissions.CurrentUserId;
        db.Permissions.SetMask(userId, null, (int)PermAction.ViewCollection);

        var me = db.Context.DbUsers.First(u => u.UserKey == CurrentUser.Key);
        me.IsAdmin    = false;
        me.IsApproved = true;
        db.Context.SaveChanges();
        db.Permissions.Reload(notify: false);

        Assert.Throws<PermissionDeniedException>(() => Apply(db.Constructor,
            new CollectionBinding(Db, collectionId,
                [new BindingColumn("Название", null), new BindingColumn("Номер", numberId)]),
            """[{"Название":"Молоток","Номер":"А-1"},{"Название":"Отвертка","Номер":"А-2"}]"""));

        // Ни одной записи: отказ случился до первой, а не на второй.
        Assert.Empty(db.Constructor.GetRecords(collectionId));
    }

    // ── Скорость ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Уложенные записи НЕ копятся в трекере EF.
    ///
    /// Проверка выглядит въедливой, а сторожит она самую дорогую ошибку, какая была в этом коде.
    /// Каждая уложенная запись оставляет за собой отслеживаемые сущности — саму запись, ее
    /// значения, строки журнала изменений, — а EF на КАЖДОМ <c>SaveChanges</c> обходит весь
    /// накопленный список. Пока их не очищали, время росло квадратично: замер на этой машине дал
    /// 13 мс на запись при 250 записях, 42 мс при 2000 и 104 мс при 5000 — то есть пять тысяч
    /// записей укладывались восемь с половиной минут, а сто тысяч ушли бы в десятки часов.
    ///
    /// После очистки — ровно 3,5 мс на запись и на пяти тысячах, и на двадцати: рост исчез.
    ///
    /// Замером это не проверить: цифры на разных машинах разные, а тест, падающий от чужой
    /// нагрузки, выключают первым. Поэтому проверяется ПРИЧИНА: после каждой уложенной строки
    /// трекер пуст. Вернись сюда накопление — счетчик пойдет вверх с первой же записи.
    /// </summary>
    [Fact]
    public void Applied_rows_do_not_pile_up_in_the_tracker()
    {
        using var db = new TempDatabase();
        var (collectionId, numberId, stateId) = Collection(db.Constructor);

        var binding = new CollectionBinding(Db, collectionId,
        [
            new BindingColumn("Название", null),
            new BindingColumn("Номер",    numberId),
            new BindingColumn("Статус",   stateId),
        ]);

        var json = "[" + string.Join(",", Enumerable.Range(0, 50).Select(i =>
            $$"""{"Название":"Заказ {{i}}","Номер":"А-{{i}}","Статус":"новый"}""")) + "]";

        var (schema, rows) = Source(json);

        var most = 0;
        var seen = 0;

        db.Constructor.ApplyRows(binding, schema, rows, CancellationToken.None,
            new SpyProgress(() =>
            {
                seen++;
                most = Math.Max(most, db.Context.ChangeTracker.Entries().Count());
            }));

        Assert.Equal(50, seen);
        Assert.Equal(0, most);
    }

    /// <summary>
    /// Синхронный отчет о ходе работы. <see cref="Progress{T}"/> здесь не годится: он уносит вызов
    /// в другой поток, и заглянуть в трекер РОВНО между строками им нельзя.
    /// </summary>
    private sealed class SpyProgress(Action look) : IProgress<int>
    {
        public void Report(int value) => look();
    }
}
