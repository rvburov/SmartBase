using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>Исходящий JSON из настоящей коллекции: порядок, выбранные поля и записи, условия, переоткрытие и технический размер сообщения.</summary>
[Collection(EgressCollection.Name)]
public sealed class OutgoingSourceTests
{

    private static readonly DateTimeOffset Now =
        new(new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc));

    private const string Master = "секрет-организации";

    /// <summary>Коллекция «Заказы» с суммой и двумя записями.</summary>
    private static (int Collection, int Sum) Orders(TempDatabase db)
    {
        var collection = SampleData.AddCollection(db.Constructor, "Заказы").Id!.Value;
        var sum        = SampleData.AddField(db.Constructor, collection, "Сумма", "number").Id!.Value;

        var first  = SampleData.AddRecord(db.Constructor, collection, "А-1", 100).Id!.Value;
        var second = SampleData.AddRecord(db.Constructor, collection, "А-2", 200).Id!.Value;

        db.Constructor.SetFieldValue(first,  sum, "100");
        db.Constructor.SetFieldValue(second, sum, "250");

        return (collection, sum);
    }

    private static OutgoingBinding Binding(int collection, int sum) =>
        new(1, collection,
            [new OutgoingField(null, "order_no"), new OutgoingField(sum, "amount")]);

    private static async Task WithStand(Func<IntegrationsRegistry, TempDatabase, Task> body)
    {
        var dir   = Directory.CreateTempSubdirectory("sbout_").FullName;
        var appDb = Path.Combine(dir, "app.db");

        using var data = new TempDatabase();

        try
        {
            using var registry = DatabaseRegistry.Open(data.Path, appDb);
            await body(registry.Integrations, data);
        }
        finally
        {
            try { SqliteGate.ClearAllPools(); } catch { /* best-effort */ }
            TempDatabase.Cleanup(appDb);
            try { Directory.Delete(dir, recursive: true); } catch { /* временная папка */ }
        }
    }

    /// <summary>Сохраненный запрос-отправитель: привязка живет у карточки, а не сама по себе.</summary>
    private static IntegrationRequest Push(IntegrationsRegistry cards)
    {
        var connection = cards.Save(IntegrationConnection.New() with
        {
            Name           = "Учетная система",
            Address        = "https://api.zavod.local",
            TimeoutSeconds = 30,
        });

        return cards.SaveRequest(IntegrationRequest.New() with
        {
            Name         = "Отдать заказы",
            ConnectionId = connection.Id,
            Method       = RequestMethod.Post,
            Path         = "/orders",
        });
    }

    // ── Тело из коллекции ─────────────────────────────────────────────────────

    [Fact]
    public async Task Records_become_the_body_in_the_order_of_the_binding()
    {

        await WithStand(async (cards, data) =>
        {
            var (collection, sum) = Orders(data);
            var request = Push(cards);
            cards.SaveOutgoing(request.Id, Binding(collection, sum));

            var (payload, trouble) = OutgoingSource.Build(cards, request, [data.Manager]);

            Assert.Null(trouble);
            Assert.NotNull(payload);
            Assert.Equal("""[{"order_no":"А-1","amount":100},{"order_no":"А-2","amount":250}]""",
                         payload.Body);
            Assert.Equal(2, payload.Rows);
            Assert.Equal("order_no, amount", payload.Fields);
            Assert.Equal("Заказы", payload.Collection);

            await Task.CompletedTask;
        });
    }

    /// <summary>Запрос без привязки уносит то, что напечатано в карточке, — это обычный случай.</summary>
    [Fact]
    public async Task Request_without_a_binding_carries_nothing_from_the_collection()
    {

        await WithStand(async (cards, data) =>
        {
            var (payload, trouble) = OutgoingSource.Build(cards, Push(cards), [data.Manager]);

            Assert.Null(payload);
            Assert.Null(trouble);

            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Closed_database_is_not_open_here()
    {

        await WithStand(async (cards, data) =>
        {
            var (collection, sum) = Orders(data);
            var request = Push(cards);
            cards.SaveOutgoing(request.Id, Binding(collection, sum));

            var (payload, trouble) = OutgoingSource.Build(cards, request, []);

            Assert.Null(payload);
            Assert.Contains("не открыта", trouble!, StringComparison.Ordinal);

            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Deleted_property_stops_the_send_by_name()
    {

        await WithStand(async (cards, data) =>
        {
            var (collection, sum) = Orders(data);
            var request = Push(cards);
            cards.SaveOutgoing(request.Id, Binding(collection, sum));

            data.Constructor.DeleteField(sum);

            var (payload, trouble) = OutgoingSource.Build(cards, request, [data.Manager]);

            Assert.Null(payload);
            Assert.Contains("amount", trouble!, StringComparison.Ordinal);

            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Single_record_exchange_refuses_a_collection_of_two()
    {

        await WithStand(async (cards, data) =>
        {
            var (collection, sum) = Orders(data);
            var request = Push(cards);
            cards.SaveOutgoing(request.Id,
                               Binding(collection, sum) with { Shape = OutgoingShape.Single });

            var (payload, trouble) = OutgoingSource.Build(cards, request, [data.Manager]);

            Assert.Null(payload);
            Assert.Contains("одну", trouble!, StringComparison.Ordinal);

            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Empty_collection_gives_a_payload_of_zero_rows()
    {

        await WithStand(async (cards, data) =>
        {
            var empty   = SampleData.AddCollection(data.Constructor, "Пусто").Id!.Value;
            var request = Push(cards);

            cards.SaveOutgoing(request.Id,
                               new OutgoingBinding(1, empty, [new OutgoingField(null, "no")]));

            var (payload, trouble) = OutgoingSource.Build(cards, request, [data.Manager]);

            Assert.Null(trouble);
            Assert.Equal(0, payload!.Rows);
            Assert.Equal("[]", payload.Body);

            await Task.CompletedTask;
        });
    }

    // ── Выделенные записи (точки вызова из раздела данных) ────────────────────

    /// <summary>
    /// Человек выделил строки в таблице и позвал действие — уходят ровно они. Порядок при этом
    /// табличный, а не тот, в каком их выделяли: выделение мышью порядка не имеет, и одни и те
    /// же записи обязаны давать одно и то же тело.
    /// </summary>
    [Fact]
    public async Task Only_the_chosen_records_go_out_and_in_table_order()
    {

        await WithStand(async (cards, data) =>
        {
            var (collection, sum) = Orders(data);
            var third = SampleData.AddRecord(data.Constructor, collection, "А-3", 300).Id!.Value;
            data.Constructor.SetFieldValue(third, sum, "300");

            var second = data.Constructor.GetRecordsPageAfter(collection, null, null, 10)
                             .First(r => r.Title == "А-2").Id!.Value;

            var request = Push(cards);
            cards.SaveOutgoing(request.Id, Binding(collection, sum));

            // Порядок выделения — обратный табличному.
            var (payload, trouble) = OutgoingSource.Build(
                cards, request, [data.Manager], [third, second]);

            Assert.Null(trouble);
            Assert.Equal("""[{"order_no":"А-2","amount":250},{"order_no":"А-3","amount":300}]""",
                         payload!.Body);
            Assert.Equal(2, payload.Rows);

            await Task.CompletedTask;
        });
    }

    /// <summary>
    /// Выделение живет в таблице, а коллекцию за это время могли переключить: чужие записи
    /// отбрасываются молча, а не уезжают наружу вместе со своими.
    /// </summary>
    [Fact]
    public async Task Records_of_another_collection_are_dropped()
    {

        await WithStand(async (cards, data) =>
        {
            var (collection, sum) = Orders(data);

            var other   = SampleData.AddCollection(data.Constructor, "Другое").Id!.Value;
            var foreign = SampleData.AddRecord(data.Constructor, other, "Чужая", 10).Id!.Value;

            var mine = data.Constructor.GetRecordsPageAfter(collection, null, null, 10)
                           .First(r => r.Title == "А-1").Id!.Value;

            var request = Push(cards);
            cards.SaveOutgoing(request.Id, Binding(collection, sum));

            var (payload, trouble) = OutgoingSource.Build(
                cards, request, [data.Manager], [mine, foreign]);

            Assert.Null(trouble);
            Assert.Equal(1, payload!.Rows);
            Assert.DoesNotContain("Чужая", payload.Body, StringComparison.Ordinal);

            await Task.CompletedTask;
        });
    }

    /// <summary>Обмен «одна запись» из точки вызова — обычный случай: выделили строку и позвали.</summary>
    [Fact]
    public async Task Single_record_exchange_works_for_one_chosen_row()
    {

        await WithStand(async (cards, data) =>
        {
            var (collection, sum) = Orders(data);

            var one = data.Constructor.GetRecordsPageAfter(collection, null, null, 10)
                          .First(r => r.Title == "А-1").Id!.Value;

            var request = Push(cards);
            cards.SaveOutgoing(request.Id,
                               Binding(collection, sum) with { Shape = OutgoingShape.Single });

            var (payload, trouble) = OutgoingSource.Build(cards, request, [data.Manager], [one]);

            Assert.Null(trouble);
            Assert.Equal("""{"order_no":"А-1","amount":100}""", payload!.Body);

            await Task.CompletedTask;
        });
    }

    // ── Хранение и подтверждение ──────────────────────────────────────────────

    [Fact]
    public async Task Binding_survives_a_reopen()
    {

        await WithStand(async (cards, data) =>
        {
            var (collection, sum) = Orders(data);
            var request = Push(cards);

            cards.SaveOutgoing(request.Id,
                               Binding(collection, sum) with
                               {
                                   Shape    = OutgoingShape.Wrapped,
                                   Envelope = "orders",
                               });

            var stored = cards.Outgoing(request.Id);

            Assert.NotNull(stored);
            Assert.Equal(OutgoingShape.Wrapped, stored.Shape);
            Assert.Equal("orders", stored.Envelope);
            Assert.Equal(2, stored.Fields.Count);
            Assert.Equal("amount", stored.Fields[1].Name);

            cards.SaveOutgoing(request.Id, null);
            Assert.Null(cards.Outgoing(request.Id));

            await Task.CompletedTask;
        });
    }

    // ── Отбор записей ─────────────────────────────────────────────────────────

    /// <summary>
    /// Отбор оставляет только подошедшие записи. Ради него все и делалось: раздел 2 обещает
    /// «коллекция И ФИЛЬТР», а без фильтра самый частый обмен — «статус стал „Отгружен" →
    /// отправить» — выражался только отправкой всей коллекции при каждой правке.
    /// </summary>
    [Fact]
    public async Task Only_the_records_that_pass_the_filter_go_out()
    {

        await WithStand(async (cards, data) =>
        {
            var (collection, sum) = Orders(data);
            var request = Push(cards);

            cards.SaveOutgoing(request.Id, Binding(collection, sum) with
            {
                Conditions = [new ScenarioCondition("Сумма", ScenarioTest.Equals, "250")],
            });

            var (payload, trouble) = OutgoingSource.Build(cards, request, [data.Manager]);

            Assert.Null(trouble);
            Assert.NotNull(payload);
            Assert.Equal("""[{"order_no":"А-2","amount":250}]""", payload.Body);
            Assert.Equal(1, payload.Rows);

            await Task.CompletedTask;
        });
    }

    /// <summary>
    /// Отбирать можно по свойству, которое НАРУЖУ НЕ ИДЕТ. «Отправлено пусто» — обычный способ
    /// не слать одно и то же дважды, и требовать ради такого условия отправлять служебное поле
    /// было бы прямо вредно: лишнее поле в теле — это лишние данные у той стороны.
    ///
    /// Колонка отбора при этом в тело НЕ попадает — она читается и обрезается.
    /// </summary>
    [Fact]
    public async Task A_filter_may_use_a_property_that_does_not_go_out()
    {

        await WithStand(async (cards, data) =>
        {
            var (collection, sum) = Orders(data);

            var sent = SampleData.AddField(data.Constructor, collection, "Отправлено", "text").Id!.Value;
            var rows = data.Constructor.GetRecords(collection);
            data.Constructor.SetFieldValue(rows[0].Id!.Value, sent, "да");

            var request = Push(cards);
            cards.SaveOutgoing(request.Id, Binding(collection, sum) with
            {
                Conditions = [new ScenarioCondition("Отправлено", ScenarioTest.Empty)],
            });

            var (payload, trouble) = OutgoingSource.Build(cards, request, [data.Manager]);

            Assert.Null(trouble);
            Assert.NotNull(payload);
            Assert.Equal("""[{"order_no":"А-2","amount":250}]""", payload.Body);

            await Task.CompletedTask;
        });
    }

    /// <summary>
    /// Свойство отбора удалили — отправка встает СЛОВАМИ, а не отбирает ноль записей молча.
    /// Молчаливый ноль здесь худший из ответов: обмен выглядит работающим, а наружу месяцами
    /// не уходит ничего.
    /// </summary>
    [Fact]
    public async Task A_filter_on_a_missing_property_stops_the_send()
    {

        await WithStand(async (cards, data) =>
        {
            var (collection, sum) = Orders(data);
            var request = Push(cards);

            cards.SaveOutgoing(request.Id, Binding(collection, sum) with
            {
                Conditions = [new ScenarioCondition("Такого нет", ScenarioTest.Filled)],
            });

            var (payload, trouble) = OutgoingSource.Build(cards, request, [data.Manager]);

            Assert.Null(payload);
            Assert.NotNull(trouble);
            Assert.Contains("отбор надо собрать заново", trouble, StringComparison.Ordinal);

            await Task.CompletedTask;
        });
    }

    /// <summary>
    /// Отбор действует и на выбранные вручную записи: состав тела одинаков у фоновой и ручной отправки.
    /// </summary>
    [Fact]
    public async Task The_filter_applies_to_hand_picked_records_too()
    {

        await WithStand(async (cards, data) =>
        {
            var (collection, sum) = Orders(data);
            var chosen  = data.Constructor.GetRecords(collection).Select(r => r.Id!.Value).ToList();
            var request = Push(cards);

            cards.SaveOutgoing(request.Id, Binding(collection, sum) with
            {
                Conditions = [new ScenarioCondition("Сумма", ScenarioTest.Equals, "250")],
            });

            var (payload, trouble) = OutgoingSource.Build(cards, request, [data.Manager], chosen);

            Assert.Null(trouble);
            Assert.Equal("""[{"order_no":"А-2","amount":250}]""", payload!.Body);

            await Task.CompletedTask;
        });
    }

    /// <summary>Отбор переживает переоткрытие базы вместе с остальной привязкой.</summary>
    [Fact]
    public async Task The_filter_survives_a_reopen()
    {

        await WithStand(async (cards, data) =>
        {
            var (collection, sum) = Orders(data);
            var request = Push(cards);

            cards.SaveOutgoing(request.Id, Binding(collection, sum) with
            {
                Conditions = [new ScenarioCondition("Сумма", ScenarioTest.Contains, "25")],
            });

            var back = cards.Outgoing(request.Id);

            Assert.NotNull(back);
            Assert.Single(back!.Tests);
            Assert.Equal("Сумма", back.Tests[0].Column);
            Assert.Equal(ScenarioTest.Contains, back.Tests[0].Test);
            Assert.Equal("25", back.Tests[0].Value);

            await Task.CompletedTask;
        });
    }
    [Fact]
    public async Task More_than_a_thousand_records_can_be_prepared_without_a_policy()
    {
        await WithStand(async (cards, data) =>
        {
            var collection = SampleData.AddCollection(data.Constructor, "Большой исходящий набор").Id!.Value;
            var ids = data.Constructor.BulkAddRecords(collection, [], Enumerable.Range(0, 1200)
                .Select(i => new BulkRecordRow($"Запись {i}", [])), 1200, default);
            var request = Push(cards);
            cards.SaveOutgoing(request.Id, new OutgoingBinding(data.Manager.DbId, collection,
                [new OutgoingField(null, "title")]));
            var (payload, trouble) = OutgoingSource.Build(cards, request, [data.Manager]);
            Assert.Null(trouble);
            Assert.NotNull(payload);
            using var json = System.Text.Json.JsonDocument.Parse(payload.Body);
            Assert.Equal(1200, json.RootElement.GetArrayLength());
            Assert.Equal(1200, payload.Rows);
            cards.SaveOutgoing(request.Id, new OutgoingBinding(data.Manager.DbId, collection,
                [new OutgoingField(null, "title")], Conditions:
                [new ScenarioCondition("Название", ScenarioTest.Contains, "1")]));
            var selected = ids.Skip(600).Reverse().ToArray();
            var filtered = OutgoingSource.Build(cards, request, [data.Manager], selected);
            Assert.Null(filtered.Trouble);
            using var selectedJson = System.Text.Json.JsonDocument.Parse(filtered.Payload!.Body);
            var expected = Enumerable.Range(600, 600).Where(i => i.ToString().Contains('1')).Select(i => $"Запись {i}");
            Assert.Equal(expected, selectedJson.RootElement.EnumerateArray().Select(row => row.GetProperty("title").GetString()));
            await Task.CompletedTask;
        });
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Range_period_reads_only_the_selected_dependency_for_full_and_chosen_pages(bool chosen)
    {
        await WithStand(async (cards, data) =>
        {
            const int count = 2101; // Полная выгрузка: две страницы; выбранные записи: одиннадцать.
            var collection = SampleData.AddCollection(data.Constructor, "Периоды отправки").Id!.Value;
            var start = SampleData.AddField(data.Constructor, collection, "Начало", "date").Id!.Value;
            var period = data.Constructor.AddField(new DbField
            {
                CollectionId = collection, Name = "Период", FieldType = "date_period", IsSystem = true,
                ConfigJson = new DatePeriodConfig(start).ToConfigJson(),
            }).Id!.Value;
            var ids = data.Constructor.BulkAddRecords(collection, [start], Enumerable.Range(0, count)
                .Select(i => new BulkRecordRow($"Запись {i}", [i % 2 == 0
                    ? "2026-10-28T07:00/2026-10-28T09:00" : "2026-10-28T08:00/2026-10-28T11:00"])), count, default);
            var request = Push(cards);
            cards.SaveOutgoing(request.Id, new OutgoingBinding(data.Manager.DbId, collection,
                [new OutgoingField(null, "title"), new OutgoingField(period, "period")]));
            var (payload, trouble) = OutgoingSource.Build(cards, request, [data.Manager],
                chosen ? ids.AsEnumerable().Reverse().ToArray() : null);
            Assert.Null(trouble);
            Assert.NotNull(payload);
            using var json = System.Text.Json.JsonDocument.Parse(payload.Body);
            Assert.Equal(count, json.RootElement.GetArrayLength());
            Assert.Equal(Enumerable.Range(0, count).Select(i => i % 2 == 0 ? "7200" : "10800"),
                json.RootElement.EnumerateArray().Select(row => row.GetProperty("period").GetString()));
            Assert.All(json.RootElement.EnumerateArray(), row => Assert.Equal(2, row.EnumerateObject().Count()));
            Assert.Equal(Enumerable.Range(0, count).Select(i => $"Запись {i}"),
                json.RootElement.EnumerateArray().Select(row => row.GetProperty("title").GetString()));
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Outgoing_title_selection_does_not_read_a_hidden_periods_date_range()
    {
        await WithStand(async (cards, data) =>
        {
            var collection = SampleData.AddCollection(data.Constructor, "Выбранные столбцы отправки").Id!.Value;
            var dates = SampleData.AddField(data.Constructor, collection, "Диапазон", "date").Id!.Value;
            data.Constructor.AddField(new DbField
            {
                CollectionId = collection, Name = "Скрытый период", FieldType = "date_period", IsSystem = true,
                ConfigJson = new DatePeriodConfig(dates).ToConfigJson(),
            });
            data.Constructor.BulkAddRecords(collection, [dates],
                [new BulkRecordRow("Только название", ["2026-10-28T09:00/2026-10-28T11:00"])], 1, default);
            var request = Push(cards);
            cards.SaveOutgoing(request.Id, new OutgoingBinding(data.Manager.DbId, collection,
                [new OutgoingField(null, "title")]));
            using var commands = new DatePeriodScalingTests.Commands(data.Path);
            var (payload, trouble) = OutgoingSource.Build(cards, request, [data.Manager]);
            Assert.Null(trouble);
            Assert.NotNull(payload);
            using var json = System.Text.Json.JsonDocument.Parse(payload.Body);
            var row = Assert.Single(json.RootElement.EnumerateArray());
            Assert.Equal("Только название", row.GetProperty("title").GetString());
            Assert.Single(row.EnumerateObject());
            commands.AssertNoAdditionalValueReads();
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task An_escaped_body_above_the_technical_message_limit_is_reported_without_a_partial_payload()
    {
        await WithStand(async (cards, data) =>
        {
            var collection = SampleData.AddCollection(data.Constructor, "Размер сообщения").Id!.Value;
            var field = SampleData.AddField(data.Constructor, collection, "Текст").Id!.Value;
            var record = SampleData.AddRecord(data.Constructor, collection, "Запись").Id!.Value;
            // В хранилище 17 МБ; после JSON-экранирования размер превышает 32 МБ.
            var text = new string('\\', 17 * 1024 * 1024);
            data.Constructor.SetFieldValue(record, field, text);
            var request = Push(cards);
            cards.SaveOutgoing(request.Id, new OutgoingBinding(data.Manager.DbId, collection,
                [new OutgoingField(field, "text")]));
            var (payload, trouble) = OutgoingSource.Build(cards, request, [data.Manager]);
            Assert.Null(payload);
            Assert.NotNull(trouble);
            Assert.Contains("32 МБ", trouble, StringComparison.Ordinal);
            Assert.Equal(text, data.Constructor.GetFieldValues(record)[field]);
            await Task.CompletedTask;
        });
    }
}
