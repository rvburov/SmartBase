using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Паспорт интеграций (<see cref="IntegrationsPassport"/>) — файл, который отдают службе
/// безопасности (docs/ROADMAP-API.md, 10.16).
///
/// Проверяется не «сложился ли текст», а обещания документа. Он обязан назвать все исходящие
/// направления, включая НЕподтвержденные; обязан свести объемы по адресу без параметров запроса,
/// иначе один обмен рассыпается на десятки строк; обязан назвать имена полей и не назвать ни
/// одного значения; и обязан сам сказать, чего он не доказывает.
///
/// Набор идет в общей очереди шлюза: политика и журнал безопасности одни на приложение.
/// </summary>
[Collection(EgressCollection.Name)]
public sealed class IntegrationsPassportTests : IDisposable
{
    private readonly string _journal =
        Path.Combine(Path.GetTempPath(), $"sbpass_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        EgressGate.Close();
        SecurityJournal.Attach(null);

        try { SqliteGate.ClearAllPools(); } catch { /* best-effort */ }

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

    private static EgressPolicy Rules() => new(
        EgressMode.Internal,
        Hosts:           ["api.zavod.local"],
        Networks:        ["10.0.0.0/8"],
        Folders:         [@"P:\Обмен"],
        MaxRowsPerRun:   5000,
        MaxBytesPerDay:  50L * 1024 * 1024,
        MaxRunsPerHour:  60,
        ValidUntil:      new DateOnly(2026, 12, 31),
        UserMayAddHosts: false,
        Source:          "P:\\Раздача\\policy.json",
        MasterHash:      MasterPassword.Hash(Master));

    private static PolicyLoadResult Policy()
    {
        var set = EgressPolicySet.Of(Rules());
        EgressGate.AttachPolicy(set);

        return new PolicyLoadResult(set,
            [new PolicyReadResult("P:\\Раздача\\policy.json", true, "подпись верна")]);
    }

    private SecurityJournal Attach()
    {
        var journal = new SecurityJournal(_journal);
        SecurityJournal.Attach(journal);
        return journal;
    }

    private static async Task WithRegistry(Func<IntegrationsRegistry, Task> body)
    {
        var dir    = Directory.CreateTempSubdirectory("sbpass_").FullName;
        var appDb  = Path.Combine(dir, "app.db");
        var dataDb = Path.Combine(dir, "database.db");

        try
        {
            using (var registry = DatabaseRegistry.Open(dataDb, appDb))
                await body(registry.Integrations);
        }
        finally
        {
            try { SqliteGate.ClearAllPools(); } catch { /* best-effort */ }
            TempDatabase.Cleanup(appDb);
            TempDatabase.Cleanup(dataDb);
            try { Directory.Delete(dir, recursive: true); } catch { /* временная папка */ }
        }
    }

    /// <summary>Соединение, читающий запрос и запрос-отправитель — обычный набор боевой машины.</summary>
    private static (IntegrationRequest Pull, IntegrationRequest Push) Cards(IntegrationsRegistry cards)
    {
        var connection = cards.Save(IntegrationConnection.New() with
        {
            Name           = "Учетная система",
            Address        = "https://api.zavod.local",
            Auth           = ConnectionAuth.Header,
            AuthName       = "X-Token",
            TimeoutSeconds = 30,
        });

        var pull = cards.SaveRequest(IntegrationRequest.New() with
        {
            Name         = "Забрать заказы",
            ConnectionId = connection.Id,
            Path         = "/orders",
        });

        var push = cards.SaveRequest(IntegrationRequest.New() with
        {
            Name         = "Отдать итог",
            ConnectionId = connection.Id,
            Method       = RequestMethod.Post,
            Path         = "/result",
            Body         = "{\"итог\":1}",
        });

        return (pull, push);
    }

    private static string Build(IntegrationsRegistry cards, SecurityJournal? journal,
                                IReadOnlyList<PassportDatabase>? databases = null) =>
        IntegrationsPassport.Build(cards, Policy(), journal, databases ?? [],
                                   "Иванов И. И.", Now);

    // ── Что разрешено ─────────────────────────────────────────────────────────

    /// <summary>
    /// Политика приезжает в паспорт целиком: режим, срок, источники и потолки. Это первое, о чем
    /// спрашивают, и собирать ответ из нескольких документов проверяющий не должен.
    /// </summary>
    [Fact]
    public async Task The_policy_is_written_out_whole()
    {
        await WithRegistry(cards =>
        {
            var text = Build(cards, null);

            Assert.Contains("внутренний контур", text, StringComparison.Ordinal);
            Assert.Contains("31.12.2026",        text, StringComparison.Ordinal);
            Assert.Contains("api.zavod.local",   text, StringComparison.Ordinal);
            Assert.Contains("10.0.0.0/8",        text, StringComparison.Ordinal);
            Assert.Contains(@"P:\Обмен",         text, StringComparison.Ordinal);
            Assert.Contains("подпись верна",     text, StringComparison.Ordinal);

            // Число разрядится тем же способом, каким его разряжает сам паспорт: разделитель
            // групп зависит от языка машины, и вписывать его в тест значило бы проверять
            // настройки Windows, а не документ.
            Assert.Contains(5000.ToString("N0"), text, StringComparison.Ordinal);

            return Task.CompletedTask;
        });
    }

    /// <summary>Задан ли мастер-пароль — сведение само по себе: без него направлений нет вовсе.</summary>
    [Fact]
    public async Task Whether_a_master_password_exists_is_stated()
    {
        await WithRegistry(cards =>
        {
            Assert.Contains("Мастер-пароль  задан", Build(cards, null), StringComparison.Ordinal);
            return Task.CompletedTask;
        });
    }

    // ── С чем соединяется ─────────────────────────────────────────────────────

    /// <summary>
    /// Соединения и запросы названы, и у каждого написано, читает он или уносит. Ровно это и
    /// есть ответ на вопрос «с чем соединяется программа».
    /// </summary>
    [Fact]
    public async Task Connections_say_which_requests_carry_data_out()
    {
        await WithRegistry(cards =>
        {
            Cards(cards);

            var text = Build(cards, null);

            Assert.Contains("«Учетная система»",       text, StringComparison.Ordinal);
            Assert.Contains("https://api.zavod.local", text, StringComparison.Ordinal);
            Assert.Contains("GET    /orders",          text, StringComparison.Ordinal);
            Assert.Contains("POST   /result",          text, StringComparison.Ordinal);
            Assert.Contains("[уносит данные]",         text, StringComparison.Ordinal);
            Assert.Contains("[только читает]",         text, StringComparison.Ordinal);

            return Task.CompletedTask;
        });
    }

    /// <summary>Секрет соединения в паспорт не попадает — назван только способ входа.</summary>
    [Fact]
    public async Task The_secret_of_a_connection_is_never_written()
    {
        await WithRegistry(cards =>
        {
            var (_, push) = Cards(cards);

            cards.Save(cards.Find(push.ConnectionId)!, "очень-секретный-токен");

            var text = Build(cards, null);

            Assert.DoesNotContain("очень-секретный-токен", text, StringComparison.Ordinal);
            Assert.Contains("секрет в заголовке «X-Token»", text, StringComparison.Ordinal);

            return Task.CompletedTask;
        });
    }

    // ── Кому разрешено ────────────────────────────────────────────────────────

    /// <summary>
    /// НЕподтвержденное направление названо наравне с подтвержденным. Паспорт, показывающий
    /// только разрешенное, отвечал бы на вопрос «что согласовано», а спрашивают его о другом —
    /// «что вообще умеет уносить данные».
    /// </summary>
    [Fact]
    public async Task An_unconfirmed_direction_is_listed_too()
    {
        await WithRegistry(cards =>
        {
            Cards(cards);

            var text = Build(cards, null);

            Assert.Contains("«Отдать итог»",   text, StringComparison.Ordinal);
            Assert.Contains("НЕ ПОДТВЕРЖДЕНО", text, StringComparison.Ordinal);

            return Task.CompletedTask;
        });
    }

    /// <summary>Подтвержденное направление несет имя подтвердившего и срок — то, что и проверяют.</summary>
    [Fact]
    public async Task A_confirmed_direction_names_who_and_until_when()
    {
        await WithRegistry(cards =>
        {
            var (_, push) = Cards(cards);

            // Политика прикладывается ДО подтверждения: мастер-пароль проверяет она, и без нее
            // подтверждать нечем.
            Policy();

            cards.ConfirmDirection(push, Master, "Петров П. П.", days: 90, Now, out var trouble);
            Assert.Equal("", trouble);

            var text = Build(cards, null);

            Assert.Contains("Петров П. П.", text, StringComparison.Ordinal);
            Assert.Contains("10.12.2026",   text, StringComparison.Ordinal);   // Now + 90 дней
            Assert.Contains("действует",    text, StringComparison.Ordinal);

            return Task.CompletedTask;
        });
    }

    /// <summary>Читающий запрос в раздел о направлениях не попадает: уносить ему нечего.</summary>
    [Fact]
    public async Task A_reading_request_is_not_in_the_directions_section()
    {
        await WithRegistry(cards =>
        {
            Cards(cards);

            var directions = Section(Build(cards, null), "4. КОМУ РАЗРЕШЕНО", "5. ЧТО УХОДИЛО");

            Assert.DoesNotContain("Забрать заказы", directions, StringComparison.Ordinal);
            Assert.Contains("Отдать итог",          directions, StringComparison.Ordinal);

            return Task.CompletedTask;
        });
    }

    // ── Что уходило ───────────────────────────────────────────────────────────

    /// <summary>
    /// Объемы сводятся по адресу БЕЗ параметров запроса. Обмен подставляет в них дату и номер
    /// страницы, и по полному адресу месячная выгрузка рассыпалась бы на тридцать строк, в
    /// которых уже ничего не видно.
    /// </summary>
    [Fact]
    public async Task Volumes_are_summed_by_address_without_query()
    {
        var journal = Attach();

        for (var day = 1; day <= 3; day++)
            journal.Write(SecurityEntry.New(
                SecurityEvent.Send, allowed: true, "Иванов И. И.", "Итоги суток",
                $"https://api.zavod.local/result?день=2026-09-0{day}", "итог, клиент",
                rows: 10, bytes: 1024, bodyHash: "", explanation: "разрешено", dbId: 1,
                at: Now.AddDays(-day)));

        await WithRegistry(cards =>
        {
            var traffic = Section(Build(cards, journal), "5. ЧТО УХОДИЛО", "6. МЕТКИ");

            Assert.Contains("https://api.zavod.local/result", traffic, StringComparison.Ordinal);
            Assert.DoesNotContain("день=", traffic, StringComparison.Ordinal);
            Assert.Contains("разрешено   3", traffic, StringComparison.Ordinal);
            Assert.Contains("записей 30",    traffic, StringComparison.Ordinal);

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Заблокированные попытки посчитаны отдельно от разрешенных. Слить их в одно число значило
    /// бы спрятать ровно то, ради чего паспорт и смотрят.
    /// </summary>
    [Fact]
    public async Task Blocked_attempts_are_counted_apart()
    {
        var journal = Attach();

        journal.Write(SecurityEntry.New(
            SecurityEvent.Send, allowed: true, "Иванов И. И.", "", "https://api.zavod.local/result",
            "итог", 1, 100, "", "разрешено", 0, Now));

        journal.Write(SecurityEntry.New(
            SecurityEvent.Send, allowed: false, "Иванов И. И.", "", "https://api.zavod.local/result",
            "итог", 0, 100, "", "не подтверждено мастер-паролем", 0, Now));

        await WithRegistry(cards =>
        {
            var traffic = Section(Build(cards, journal), "5. ЧТО УХОДИЛО", "6. МЕТКИ");

            Assert.Contains("из них заблокировано 1", traffic, StringComparison.Ordinal);
            Assert.Contains("заблокировано 1",        traffic, StringComparison.Ordinal);

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Имена полей — да, значения — никогда. Паспорт уезжает из организации так же легко, как
    /// журнал, и сделан по тем же правилам.
    /// </summary>
    [Fact]
    public async Task Fields_are_named_and_values_are_not()
    {
        var journal = Attach();

        journal.Write(SecurityEntry.New(
            SecurityEvent.Send, allowed: true, "Иванов И. И.", "", "https://api.zavod.local/result",
            "итог, клиент", 1, 100, SecurityJournal.Sha256("{\"клиент\":\"Ромашка\"}"),
            "разрешено", 0, Now));

        await WithRegistry(cards =>
        {
            var text = Build(cards, journal);

            Assert.Contains("итог, клиент", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Ромашка", text, StringComparison.Ordinal);

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// События старше периода в сводку не идут — и документ об этом сам говорит. Умолчи он, и
    /// «за период не уходило ничего» прочли бы как «не уходило никогда».
    /// </summary>
    [Fact]
    public async Task What_is_older_than_the_period_stays_out_and_is_declared()
    {
        var journal = Attach();

        journal.Write(SecurityEntry.New(
            SecurityEvent.Send, allowed: true, "Иванов И. И.", "", "https://api.zavod.local/old",
            "итог", 1, 100, "", "разрешено", 0, Now.AddDays(-100)));

        await WithRegistry(cards =>
        {
            var text = Build(cards, journal);

            Assert.DoesNotContain("/old", text, StringComparison.Ordinal);
            Assert.Contains("События старше в нее не вошли", text, StringComparison.Ordinal);

            return Task.CompletedTask;
        });
    }

    // ── Метки и журнал ────────────────────────────────────────────────────────

    /// <summary>Метка базы и число помеченных свойств — строкой на каждую открытую базу.</summary>
    [Fact]
    public async Task Database_labels_are_listed()
    {
        await WithRegistry(cards =>
        {
            var text = Build(cards, null,
            [
                new PassportDatabase(1, "Конструкторская", ConfidentialityLabel.Closed, 7),
            ]);

            Assert.Contains("«Конструкторская»", text, StringComparison.Ordinal);
            Assert.Contains("свойств с метками: 7", text, StringComparison.Ordinal);

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Испорченная цепочка попадает в паспорт. Документ, умолчавший о том, что журнал правили,
    /// хуже отсутствующего: на него сошлются как на доказательство.
    /// </summary>
    [Fact]
    public async Task A_broken_chain_is_reported_in_the_passport()
    {
        var journal = Attach();

        journal.Write(SecurityEntry.New(SecurityEvent.Send, true, "Иванов И. И.", "", "/result",
                                        "итог", 1, 100, "", "разрешено", 0, Now));

        // Так правит файл тот, кто хочет убрать след: прямым SQL мимо программы.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_journal}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE events SET Allowed = 0 WHERE Id = 1;";
            command.ExecuteNonQuery();
        }

        await WithRegistry(cards =>
        {
            Assert.Contains("РАЗОШЛАСЬ на строке №1", Build(cards, journal),
                            StringComparison.Ordinal);

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Журнал не открылся — паспорт говорит об этом прямо, а не отдает пустую сводку. Пустая
    /// читалась бы как «наружу ничего не уходило».
    /// </summary>
    [Fact]
    public async Task A_missing_journal_is_declared_and_not_shown_as_silence()
    {
        await WithRegistry(cards =>
        {
            var text = Build(cards, null);

            Assert.Contains("НЕ ОТКРЫЛСЯ", text, StringComparison.Ordinal);
            Assert.DoesNotContain("не уходило ничего", text, StringComparison.Ordinal);

            return Task.CompletedTask;
        });
    }

    // ── Честность документа ───────────────────────────────────────────────────

    /// <summary>
    /// Последним разделом паспорт говорит, чего он НЕ доказывает. Его составила та самая
    /// программа, о которой он рассказывает, и умолчать об этом значило бы выдать сводку за
    /// доказательство.
    /// </summary>
    [Fact]
    public async Task The_passport_says_what_it_does_not_prove()
    {
        await WithRegistry(cards =>
        {
            var text = Build(cards, null);

            Assert.Contains("ЧЕГО ЭТОТ ДОКУМЕНТ НЕ ДОКАЗЫВАЕТ", text, StringComparison.Ordinal);
            Assert.Contains("сводкой, а не доказательством",     text, StringComparison.Ordinal);

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Копия журнала вне машины названа — без нее цепочка отпечатков не ловит подмену файла
    /// целиком, — но названа как то, чего программа НЕ делает. Раньше паспорт перечислял ее
    /// среди того, чем он проверяется, и проверяющий обопрался бы на рубеж, которого нет.
    /// Документ, врущий службе безопасности о собственных рубежах, хуже ненаписанного.
    /// </summary>
    [Fact]
    public async Task The_passport_does_not_pass_off_a_missing_audit_copy_as_a_control()
    {
        await WithRegistry(cards =>
        {
            var text = Build(cards, null);

            Assert.Contains("папке аудита организации",       text, StringComparison.Ordinal);
            Assert.Contains("ТАКОЙ КОПИИ ПРОГРАММА НЕ ВЕДЕТ", text, StringComparison.Ordinal);

            return Task.CompletedTask;
        });
    }

    /// <summary>Кто и когда выписал — без этого документ не подшить.</summary>
    [Fact]
    public async Task The_passport_names_its_author_and_moment()
    {
        await WithRegistry(cards =>
        {
            var text = Build(cards, null);

            Assert.Contains("Иванов И. И.", text, StringComparison.Ordinal);
            Assert.Contains("11.09.2026",   text, StringComparison.Ordinal);

            return Task.CompletedTask;
        });
    }

    // ── Мелочи ────────────────────────────────────────────────────────────────

    /// <summary>Кусок текста между двумя заголовками — чтобы искать в нужном разделе.</summary>
    // ── Кто может постучаться к нам ───────────────────────────────────────────

    /// <summary>
    /// Входящий канал в паспорте есть, и он называет главное: кому выдан доступ, куда он ведет
    /// и чего НЕ позволяет. Про открытый порт проверяющий спрашивает раньше, чем про исходящие,
    /// и ответ обязан лежать в том же документе.
    /// </summary>
    [Fact]
    public async Task The_inward_channel_is_written_out_with_its_tokens()
    {
        await WithRegistry(cards =>
        {
            cards.Issue("Обмен учетной системы", dbId: 1, "ПК17", Now);

            var text = IntegrationsPassport.Build(
                cards, Policy(), null,
                [new PassportDatabase(1, "Основная", ConfidentialityLabel.Open, 0)],
                "Иванов И. И.", Now, ingress: IngressMode.Loopback,
                ingressPort: IngressGate.DefaultPort);

            var section = Section(text, "3. КТО МОЖЕТ ПОСТУЧАТЬСЯ", "4. КОМУ РАЗРЕШЕНО");

            Assert.Contains("Обмен учетной системы", section, StringComparison.Ordinal);
            Assert.Contains("Основная",              section, StringComparison.Ordinal);
            Assert.Contains("обращений не было",     section, StringComparison.Ordinal);
            Assert.Contains(IngressGate.LoopbackHost, section, StringComparison.Ordinal);

            // Главные утверждения раздела: чтение закрыто выдачей, а выдач здесь нет ни одной —
            // и об этом сказано словами, а не пустотой.
            Assert.Contains("ВЫДАЧЕЙ", section, StringComparison.Ordinal);
            Assert.Contains("Выдач чтения нет ни одной", section, StringComparison.Ordinal);

            return Task.CompletedTask;
        });
    }

    /// <summary>Ни одного токена — тоже ответ, и он должен быть написан словами.</summary>
    [Fact]
    public async Task With_no_tokens_the_passport_says_so()
    {
        await WithRegistry(cards =>
        {
            var section = Section(Build(cards, null),
                                  "3. КТО МОЖЕТ ПОСТУЧАТЬСЯ", "4. КОМУ РАЗРЕШЕНО");

            Assert.Contains("не выдано ни одного", section, StringComparison.Ordinal);

            return Task.CompletedTask;
        });
    }

    private static string Section(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"в паспорте нет раздела «{from}»");

        var end = text.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, $"в паспорте нет раздела «{to}»");

        return text[start..end];
    }
}
