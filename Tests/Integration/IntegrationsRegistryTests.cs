using SmartBase.Database;
using SmartBase.Integrations;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Хранилище соединений интеграций в app.db (<see cref="IntegrationsRegistry"/>): создание,
/// правка, удаление и обращение с секретом.
///
/// Секрет — главное здесь. Проверяется, что он не живет в модели соединения (та ездит по
/// экранам и отчетам), что правка адреса его не теряет, что замена и удаление убирают
/// прежний, и что таблица достраивается в существующем app.db — тот никогда не
/// пересоздается, иначе терялись бы реестр баз и настройки.
///
/// Временный app.db на диске, как в <see cref="DbSettingsRegistryTests"/>; PostgreSQL не нужен.
/// </summary>
public sealed class IntegrationsRegistryTests
{
    private static (string Dir, string AppDb, string DataDb) TempPaths()
    {
        var dir = Directory.CreateTempSubdirectory("sbint_").FullName;
        return (dir, Path.Combine(dir, "app.db"), Path.Combine(dir, "database.db"));
    }

    private static void Cleanup(string dir, string appDb, string dataDb)
    {
        TempDatabase.Cleanup(appDb);
        TempDatabase.Cleanup(dataDb);
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    private static IntegrationConnection Card(string name = "Учетная система") =>
        IntegrationConnection.New() with
        {
            Name    = name,
            Address = "https://srv.zavod.local/exchange",
            Auth    = ConnectionAuth.Bearer,
        };

    private static IntegrationRequest Query(string name, int connectionId) =>
        IntegrationRequest.New() with
        {
            Name         = name,
            ConnectionId = connectionId,
            Path         = "/orders",
        };

    // ── Создание и чтение ─────────────────────────────────────────────────────

    [Fact]
    public void Connection_is_saved_and_read_back()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            int id;
            using (var registry = DatabaseRegistry.Open(dataDb, appDb))
            {
                var saved = registry.Integrations.Save(
                    Card() with { Headers = [new IntegrationHeader("X-Client", "SmartBase")] },
                    secret: "токен-1");

                id = saved.Id;
                Assert.NotEqual(0, id);
            }

            // Переоткрытие: строка легла в app.db, а не осталась в памяти.
            using (var again = DatabaseRegistry.Open(dataDb, appDb))
            {
                var card = again.Integrations.Find(id);

                Assert.NotNull(card);
                Assert.Equal("Учетная система", card!.Name);
                Assert.Equal(ConnectionAuth.Bearer, card.Auth);
                Assert.Equal(30, card.TimeoutSeconds);
                Assert.Equal(["X-Client"], card.Headers.Select(h => h.Name));
                Assert.True(again.Integrations.HasSecret(id));
                Assert.Equal("токен-1", again.Integrations.Secret(id));
            }
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    /// <summary>Соединений может быть несколько, и список идет по имени.</summary>
    [Fact]
    public void Connections_are_listed_by_name()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            registry.Integrations.Save(Card("Склад"));
            registry.Integrations.Save(Card("Бухгалтерия"));

            Assert.Equal(["Бухгалтерия", "Склад"], registry.Integrations.Connections().Select(c => c.Name));
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    // ── Секрет ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Правка карточки без секрета его не теряет: человек меняет адрес, а не токен, и вводить
    /// токен заново он не должен.
    /// </summary>
    [Fact]
    public void Saving_without_a_secret_keeps_the_stored_one()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            var saved = registry.Integrations.Save(Card(), secret: "токен-1");

            registry.Integrations.Save(saved with { Address = "https://srv2.zavod.local/exchange" });

            Assert.Equal("токен-1", registry.Integrations.Secret(saved.Id));
            Assert.Equal("https://srv2.zavod.local/exchange", registry.Integrations.Find(saved.Id)!.Address);
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    [Fact]
    public void Secret_can_be_replaced_and_cleared()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            var saved = registry.Integrations.Save(Card(), secret: "токен-1");

            registry.Integrations.Save(saved, secret: "токен-2");
            Assert.Equal("токен-2", registry.Integrations.Secret(saved.Id));

            registry.Integrations.Save(saved, secret: "");
            Assert.False(registry.Integrations.HasSecret(saved.Id));
            Assert.Equal("", registry.Integrations.Secret(saved.Id));
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    /// <summary>Секрета нет в модели соединения — она ездит по экранам и отчетам.</summary>
    [Fact]
    public void Connection_model_carries_no_secret()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            var saved = registry.Integrations.Save(Card(), secret: "токен-1");

            var text = saved.ToString();   // record печатает все свои поля

            Assert.DoesNotContain("токен-1", text, StringComparison.Ordinal);
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    [Fact]
    public void Delete_removes_the_connection_and_its_secret()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            var saved = registry.Integrations.Save(Card(), secret: "токен-1");

            registry.Integrations.Delete(saved.Id);

            Assert.Null(registry.Integrations.Find(saved.Id));
            Assert.Empty(registry.Integrations.Connections());
            Assert.False(registry.Integrations.HasSecret(saved.Id));
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    // ── Проверка формы ────────────────────────────────────────────────────────

    /// <summary>
    /// Негодная карточка не сохраняется: пустое имя и неразобравшийся адрес в хранилище
    /// попасть не должны — дальше на них будет опираться отправка.
    /// </summary>
    [Fact]
    public void Broken_card_is_refused_with_a_readable_reason()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            using var registry = DatabaseRegistry.Open(dataDb, appDb);

            var error = Assert.Throws<InvalidOperationException>(() =>
                registry.Integrations.Save(IntegrationConnection.New() with { Name = "Без адреса" }));

            Assert.Contains("Адрес не заполнен", error.Message, StringComparison.Ordinal);
            Assert.Empty(registry.Integrations.Connections());
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    // ── Запросы ───────────────────────────────────────────────────────────────

    [Fact]
    public void Request_is_saved_and_read_back()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            int id;
            int connectionId;

            using (var registry = DatabaseRegistry.Open(dataDb, appDb))
            {
                connectionId = registry.Integrations.Save(Card()).Id;

                id = registry.Integrations.SaveRequest(IntegrationRequest.New() with
                {
                    Name         = "Заказы за сутки",
                    ConnectionId = connectionId,
                    Method       = RequestMethod.Post,
                    Path         = "/orders?since={{последний_запуск}}",
                    Headers      = [new IntegrationHeader("Accept", "application/json")],
                    Body         = """{"depot":1}""",
                    Paging       = RequestPaging.Cursor,
                    PagingField  = "next_cursor",
                    ItemsPath    = "data.items",
                }).Id;
            }

            using (var again = DatabaseRegistry.Open(dataDb, appDb))
            {
                var card = again.Integrations.FindRequest(id);

                Assert.NotNull(card);
                Assert.Equal("Заказы за сутки", card!.Name);
                Assert.Equal(connectionId, card.ConnectionId);
                Assert.Equal(RequestMethod.Post, card.Method);
                Assert.Equal("/orders?since={{последний_запуск}}", card.Path);
                Assert.Equal(["Accept"], card.Headers.Select(h => h.Name));
                Assert.Equal("""{"depot":1}""", card.Body);
                Assert.Equal(RequestPaging.Cursor, card.Paging);
                Assert.Equal("next_cursor", card.PagingField);
                Assert.Equal("data.items", card.ItemsPath);
            }
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    [Fact]
    public void Requests_are_listed_by_connection()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            var first  = registry.Integrations.Save(Card("Склад")).Id;
            var second = registry.Integrations.Save(Card("Бухгалтерия")).Id;

            registry.Integrations.SaveRequest(Query("Остатки",  first));
            registry.Integrations.SaveRequest(Query("Движения", first));
            registry.Integrations.SaveRequest(Query("Счета",    second));

            Assert.Equal(["Движения", "Остатки"], registry.Integrations.RequestsOf(first).Select(r => r.Name));
            Assert.Equal(3, registry.Integrations.Requests().Count);
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    /// <summary>Запрос без соединения отправить некуда — такую карточку хранить незачем.</summary>
    [Fact]
    public void Request_without_a_connection_is_refused()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            using var registry = DatabaseRegistry.Open(dataDb, appDb);

            var error = Assert.Throws<InvalidOperationException>(() =>
                registry.Integrations.SaveRequest(Query("Остатки", connectionId: 404)));

            Assert.Contains("соединение не найдено", error.Message, StringComparison.Ordinal);
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    /// <summary>
    /// Соединение, на котором висят запросы, не удаляется молча вместе с ними: унести чужие
    /// карточки хуже, чем отказать.
    /// </summary>
    [Fact]
    public void Connection_in_use_is_not_deleted()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            var connectionId = registry.Integrations.Save(Card()).Id;
            var requestId    = registry.Integrations.SaveRequest(Query("Остатки", connectionId)).Id;

            var error = Assert.Throws<InvalidOperationException>(
                () => registry.Integrations.Delete(connectionId));

            Assert.Contains("ссылаются запросы", error.Message, StringComparison.Ordinal);
            Assert.Single(registry.Integrations.Connections());

            registry.Integrations.DeleteRequest(requestId);
            registry.Integrations.Delete(connectionId);

            Assert.Empty(registry.Integrations.Connections());
            Assert.Empty(registry.Integrations.Requests());
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    // ── Аддитивность схемы app.db ─────────────────────────────────────────────

    /// <summary>
    /// Таблица соединений достраивается в УЖЕ СУЩЕСТВУЮЩЕМ app.db: он никогда не
    /// пересоздается, иначе при обновлении программы терялись бы реестр баз и настройки.
    /// Здесь это и проверяется — app.db создается первым открытием (без соединений),
    /// а соединение заводится вторым.
    /// </summary>
    [Fact]
    public void Table_is_added_to_an_existing_app_db()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            using (var first = DatabaseRegistry.Open(dataDb, appDb))
                Assert.Empty(first.Integrations.Connections());

            using (var second = DatabaseRegistry.Open(dataDb, appDb))
            {
                var saved = second.Integrations.Save(Card(), secret: "токен-1");
                Assert.NotEqual(0, saved.Id);
            }

            using (var third = DatabaseRegistry.Open(dataDb, appDb))
                Assert.Single(third.Integrations.Connections());
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }
}
