using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Тело исходящего запроса, собранное из настоящей коллекции (<see cref="OutgoingSource"/> и
/// <c>ConstructorRepository.ReadOutgoingIsolated</c>, docs/ROADMAP-API.md, раздел 2).
///
/// Форма привязки и вид тела проверяются на модели — <c>Tests/Unit/OutgoingBindingTests</c>.
/// Здесь то, чего на модели не проверить: значения берутся из записей, метки закрывают отправку
/// ВТОРЫМ рубежом (после окна настройки), потолок политики считается по факту прочитанного, а
/// смена состава полей роняет подтверждение направления.
///
/// Набор идет в общей очереди шлюза: и политика, и метки спрашиваются у <c>EgressGate</c>,
/// одного на приложение.
/// </summary>
[Collection(EgressCollection.Name)]
public sealed class OutgoingSourceTests : IDisposable
{
    public void Dispose() => EgressGate.Close();

    private static readonly DateTimeOffset Now =
        new(new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc));

    private const string Master = "секрет-организации";

    // ── Стенд ─────────────────────────────────────────────────────────────────

    /// <summary>Политика с потолком строк; без нее наружу не уходит ничего.</summary>
    private static void AttachPolicy(int maxRows = 1000) =>
        EgressGate.AttachPolicy(EgressPolicySet.Of(new EgressPolicy(
            EgressMode.Internal,
            Hosts:           ["api.zavod.local"],
            Networks:        ["10.0.0.0/8"],
            Folders:         [],
            MaxRowsPerRun:   maxRows,
            MaxBytesPerDay:  1024 * 1024,
            MaxRunsPerHour:  100,
            ValidUntil:      null,
            UserMayAddHosts: false,
            Source:          "тест",
            MasterHash:      MasterPassword.Hash(Master))));

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
            try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { /* best-effort */ }
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
        AttachPolicy();

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
        AttachPolicy();

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
        AttachPolicy();

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

    // ── Метки: второй рубеж ───────────────────────────────────────────────────

    /// <summary>
    /// Метку могли поставить ПОСЛЕ того, как привязку собрали. Окно настройки такое поле больше
    /// не покажет, но сохраненная привязка о нем не знает — и отправка обязана встать.
    /// </summary>
    [Fact]
    public async Task Field_labeled_after_the_binding_was_made_stops_the_send()
    {
        AttachPolicy();

        await WithStand(async (cards, data) =>
        {
            var (collection, sum) = Orders(data);
            var request = Push(cards);
            cards.SaveOutgoing(request.Id, Binding(collection, sum));

            data.Constructor.SetFieldLabel(sum, ConfidentialityLabel.Restricted);

            var (payload, trouble) = OutgoingSource.Build(cards, request, [data.Manager]);

            Assert.Null(payload);
            Assert.Contains("Сумма", trouble!, StringComparison.Ordinal);
            Assert.Contains("наружу", trouble!, StringComparison.Ordinal);

            await Task.CompletedTask;
        });
    }

    /// <summary>Закрытый контур запрещает исходящие независимо от политики, режима и прав.</summary>
    [Fact]
    public async Task Closed_loop_database_has_no_outgoing_path()
    {
        AttachPolicy();

        await WithStand(async (cards, data) =>
        {
            var (collection, sum) = Orders(data);
            var request = Push(cards);
            cards.SaveOutgoing(request.Id, Binding(collection, sum));

            data.Constructor.SetDatabaseLabel(ConfidentialityLabel.Closed);

            var (payload, trouble) = OutgoingSource.Build(cards, request, [data.Manager]);

            Assert.Null(payload);
            Assert.Contains("закрытым контуром", trouble!, StringComparison.Ordinal);

            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Deleted_property_stops_the_send_by_name()
    {
        AttachPolicy();

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

    // ── Потолок политики ──────────────────────────────────────────────────────

    /// <summary>
    /// Отправить первые N из тысячи, промолчав об остальных, нельзя ни при каком объяснении:
    /// человек уверен, что ушло все.
    /// </summary>
    [Fact]
    public async Task More_records_than_the_policy_allows_is_refused_whole()
    {
        AttachPolicy(maxRows: 1);

        await WithStand(async (cards, data) =>
        {
            var (collection, sum) = Orders(data);
            var request = Push(cards);
            cards.SaveOutgoing(request.Id, Binding(collection, sum));

            var (payload, trouble) = OutgoingSource.Build(cards, request, [data.Manager]);

            Assert.Null(payload);
            Assert.Contains("потолок", trouble!, StringComparison.Ordinal);

            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Single_record_exchange_refuses_a_collection_of_two()
    {
        AttachPolicy();

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
        AttachPolicy();

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
        AttachPolicy();

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
        AttachPolicy();

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
        AttachPolicy();

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
        AttachPolicy();

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

    /// <summary>
    /// Состав полей входит в отпечаток запроса: дописать поле в уже подтвержденное направление —
    /// самый дешевый способ обойти согласование, и он обязан не работать.
    /// </summary>
    [Fact]
    public async Task Changing_the_fields_drops_the_confirmation()
    {
        AttachPolicy();

        await WithStand(async (cards, data) =>
        {
            var (collection, sum) = Orders(data);
            var request = Push(cards);
            cards.SaveOutgoing(request.Id, Binding(collection, sum));

            var direction = cards.ConfirmDirection(
                request, Master, "Петров П. П.", days: 30, Now, out var trouble);

            Assert.NotNull(direction);
            Assert.Equal("", trouble);
            Assert.Null(cards.OutwardTrouble(request, Now));

            // Дописали поле — согласовано было другое.
            cards.SaveOutgoing(request.Id, Binding(collection, sum) with
            {
                Fields = [.. Binding(collection, sum).Fields, new OutgoingField(sum, "amount_copy")],
            });

            Assert.Contains("правили после подтверждения",
                            cards.OutwardTrouble(request, Now)!, StringComparison.Ordinal);

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
        AttachPolicy();

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
        AttachPolicy();

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
    /// Отбор идет ДО потолка строк, иначе он ничего не решает: коллекция, которая целиком в
    /// потолок не влезает, отправляется, если отобранного в нем помещается.
    ///
    /// Это и есть вторая половина найденной беды: с триггером «изменение данных» сценарий
    /// переставал работать совсем, как только коллекция перерастала потолок.
    /// </summary>
    [Fact]
    public async Task The_filter_runs_before_the_row_ceiling()
    {
        AttachPolicy(maxRows: 1);

        await WithStand(async (cards, data) =>
        {
            var (collection, sum) = Orders(data);
            var request = Push(cards);

            // Без отбора две записи в потолок «одна строка» не влезают.
            cards.SaveOutgoing(request.Id, Binding(collection, sum));
            Assert.Contains("больше, чем разрешает политика",
                            OutgoingSource.Build(cards, request, [data.Manager]).Trouble!,
                            StringComparison.Ordinal);

            // С отбором остается одна — и она проходит.
            cards.SaveOutgoing(request.Id, Binding(collection, sum) with
            {
                Conditions = [new ScenarioCondition("Сумма", ScenarioTest.Equals, "250")],
            });

            var (payload, trouble) = OutgoingSource.Build(cards, request, [data.Manager]);

            Assert.Null(trouble);
            Assert.Equal(1, payload!.Rows);

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
        AttachPolicy();

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
    /// Отбирать по ПОМЕЧЕННОМУ свойству нельзя. Значение такого поля наружу не уходит, но
    /// уходит знание о нем: по тому, какие записи ушли, оно и восстанавливается.
    /// </summary>
    [Fact]
    public async Task A_filter_on_a_labeled_property_is_refused()
    {
        AttachPolicy();

        await WithStand(async (cards, data) =>
        {
            var (collection, sum) = Orders(data);

            var secret = SampleData.AddField(data.Constructor, collection, "Скидка", "text").Id!.Value;
            data.Constructor.SetFieldLabel(secret, ConfidentialityLabel.Restricted);

            var request = Push(cards);
            cards.SaveOutgoing(request.Id, Binding(collection, sum) with
            {
                Conditions = [new ScenarioCondition("Скидка", ScenarioTest.Filled)],
            });

            var (payload, trouble) = OutgoingSource.Build(cards, request, [data.Manager]);

            Assert.Null(payload);
            Assert.NotNull(trouble);
            Assert.Contains("отбор идет по свойству", trouble, StringComparison.Ordinal);

            await Task.CompletedTask;
        });
    }

    /// <summary>
    /// Отбор действует и на записи, выбранные РУКАМИ. Он входит в отпечаток направления, то есть
    /// в то, что подтверждали мастер-паролем: пропусти его у кнопки — и ручная отправка уносила
    /// бы записи, которых согласованный обмен не касается.
    /// </summary>
    [Fact]
    public async Task The_filter_applies_to_hand_picked_records_too()
    {
        AttachPolicy();

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
        AttachPolicy();

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
}
