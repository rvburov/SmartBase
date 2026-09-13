using SmartBase.Database;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Опись секретов машины (docs/ROADMAP-API.md, шаг 16).
///
/// Проверяются обещания описи, а не форматирование строк: что показать можно ровно там, где
/// секрет восстановим по построению; что «мы не считаем» и «не пользовались» — разные ответы;
/// что мертвые секреты стоят наверху, потому что за ними сюда и приходят.
/// </summary>
public sealed class SecretsInventoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    // ── Что можно показать ────────────────────────────────────────────────────

    /// <summary>
    /// Секрет соединения показать можно: им подписывается каждый запрос, и программа достает его
    /// сама при каждой отправке. Прятать то, что программа достает сама, — притворство.
    /// </summary>
    [Fact]
    public void A_connection_secret_can_be_revealed()
    {
        var entry = SecretsInventory.Connection(5, "Учетная система", "api.example.org",
                                                requests: 3, lastRun: Now, created: null);

        Assert.True(entry.Revealable);
        Assert.Empty(entry.Guard);
    }

    /// <summary>
    /// Все остальное не показывается никогда — и у каждого написано ПОЧЕМУ. Отсутствие кнопки
    /// без объяснения читается как недоделка, и человек идет искать ее в другом месте.
    /// </summary>
    [Fact]
    public void Everything_else_is_hidden_and_says_why()
    {
        SecretEntry[] hidden =
        [
            SecretsInventory.RobotToken(1, "Обмен склада", "Склад", Now, Now, enabled: true),
            SecretsInventory.DatabasePassword(2, "Заказы"),
            SecretsInventory.PgPassword(3, "Заказы", "pg.example.org:5432/orders"),
            SecretsInventory.PolicyMaster(defined: true, "policy.json"),
        ];

        foreach (var entry in hidden)
        {
            Assert.False(entry.Revealable);
            Assert.NotEmpty(entry.Guard);
        }
    }

    /// <summary>Мастер-пароля может не быть вовсе — и тогда об этом сказано, а строка остается.</summary>
    [Fact]
    public void A_missing_master_password_is_still_a_row()
    {
        var entry = SecretsInventory.PolicyMaster(defined: false, "");

        Assert.Contains("не задан", entry.Where);
        Assert.NotEmpty(entry.Guard);
    }

    // ── Мертвые секреты ───────────────────────────────────────────────────────

    [Fact]
    public void A_secret_used_yesterday_is_alive()
    {
        var entry = SecretsInventory.Connection(5, "Учетная система", "api.example.org",
                                                requests: 1, lastRun: Now.AddDays(-1), created: null);

        Assert.False(SecretsInventory.Stale(entry, Now));
    }

    [Fact]
    public void A_secret_untouched_for_a_quarter_is_dead()
    {
        var entry = SecretsInventory.Connection(5, "Учетная система", "api.example.org",
                                                requests: 1,
                                                lastRun: Now.AddDays(-SecretsInventory.StaleDays - 1),
                                                created: null);

        Assert.True(SecretsInventory.Stale(entry, Now));
    }

    /// <summary>Ни разу не пользовались — тоже мертвый: заведен и забыт в тот же день.</summary>
    [Fact]
    public void A_secret_never_used_is_dead()
    {
        var entry = SecretsInventory.RobotToken(1, "Обмен склада", "Склад",
                                                created: Now.AddDays(-200), used: null,
                                                enabled: true);

        Assert.True(SecretsInventory.Stale(entry, Now));
    }

    /// <summary>
    /// Секрет, о пользовании которым программа не знает, мертвым НЕ считается. Отозвать рабочий
    /// пароль базы по догадке хуже, чем не отозвать заброшенный: первое ломает работу сегодня.
    /// </summary>
    [Fact]
    public void A_secret_whose_use_is_not_tracked_is_never_called_dead()
    {
        Assert.False(SecretsInventory.Stale(SecretsInventory.DatabasePassword(2, "Заказы"), Now));
        Assert.False(SecretsInventory.Stale(SecretsInventory.PgPassword(3, "Заказы", "pg:5432"), Now));
        Assert.False(SecretsInventory.Stale(SecretsInventory.PolicyMaster(true, "policy.json"), Now));
    }

    /// <summary>Разницу видно и человеку: «не отмечается» — это не «ни разу».</summary>
    [Fact]
    public void Not_tracking_use_is_stated_in_the_row()
    {
        Assert.False(SecretsInventory.DatabasePassword(2, "Заказы").UseKnown);
        Assert.True(SecretsInventory.RobotToken(1, "Обмен", "Склад", Now, null, true).UseKnown);
    }

    // ── Порядок ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Мертвые наверх: список из тридцати строк, где две нужные лежат в середине, отвечает на
    /// вопрос хуже, чем не отвечает вовсе.
    /// </summary>
    [Fact]
    public void Dead_secrets_come_first()
    {
        var alive = SecretsInventory.Connection(1, "Живое", "a.example.org",
                                                requests: 1, lastRun: Now, created: null);
        var dead  = SecretsInventory.Connection(2, "Забытое", "b.example.org",
                                                requests: 1, lastRun: Now.AddYears(-1), created: null);

        var order = SecretsInventory.Ordered([alive, dead], Now);

        Assert.Equal("Соединение «Забытое»", order[0].Title);
    }

    /// <summary>Внутри — по роду и имени: список не должен перетасовываться между заходами.</summary>
    [Fact]
    public void Live_secrets_are_grouped_by_kind_then_name()
    {
        var token = SecretsInventory.RobotToken(1, "Обмен", "Склад", Now, Now, true);
        var conn2 = SecretsInventory.Connection(2, "Банк", "b.example.org", 1, Now, null);
        var conn1 = SecretsInventory.Connection(3, "Авто", "a.example.org", 1, Now, null);

        var order = SecretsInventory.Ordered([token, conn2, conn1], Now);

        Assert.Equal(["Соединение «Авто»", "Соединение «Банк»", "Токен «Обмен»"],
                     order.Select(e => e.Title));
    }

    // ── Ключи ─────────────────────────────────────────────────────────────────

    /// <summary>Ключ строки переживает обратный разбор: по нему экран находит, что менять.</summary>
    [Theory]
    [InlineData(SecretKind.Connection,       5)]
    [InlineData(SecretKind.RobotToken,       7)]
    [InlineData(SecretKind.DatabasePassword, 2)]
    [InlineData(SecretKind.PgPassword,       9)]
    public void A_key_survives_a_round_trip(SecretKind kind, int id)
    {
        Assert.Equal((kind, id), SecretsInventory.Parse(SecretsInventory.KeyOf(kind, id)));
    }

    /// <summary>У мастер-пароля ключа нет — он один на машину.</summary>
    [Fact]
    public void The_master_password_has_a_single_key()
    {
        Assert.Equal((SecretKind.PolicyMaster, 0), SecretsInventory.Parse("master"));
        Assert.Equal("master", SecretsInventory.KeyOf(SecretKind.PolicyMaster, 0));
    }

    /// <summary>Мусор в ключе не роняет экран, а читается как мастер-пароль — строка без действий.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("conn")]
    [InlineData("conn:abc")]
    [InlineData("что-то:5")]
    public void Garbage_in_a_key_does_not_throw(string key)
    {
        var (kind, id) = SecretsInventory.Parse(key);

        Assert.True(kind == SecretKind.PolicyMaster || id == 0);
    }

    // ── Где применяется ───────────────────────────────────────────────────────

    /// <summary>
    /// Соединение без единого запроса названо прямо: секрет заведен, а ходить им некуда — первый
    /// кандидат на отзыв.
    /// </summary>
    [Fact]
    public void A_connection_without_requests_says_so()
    {
        var entry = SecretsInventory.Connection(5, "Учетная система", "api.example.org",
                                                requests: 0, lastRun: null, created: null);

        Assert.Contains("запросов нет", entry.Where);
        Assert.True(SecretsInventory.Stale(entry, Now));
    }

    /// <summary>Выключенный токен помечен: он не работает, но и не удален.</summary>
    [Fact]
    public void A_disabled_token_is_marked()
    {
        var entry = SecretsInventory.RobotToken(1, "Обмен", "Склад", Now, Now, enabled: false);

        Assert.Contains("выключен", entry.Where);
    }

    /// <summary>Токен базы, которой сейчас нет на машине, не притворяется, что она есть.</summary>
    [Fact]
    public void A_token_of_a_missing_database_says_so()
    {
        var entry = SecretsInventory.RobotToken(1, "Обмен", "", Now, Now, enabled: true);

        Assert.Contains("не найдена", entry.Where);
    }

    // ── Места без пароля (что где установлено) ────────────────────────────────

    /// <summary>
    /// Место без пароля — это ответ, а не пропуск. Вопрос «где я поставил пароль, а где нет» —
    /// ровно тот, ради которого опись и заводят, и молчание на него читается как «такого места
    /// нет».
    /// </summary>
    [Fact]
    public void A_place_without_a_password_is_still_a_row()
    {
        var db   = SecretsInventory.DatabasePassword(2, "Заказы", SecretState.NotSet);
        var conn = SecretsInventory.Connection(5, "Открытый обмен", "api.example.org",
                                               requests: 2, lastRun: Now, created: null,
                                               hasSecret: false);
        var pg   = SecretsInventory.PgPassword(3, "Заказы", "pg:5432", hasPassword: false);

        foreach (var entry in new[] { db, conn, pg })
        {
            Assert.Equal(SecretState.NotSet, entry.State);
            Assert.NotEmpty(entry.Guard);          // сказано, почему пусто
            Assert.False(entry.Revealable);        // показывать нечего
        }
    }

    /// <summary>Пустое место не может быть «давно не нужным»: нечему быть забытым.</summary>
    [Fact]
    public void An_empty_place_is_never_called_dead()
    {
        var conn = SecretsInventory.Connection(5, "Открытый обмен", "api.example.org",
                                               requests: 0, lastRun: null, created: null,
                                               hasSecret: false);

        Assert.False(SecretsInventory.Stale(conn, Now));
        Assert.Equal("не установлен", SecretsInventory.StateText(conn, Now));
    }

    /// <summary>
    /// У закрытой базы — третий ответ. Сказать про нее «пароля нет» значило бы соврать ровно в
    /// том вопросе, ради которого опись заведена: пароль лежит внутри ее файла.
    /// </summary>
    [Fact]
    public void A_closed_database_answers_unknown()
    {
        var entry = SecretsInventory.DatabasePassword(2, "Архив", SecretState.Unknown);

        Assert.Equal(SecretState.Unknown, entry.State);
        Assert.Equal("неизвестно", SecretsInventory.StateText(entry, Now));
        Assert.Contains("закрыт", entry.Where);
        Assert.Contains("Откройте базу", entry.Guard);
        Assert.False(SecretsInventory.Stale(entry, Now));
    }

    /// <summary>Незаданный мастер-пароль — тоже «не установлен», а не строка-обманка.</summary>
    [Fact]
    public void A_missing_master_password_reads_as_not_set()
    {
        Assert.Equal(SecretState.NotSet, SecretsInventory.PolicyMaster(false, "").State);
        Assert.Equal(SecretState.Set,    SecretsInventory.PolicyMaster(true, "policy.json").State);
    }

    /// <summary>
    /// Порядок: мертвое — наверх, пустые места — вниз. Пустое не требует действия, но и пропасть
    /// не должно, иначе вопрос «где я пароль не поставил» остается без ответа.
    /// </summary>
    [Fact]
    public void Empty_places_sink_below_the_filled_ones()
    {
        var dead  = SecretsInventory.Connection(1, "Забытое", "a.example.org",
                                                1, Now.AddYears(-1), null);
        var alive = SecretsInventory.Connection(2, "Живое", "b.example.org", 1, Now, null);
        var empty = SecretsInventory.Connection(3, "Без секрета", "c.example.org",
                                                1, Now, null, hasSecret: false);
        var closed = SecretsInventory.DatabasePassword(4, "Архив", SecretState.Unknown);

        var order = SecretsInventory.Ordered([empty, closed, alive, dead], Now);

        Assert.Equal(["Соединение «Забытое»", "Соединение «Живое»",
                      "Пароль базы «Архив»",  "Соединение «Без секрета»"],
                     order.Select(e => e.Title));
    }

    /// <summary>Состояние словами — одно на колонку и на сводку.</summary>
    [Fact]
    public void The_state_is_said_in_words()
    {
        var alive = SecretsInventory.Connection(1, "Живое", "a.example.org", 1, Now, null);
        var dead  = SecretsInventory.Connection(2, "Забытое", "b.example.org",
                                                1, Now.AddYears(-1), null);

        Assert.Equal("установлен",     SecretsInventory.StateText(alive, Now));
        Assert.Equal("давно не нужен", SecretsInventory.StateText(dead, Now));
    }
}
