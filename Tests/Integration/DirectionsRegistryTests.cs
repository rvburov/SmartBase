using SmartBase.Database;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Хранение исходящих направлений в app.db (<c>IntegrationsRegistry.Directions.cs</c>).
///
/// Проверяется то, что нельзя проверить на модели: подтверждение переживает переоткрытие, пароль
/// спрашивается у политики, а не у экрана, и разрешение не переезжает ни на чужой запрос, ни на
/// следующий, получивший тот же ключ.
///
/// Набор идет в общей очереди шлюза: подтверждение проверяет политику из
/// <c>EgressGate.Policy</c>, одну на приложение.
/// </summary>
[Collection(EgressCollection.Name)]
public sealed class DirectionsRegistryTests : IDisposable
{
    public void Dispose() => EgressGate.Close();

    private static readonly DateTimeOffset Now =
        new(new DateTime(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc));

    private const string Master = "секрет-организации";

    private static (string Dir, string AppDb, string DataDb) TempPaths()
    {
        var dir = Directory.CreateTempSubdirectory("sbdir_").FullName;
        return (dir, Path.Combine(dir, "app.db"), Path.Combine(dir, "database.db"));
    }

    private static void Cleanup(string dir, string appDb, string dataDb)
    {
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { /* best-effort */ }
        TempDatabase.Cleanup(appDb);
        TempDatabase.Cleanup(dataDb);
        try { Directory.Delete(dir, recursive: true); } catch { /* временная папка */ }
    }

    /// <summary>Политика с мастер-паролем; без него подтверждать нечем.</summary>
    private static void AttachPolicy(string master = Master) =>
        EgressGate.AttachPolicy(EgressPolicySet.Of(new EgressPolicy(
            EgressMode.Internal,
            Hosts:           ["api.zavod.local"],
            Networks:        ["10.0.0.0/8"],
            Folders:         [],
            MaxRowsPerRun:   1000,
            MaxBytesPerDay:  1024 * 1024,
            MaxRunsPerHour:  100,
            ValidUntil:      null,
            UserMayAddHosts: false,
            Source:          "тест",
            MasterHash:      master.Length > 0 ? MasterPassword.Hash(master) : "")));

    /// <summary>Соединение и запрос-отправитель: без сохраненной карточки подтверждать нечего.</summary>
    private static IntegrationRequest Push(IntegrationsRegistry cards, string body = "{\"итог\":1}")
    {
        var connection = cards.Save(IntegrationConnection.New() with
        {
            Name           = "Учетная система",
            Address        = "https://api.zavod.local",
            TimeoutSeconds = 30,
        });

        return cards.SaveRequest(IntegrationRequest.New() with
        {
            Name         = "Отдать итог",
            ConnectionId = connection.Id,
            Method       = RequestMethod.Post,
            Path         = "/result",
            Body         = body,
        });
    }

    // ── Подтверждение ─────────────────────────────────────────────────────────

    [Fact]
    public void A_confirmed_direction_survives_a_reopen()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            AttachPolicy();
            int requestId;

            using (var registry = DatabaseRegistry.Open(dataDb, appDb))
            {
                var request = Push(registry.Integrations);
                requestId   = request.Id;

                var direction = registry.Integrations.ConfirmDirection(
                    request, Master, "Петров П. П.", days: 90, Now, out var trouble);

                Assert.NotNull(direction);
                Assert.Equal("", trouble);
            }

            using (var again = DatabaseRegistry.Open(dataDb, appDb))
            {
                var saved = again.Integrations.Direction(requestId);

                Assert.NotNull(saved);
                Assert.Equal("Петров П. П.", saved!.Confirmed);
                Assert.Equal(Now.AddDays(90), saved.Until);
                Assert.Null(saved.UsedAt);
            }
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    /// <summary>
    /// Пароль проверяется у политики, а не на экране. Иначе проверку пришлось бы повторить
    /// в каждом месте, откуда подтверждают, и одно из них однажды ее забыло бы.
    /// </summary>
    [Fact]
    public void A_wrong_password_confirms_nothing()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            AttachPolicy();

            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            var request = Push(registry.Integrations);

            var direction = registry.Integrations.ConfirmDirection(
                request, "не тот пароль", "Петров П. П.", days: 90, Now, out var trouble);

            Assert.Null(direction);
            Assert.Contains("не подошел", trouble, StringComparison.Ordinal);
            Assert.Null(registry.Integrations.Direction(request.Id));
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    /// <summary>
    /// В политике пароля нет — подтверждать нечем, и причина называется словами: человек должен
    /// пойти к ответственному за интеграции, а не искать ошибку у себя.
    /// </summary>
    [Fact]
    public void Without_a_master_in_the_policy_the_reason_is_named()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            AttachPolicy(master: "");

            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            var request = Push(registry.Integrations);

            registry.Integrations.ConfirmDirection(
                request, "любой", "Петров П. П.", days: 90, Now, out var trouble);

            Assert.Contains("не задан мастер-пароль", trouble, StringComparison.Ordinal);
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    /// <summary>Читающему запросу подтверждать нечего, и предлагать это не надо.</summary>
    [Fact]
    public void A_reading_request_is_not_confirmed_at_all()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            AttachPolicy();

            using var registry = DatabaseRegistry.Open(dataDb, appDb);

            var connection = registry.Integrations.Save(IntegrationConnection.New() with
            {
                Name           = "Учетная система",
                Address        = "https://api.zavod.local",
                TimeoutSeconds = 30,
            });

            var request = registry.Integrations.SaveRequest(IntegrationRequest.New() with
            {
                Name         = "Заказы",
                ConnectionId = connection.Id,
                Path         = "/orders",
            });

            registry.Integrations.ConfirmDirection(
                request, Master, "Петров П. П.", days: 90, Now, out var trouble);

            Assert.Contains("подтверждать нечего", trouble, StringComparison.Ordinal);
            Assert.Null(registry.Integrations.OutwardTrouble(request, Now));
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    // ── Что мешает отправлять ─────────────────────────────────────────────────

    [Fact]
    public void An_unconfirmed_push_is_refused_by_name()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            AttachPolicy();

            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            var request = Push(registry.Integrations);

            Assert.Contains("не подтверждено",
                            registry.Integrations.OutwardTrouble(request, Now)!,
                            StringComparison.Ordinal);
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    /// <summary>
    /// Подтвердить безобидный запрос и дописать в него выгрузку — самый дешевый обход
    /// согласования. Отпечаток ловит это на хранении, а не только на модели.
    /// </summary>
    [Fact]
    public void Editing_the_request_after_confirmation_stops_it()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            AttachPolicy();

            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            var cards   = registry.Integrations;
            var request = Push(cards);

            cards.ConfirmDirection(request, Master, "Петров П. П.", days: 90, Now, out _);
            Assert.Null(cards.OutwardTrouble(request, Now));

            var edited = cards.SaveRequest(request with { Body = "{\"итог\":1,\"и\":\"все записи\"}" });

            Assert.Contains("правили после подтверждения", cards.OutwardTrouble(edited, Now)!,
                            StringComparison.Ordinal);
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    /// <summary>Отметка об использовании сдвигает отсчет простоя и переживает переоткрытие.</summary>
    [Fact]
    public void Marking_the_direction_used_moves_the_idle_count()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            AttachPolicy();

            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            var cards   = registry.Integrations;
            var request = Push(cards);

            cards.ConfirmDirection(request, Master, "Петров П. П.", days: 365, Now, out _);

            // Без отметки направление отключилось бы через месяц простоя.
            Assert.NotNull(cards.OutwardTrouble(request, Now.AddDays(40)));

            cards.MarkDirectionUsed(request.Id, Now.AddDays(35));

            Assert.Null(cards.OutwardTrouble(request, Now.AddDays(40)));
            Assert.Equal(Now.AddDays(35), cards.Direction(request.Id)!.UsedAt);
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    [Fact]
    public void A_revoked_direction_stops_working()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            AttachPolicy();

            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            var cards   = registry.Integrations;
            var request = Push(cards);

            cards.ConfirmDirection(request, Master, "Петров П. П.", days: 90, Now, out _);

            Assert.True(cards.RevokeDirection(request.Id));
            Assert.Null(cards.Direction(request.Id));
            Assert.NotNull(cards.OutwardTrouble(request, Now));
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    /// <summary>
    /// Подтверждение уходит вместе с запросом. SQLite выдает ключи заново после удаления, и
    /// следующий запрос не должен унаследовать чужое разрешение.
    /// </summary>
    [Fact]
    public void Deleting_the_request_takes_its_confirmation_with_it()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            AttachPolicy();

            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            var cards   = registry.Integrations;
            var request = Push(cards);

            cards.ConfirmDirection(request, Master, "Петров П. П.", days: 90, Now, out _);
            cards.DeleteRequest(request.Id);

            Assert.Null(cards.Direction(request.Id));
            Assert.Empty(cards.Directions());
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }

    /// <summary>
    /// Правка app.db руками не должна продлевать разрешение: испорченный срок читается как
    /// «вышел», а не как «без срока».
    /// </summary>
    [Fact]
    public void A_broken_date_in_the_file_reads_as_expired()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            AttachPolicy();

            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            var cards   = registry.Integrations;
            var request = Push(cards);

            cards.ConfirmDirection(request, Master, "Петров П. П.", days: 90, Now, out _);

            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={appDb}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE integration_directions SET Until = 'никогда';";
                command.ExecuteNonQuery();
            }

            using var again = DatabaseRegistry.Open(dataDb, appDb);

            Assert.Contains("срок", again.Integrations.OutwardTrouble(request, Now)!,
                            StringComparison.Ordinal);
        }
        finally { Cleanup(dir, appDb, dataDb); }
    }
}
