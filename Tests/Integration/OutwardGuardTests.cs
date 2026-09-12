using SmartBase.Database;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Замок на исходящей отправке (<see cref="OutwardGuard"/>) — подтверждение направления и след в
/// журнале безопасности, соединенные с настоящим запуском (docs/ROADMAP-API.md, 10.8 и 10.10).
///
/// Проверяется здесь не «вернулась ли причина» — это дело <c>OutgoingDirectionTests</c>, — а то,
/// что от нее МЕНЯЕТСЯ: сокет не открывается, квота не расходуется, строка ложится и до
/// отправки, а не после.
///
/// Набор идет в общей очереди шлюза: и политика, и журнал безопасности одни на приложение.
/// </summary>
[Collection(EgressCollection.Name)]
public sealed class OutwardGuardTests : IDisposable
{
    private readonly string _journal =
        Path.Combine(Path.GetTempPath(), $"sbguard_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        EgressGate.Close();
        SecurityJournal.Attach(null);

        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { /* best-effort */ }

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(_journal + suffix)) File.Delete(_journal + suffix); }
            catch { /* временный файл */ }
        }
    }

    private static readonly DateTimeOffset Now =
        new(new DateTime(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc));

    private const string Master = "секрет-организации";

    // ── Стенд ─────────────────────────────────────────────────────────────────

    /// <summary>Политика, разрешающая обратную петлю, с мастер-паролем организации.</summary>
    private static void AttachPolicy() =>
        EgressGate.AttachPolicy(EgressPolicySet.Of(new EgressPolicy(
            EgressMode.Internal,
            Hosts:           ["127.0.0.1"],
            Networks:        ["127.0.0.0/8"],
            Folders:         [],
            MaxRowsPerRun:   1000,
            MaxBytesPerDay:  8 * 1024 * 1024,
            MaxRunsPerHour:  100,
            ValidUntil:      null,
            UserMayAddHosts: false,
            Source:          "тест",
            MasterHash:      MasterPassword.Hash(Master))));

    private SecurityJournal Attach()
    {
        var journal = new SecurityJournal(_journal);
        SecurityJournal.Attach(journal);
        return journal;
    }

    /// <summary>Реестр на временной app.db; папка убирается при любом исходе.</summary>
    private static async Task WithRegistry(Func<IntegrationsRegistry, Task> body)
    {
        var dir    = Directory.CreateTempSubdirectory("sbguard_").FullName;
        var appDb  = Path.Combine(dir, "app.db");
        var dataDb = Path.Combine(dir, "database.db");

        try
        {
            using (var registry = DatabaseRegistry.Open(dataDb, appDb))
                await body(registry.Integrations);
        }
        finally
        {
            try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { /* best-effort */ }
            TempDatabase.Cleanup(appDb);
            TempDatabase.Cleanup(dataDb);
            try { Directory.Delete(dir, recursive: true); } catch { /* временная папка */ }
        }
    }

    private static IntegrationConnection Connect(IntegrationsRegistry cards, int port) =>
        cards.Save(IntegrationConnection.New() with
        {
            Name           = "Учетная система",
            Address        = $"http://127.0.0.1:{port}",
            TimeoutSeconds = 30,
            Retries        = 0,
        });

    private static IntegrationRequest Push(IntegrationsRegistry cards, int connectionId,
                                           string body = "{\"итог\":1}") =>
        cards.SaveRequest(IntegrationRequest.New() with
        {
            Name         = "Отдать итог",
            ConnectionId = connectionId,
            Method       = RequestMethod.Post,
            Path         = "/result",
            Body         = body,
        });

    private static IntegrationRequest Pull(IntegrationsRegistry cards, int connectionId) =>
        cards.SaveRequest(IntegrationRequest.New() with
        {
            Name         = "Забрать заказы",
            ConnectionId = connectionId,
            Path         = "/orders",
        });

    private static VariableContext Context() =>
        new(new DateOnly(2026, 9, 11), null, "http://127.0.0.1");

    private static void Confirm(IntegrationsRegistry cards, IntegrationRequest request)
    {
        cards.ConfirmDirection(request, Master, "Петров П. П.", days: 90, Now, out var trouble);
        Assert.Equal("", trouble);
    }

    // ── Замок держит ──────────────────────────────────────────────────────────

    /// <summary>
    /// Главное свойство всего замка: неподтвержденная отправка не доходит до сокета. Не
    /// «отправляется и отбрасывается», не «уходит и не засчитывается» — чужая программа не видит
    /// ни одного соединения.
    /// </summary>
    [Fact]
    public async Task An_unconfirmed_send_never_reaches_the_socket()
    {
        AttachPolicy();
        Attach();

        await WithRegistry(async cards =>
        {
            using var server = new TinyServer(_ => TinyServer.Reply(200, "[]"));

            var connection = Connect(cards, server.Port);
            var request    = Push(cards, connection.Id);

            var verdict = OutwardGuard.Check(cards, connection, request, Context(),
                                             scenario: "Итоги суток", dbId: 3, Now, payload: null);

            Assert.False(verdict.Allowed);

            var outcome = await RequestRunner.RunAsync(connection, request, "", Context(),
                                                       verdict.Trouble, payload: null);

            Assert.False(outcome.Ok);
            Assert.Contains("не подтверждено", outcome.Explanation, StringComparison.Ordinal);
            Assert.Empty(server.Seen);
        });
    }

    /// <summary>
    /// Отказ замка не расходует квоту. Иначе неподтвержденный сценарий, стучащийся раз в пять
    /// минут, к обеду выбирал бы часовой потолок, и вставал бы весь обмен — тот, что настроен
    /// правильно, тоже.
    /// </summary>
    [Fact]
    public async Task A_refused_send_costs_no_quota()
    {
        AttachPolicy();
        Attach();

        await WithRegistry(async cards =>
        {
            var connection = Connect(cards, 8080);
            var request    = Push(cards, connection.Id);

            var verdict = OutwardGuard.Check(cards, connection, request, Context(),
                                             scenario: "", dbId: 0, Now, payload: null);

            var outcome = await RequestRunner.RunAsync(connection, request, "", Context(),
                                                       verdict.Trouble, payload: null);

            Assert.False(outcome.Sent);
        });
    }

    /// <summary>Подтвержденное направление пропускает отправку до конца.</summary>
    [Fact]
    public async Task A_confirmed_direction_lets_the_send_through()
    {
        AttachPolicy();
        Attach();

        await WithRegistry(async cards =>
        {
            using var server = new TinyServer(_ => TinyServer.Reply(200, "[]"));

            var connection = Connect(cards, server.Port);
            var request    = Push(cards, connection.Id);

            Confirm(cards, request);

            var verdict = OutwardGuard.Check(cards, connection, request, Context(),
                                             scenario: "", dbId: 0, Now, payload: null);

            Assert.True(verdict.Allowed, verdict.Trouble);
            Assert.Equal("Петров П. П.", verdict.Confirmed);

            var outcome = await RequestRunner.RunAsync(connection, request, "", Context(),
                                                       verdict.Trouble, payload: null);

            Assert.True(outcome.Ok, outcome.Explanation);
            Assert.Single(server.Seen);
        });
    }

    /// <summary>
    /// Читающему запросу подтверждать нечего, и следа он не оставляет: обмен, опрашивающий чужую
    /// программу каждые пять минут, за сутки утопил бы журнал проверяющего в трех сотнях строк
    /// «сходили и вернулись».
    /// </summary>
    [Fact]
    public async Task A_reading_request_is_free_and_silent()
    {
        AttachPolicy();
        var journal = Attach();

        await WithRegistry(cards =>
        {
            var connection = Connect(cards, 8080);
            var request    = Pull(cards, connection.Id);

            var verdict = OutwardGuard.Check(cards, connection, request, Context(),
                                             scenario: "", dbId: 0, Now, payload: null);

            Assert.True(verdict.Allowed);
            Assert.Equal(0, journal.Count());

            return Task.CompletedTask;
        });
    }

    // ── Не записалось — не отправляем ─────────────────────────────────────────

    /// <summary>
    /// Журнала безопасности нет — исходящей отправки нет. Правило неудобное намеренно: обмен, о
    /// котором не осталось записи, — ровно то, чего требование службы безопасности не допускает,
    /// и «журнал не открылся» не может быть поводом сделать исключение.
    /// </summary>
    [Fact]
    public async Task Without_a_journal_a_confirmed_send_is_cancelled_too()
    {
        AttachPolicy();
        SecurityJournal.Attach(null);

        await WithRegistry(async cards =>
        {
            using var server = new TinyServer(_ => TinyServer.Reply(200, "[]"));

            var connection = Connect(cards, server.Port);
            var request    = Push(cards, connection.Id);

            Confirm(cards, request);

            var verdict = OutwardGuard.Check(cards, connection, request, Context(),
                                             scenario: "", dbId: 0, Now, payload: null);

            Assert.False(verdict.Allowed);
            Assert.Contains("след", verdict.Trouble!, StringComparison.Ordinal);

            var outcome = await RequestRunner.RunAsync(connection, request, "", Context(),
                                                       verdict.Trouble, payload: null);

            Assert.False(outcome.Ok);
            Assert.Empty(server.Seen);
        });
    }

    // ── След ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Заблокированная попытка оставляет строку — ради нее журнал во многом и ведется. И строка
    /// эта называет, КУДА и с чем шли: проверяющий разбирает именно такие.
    /// </summary>
    [Fact]
    public async Task A_blocked_attempt_leaves_a_row()
    {
        AttachPolicy();
        var journal = Attach();

        await WithRegistry(cards =>
        {
            var connection = Connect(cards, 8080);
            var request    = Push(cards, connection.Id);

            OutwardGuard.Check(cards, connection, request, Context(),
                               scenario: "Итоги суток", dbId: 3, Now, payload: null);

            var row = journal.Recent().Single();

            Assert.Equal(SecurityEvent.Send, row.Event);
            Assert.False(row.Allowed);
            Assert.Equal("Итоги суток", row.Scenario);
            Assert.Equal(3, row.DbId);
            Assert.Equal("", row.Confirmed);
            Assert.Contains("/result",        row.Address,     StringComparison.Ordinal);
            Assert.Contains("не подтверждено", row.Explanation, StringComparison.Ordinal);

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Разрешенная попытка тоже оставляет строку, и в ней записано, с чьего подтверждения она
    /// ушла. Без этого имени разбор «кто это согласовал» упирается в чужую память.
    /// </summary>
    [Fact]
    public async Task An_allowed_attempt_names_who_confirmed_it()
    {
        AttachPolicy();
        var journal = Attach();

        await WithRegistry(cards =>
        {
            var connection = Connect(cards, 8080);
            var request    = Push(cards, connection.Id);

            Confirm(cards, request);

            OutwardGuard.Check(cards, connection, request, Context(), scenario: "", dbId: 0, Now, payload: null);

            var row = journal.Recent().Single();

            Assert.True(row.Allowed);
            Assert.Equal("Петров П. П.", row.Confirmed);
            Assert.Equal("", row.Scenario);

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Имена полей — да, значения — никогда. Журнал выгружают проверяющему целиком, и значение
    /// поля в нем стало бы второй утечкой следом за первой.
    /// </summary>
    [Fact]
    public async Task The_row_names_fields_but_never_values()
    {
        AttachPolicy();
        var journal = Attach();

        await WithRegistry(cards =>
        {
            var connection = Connect(cards, 8080);
            var request    = Push(cards, connection.Id,
                                  body: "{\"итог\":1,\"клиент\":\"Ромашка\"}");

            Confirm(cards, request);

            OutwardGuard.Check(cards, connection, request, Context(), scenario: "", dbId: 0, Now, payload: null);

            var row = journal.Recent().Single();

            Assert.Equal("итог, клиент", row.Fields);

            foreach (var cell in new[] { row.Address, row.Fields, row.Explanation, row.BodyHash })
                Assert.DoesNotContain("Ромашка", cell, StringComparison.Ordinal);

            return Task.CompletedTask;
        });
    }

    /// <summary>Сколько записей уносит тело — считается по самому телу, а не со слов карточки.</summary>
    [Fact]
    public async Task The_row_counts_what_the_body_carries()
    {
        AttachPolicy();
        var journal = Attach();

        await WithRegistry(cards =>
        {
            var connection = Connect(cards, 8080);
            var request    = Push(cards, connection.Id,
                                  body: "[{\"итог\":1},{\"итог\":2},{\"итог\":3}]");

            Confirm(cards, request);

            OutwardGuard.Check(cards, connection, request, Context(), scenario: "", dbId: 0, Now, payload: null);

            var row = journal.Recent().Single();

            Assert.Equal(3,      row.Rows);
            Assert.Equal("итог", row.Fields);

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Отпечаток тела в журнале — это отпечаток ТОГО САМОГО тела, которое получила чужая
    /// программа. Разойдись они — и по журналу нельзя было бы доказать, что ушло именно
    /// согласованное; а доказывают им как раз это.
    /// </summary>
    [Fact]
    public async Task The_body_hash_is_the_hash_of_what_the_server_received()
    {
        AttachPolicy();
        var journal = Attach();

        await WithRegistry(async cards =>
        {
            using var server = new TinyServer(_ => TinyServer.Reply(200, "[]"));

            var connection = Connect(cards, server.Port);
            var request    = Push(cards, connection.Id);

            Confirm(cards, request);

            var verdict = OutwardGuard.Check(cards, connection, request, Context(),
                                             scenario: "", dbId: 0, Now, payload: null);

            await RequestRunner.RunAsync(connection, request, "", Context(), verdict.Trouble, payload: null);

            var row  = journal.Recent().Single();
            var seen = server.Seen.Single().Body;

            Assert.Equal(SecurityJournal.Sha256(System.Text.Encoding.UTF8.GetBytes(seen)),
                         row.BodyHash);
        });
    }

    /// <summary>
    /// Строка ложится ДО отправки, а не после. Проверяется тем, что она есть даже тогда, когда
    /// отправка не состоялась вовсе: пиши ее по итогу — и попытка, оборвавшаяся на сокете, следа
    /// бы не оставила.
    /// </summary>
    [Fact]
    public async Task The_row_is_written_before_the_socket()
    {
        AttachPolicy();
        var journal = Attach();

        await WithRegistry(async cards =>
        {
            // Порт, на котором никто не слушает: отправка кончится сетевым сбоем.
            var connection = Connect(cards, 1);
            var request    = Push(cards, connection.Id);

            Confirm(cards, request);

            var verdict = OutwardGuard.Check(cards, connection, request, Context(),
                                             scenario: "", dbId: 0, Now, payload: null);

            var outcome = await RequestRunner.RunAsync(connection, request, "", Context(),
                                                       verdict.Trouble, payload: null);

            Assert.False(outcome.Ok);
            Assert.Equal(1, journal.Count());
        });
    }

    // ── Простой ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Отметка об использовании сдвигает отсчет простоя. Без нее направление, которым пользуются
    /// каждый день, отключилось бы через месяц после подтверждения — посреди работающего обмена.
    /// </summary>
    [Fact]
    public async Task Using_a_direction_restarts_the_idle_countdown()
    {
        AttachPolicy();
        Attach();

        await WithRegistry(cards =>
        {
            var connection = Connect(cards, 8080);
            var request    = Push(cards, connection.Id);

            Confirm(cards, request);

            var late = Now.AddDays(40);

            Assert.NotNull(cards.OutwardTrouble(request, late));

            OutwardGuard.Used(cards, request, Now.AddDays(20));

            Assert.Null(cards.OutwardTrouble(request, late));

            return Task.CompletedTask;
        });
    }

    /// <summary>Читающему запросу отмечать нечего: направления у него нет и не будет.</summary>
    [Fact]
    public async Task Marking_a_reading_request_changes_nothing()
    {
        AttachPolicy();
        Attach();

        await WithRegistry(cards =>
        {
            var connection = Connect(cards, 8080);
            var request    = Pull(cards, connection.Id);

            OutwardGuard.Used(cards, request, Now);

            Assert.Null(cards.Direction(request.Id));

            return Task.CompletedTask;
        });
    }
}
