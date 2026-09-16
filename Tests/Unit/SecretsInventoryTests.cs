using SmartBase.Database;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Опись секретов машины — менеджер паролей (docs/ROADMAP-API.md, шаг 16).
///
/// Проверяются обещания описи, а не форматирование строк: кто что видит (граница — админство
/// открытой базы); что человек без админства получает объяснение, а не пустоту; что состояние
/// строки не зовет чинить исправное и не молчит о мертвом; что показать можно ровно там, где
/// секрет восстановим по построению; что мертвые секреты стоят наверху.
/// </summary>
public sealed class SecretsInventoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    // ── Данные ────────────────────────────────────────────────────────────────

    private static InventoryDatabase Sqlite(int id, string name, bool admin = true,
                                            bool hasPassword = false, bool access = true,
                                            DateTimeOffset? unlock = null) =>
        new(id, name, admin, IsPostgres: false,
            Path: $@"C:\Базы\{name}.db", PgHost: "", PgPort: 5432, PgDatabase: "",
            HasServerPassword: false,
            HasPassword: hasPassword, PasswordAccessEnabled: access, LastPasswordUnlock: unlock);

    private static InventoryDatabase Postgres(int id, string name, bool admin = true,
                                              bool serverPassword = true) =>
        new(id, name, admin, IsPostgres: true,
            Path: "postgres://pg.zavod.local:5433/orders", PgHost: "pg.zavod.local", PgPort: 5433,
            PgDatabase: "orders", HasServerPassword: serverPassword,
            HasPassword: false, PasswordAccessEnabled: true, LastPasswordUnlock: null);

    private static InventoryConnection Connection(int id, string name, bool secret = true,
                                                  DateTimeOffset? lastRun = null) =>
        new(id, name, $"https://api{id}.example.org", secret, lastRun);

    private static InventoryToken Token(int id, string name, int dbId, bool enabled = true,
                                        DateTimeOffset? created = null,
                                        DateTimeOffset? used = null) =>
        new(id, name, dbId, enabled, created, used);

    private static readonly InventoryMaster NoMaster = new(false, []);

    private static InventoryListing Build(IReadOnlyList<InventoryDatabase>    databases,
                                          IReadOnlyList<InventoryConnection>? connections = null,
                                          IReadOnlyList<InventoryToken>?      tokens      = null,
                                          InventoryMaster?                    master      = null) =>
        SecretsInventory.Build(
            new InventoryFacts(databases, connections ?? [], tokens ?? [], master ?? NoMaster), Now);

    private static SecretEntry Only(InventoryListing listing, SecretKind kind) =>
        Assert.Single(listing.Entries, e => e.Kind == kind);

    // ── Кто что видит ─────────────────────────────────────────────────────────

    /// <summary>
    /// Человек, который не администрирует ни одной открытой базы, не видит ни одного пароля — ни
    /// баз, ни соединений, ни мастер-пароля. Экран показывает ему строку-заглушку, и в ней названа
    /// граница целиком: иначе пустота читается как поломка.
    /// </summary>
    [Fact]
    public void Someone_who_administers_no_open_database_sees_only_the_stub()
    {
        var listing = Build(
            [Sqlite(1, "Склад", admin: false, hasPassword: true)],
            [Connection(1, "Учетная система")],
            [Token(1, "Обмен склада", dbId: 1)],
            new InventoryMaster(true, ["реестр HKLM"]));

        Assert.False(listing.Admin);
        Assert.Empty(listing.Entries);

        Assert.Equal("Нет прав администратора", SecretsInventory.NoAccessTitle);
        Assert.Contains("только администратор", SecretsInventory.NoAccessText);
        Assert.Contains("хотя бы одной открытой базы", SecretsInventory.NoAccessText);
    }

    /// <summary>Открытых баз нет — спрашивать об админстве некого, и видеть нечего.</summary>
    [Fact]
    public void Without_open_databases_there_is_no_admin()
    {
        var listing = Build([], [Connection(1, "Учетная система")]);

        Assert.False(listing.Admin);
        Assert.Empty(listing.Entries);
    }

    /// <summary>Пароль базы — только ее администратору: чужой базой он не распоряжается.</summary>
    [Fact]
    public void A_database_password_is_shown_only_to_its_admin()
    {
        var listing = Build([Sqlite(1, "Склад"), Sqlite(2, "Бухгалтерия", admin: false)]);

        Assert.True(listing.Admin);
        Assert.Equal([1], listing.Entries.Where(e => e.Kind == SecretKind.DatabasePassword)
                                         .Select(e => e.Id));
    }

    /// <summary>Пароль сервера — только у базы PostgreSQL и только ее администратору.</summary>
    [Fact]
    public void A_server_password_needs_postgres_and_admin()
    {
        var listing = Build([Postgres(1, "Заказы"), Postgres(2, "Чужая", admin: false),
                             Sqlite(3, "Склад")]);

        Assert.Equal([1], listing.Entries.Where(e => e.Kind == SecretKind.PgPassword)
                                         .Select(e => e.Id));
    }

    /// <summary>
    /// Токен — только если его база открыта и человек ее администратор. Токен закрытой базы и
    /// токен чужой базы в описи не появляются.
    /// </summary>
    [Fact]
    public void Tokens_follow_the_rights_on_their_database()
    {
        var listing = Build([Sqlite(1, "Склад"), Sqlite(2, "Бухгалтерия", admin: false)],
                            tokens: [Token(10, "Свой", dbId: 1), Token(11, "Чужой", dbId: 2),
                                     Token(12, "Закрытой базы", dbId: 99)]);

        Assert.Equal([10], listing.Entries.Where(e => e.Kind == SecretKind.RobotToken)
                                          .Select(e => e.Id));
    }

    /// <summary>
    /// Соединения и мастер-пароль общие на машину — их видит администратор ХОТЯ БЫ ОДНОЙ открытой
    /// базы, тем же признаком, что открывает панель администратора.
    /// </summary>
    [Fact]
    public void Connections_and_master_password_need_admin_of_any_open_database()
    {
        var listing = Build([Sqlite(1, "Бухгалтерия", admin: false), Sqlite(2, "Склад")],
                            [Connection(1, "Учетная система"), Connection(2, "Банк", secret: false)]);

        Assert.Equal(2, listing.Entries.Count(e => e.Kind == SecretKind.Connection));
        Only(listing, SecretKind.PolicyMaster);
    }

    // ── Названия и пути ───────────────────────────────────────────────────────

    [Fact]
    public void Titles_name_the_kind_and_the_engine()
    {
        var listing = Build([Sqlite(1, "Склад"), Postgres(2, "Заказы")],
                            [Connection(1, "Учетная система")],
                            [Token(1, "Обмен склада", dbId: 1)]);

        Assert.Equal(
            ["Мастер-пароль организации",
             "Пароль базы PostgreSQL «Заказы»",
             "Пароль базы SQLite «Склад»",
             "Пароль сервера PostgreSQL для базы «Заказы»",
             "Соединение «Учетная система»",
             "Токен «Обмен склада»"],
            listing.Entries.Select(e => e.Title).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_third_line_says_where_the_secret_lives()
    {
        var listing = Build([Sqlite(1, "Склад"), Postgres(2, "Заказы")],
                            [Connection(7, "Учетная система")],
                            [Token(1, "Обмен склада", dbId: 1)]);

        Assert.Equal(@"C:\Базы\Склад.db",
                     listing.Entries.Single(e => e.Kind == SecretKind.DatabasePassword && e.Id == 1).Where);
        Assert.Equal("pg.zavod.local:5433/orders",
                     listing.Entries.Single(e => e.Kind == SecretKind.DatabasePassword && e.Id == 2).Where);
        Assert.Equal("pg.zavod.local:5433/orders", Only(listing, SecretKind.PgPassword).Where);
        Assert.Equal("https://api7.example.org", Only(listing, SecretKind.Connection).Where);
        Assert.Equal("база «Склад»", Only(listing, SecretKind.RobotToken).Where);
    }

    [Fact]
    public void A_server_without_a_host_says_so()
    {
        Assert.Equal("сервер не указан", SecretsInventory.ServerOf("", 5432, "orders"));
    }

    // ── Пароль базы ───────────────────────────────────────────────────────────

    [Fact]
    public void A_database_without_a_password_is_a_grey_empty_place()
    {
        var entry = Only(Build([Sqlite(1, "Склад")]), SecretKind.DatabasePassword);

        Assert.Equal("не установлен", entry.Status);
        Assert.Equal(SecretTone.Muted, entry.Tone);
        Assert.Equal(SecretState.NotSet, entry.State);
    }

    /// <summary>
    /// Пароль задан, но вход по паролю выключен — серым: пароль сейчас никого не пускает, и
    /// зеленый цвет обещал бы дверь, которой нет. Дата последнего входа при этом не показывается.
    /// </summary>
    [Fact]
    public void A_password_behind_a_switched_off_login_is_grey()
    {
        var entry = Only(Build([Sqlite(1, "Склад", hasPassword: true, access: false,
                                       unlock: Now.AddDays(-3))]),
                         SecretKind.DatabasePassword);

        Assert.Equal("установлен · вход по паролю выключен", entry.Status);
        Assert.Equal(SecretTone.Muted, entry.Tone);
        Assert.Equal(SecretState.Set, entry.State);
    }

    [Fact]
    public void A_working_password_shows_the_last_login_by_password()
    {
        var moment = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        var entry = Only(Build([Sqlite(1, "Склад", hasPassword: true, unlock: moment)]),
                         SecretKind.DatabasePassword);

        Assert.Equal($"установлен · последний вход по паролю {moment.ToLocalTime():dd.MM.yyyy}",
                     entry.Status);
        Assert.Equal(SecretTone.Good, entry.Tone);
    }

    [Fact]
    public void A_working_password_without_a_known_login_is_just_set()
    {
        var entry = Only(Build([Sqlite(1, "Склад", hasPassword: true)]),
                         SecretKind.DatabasePassword);

        Assert.Equal("установлен", entry.Status);
        Assert.Equal(SecretTone.Good, entry.Tone);
    }

    /// <summary>
    /// Пароль базы «давно не нужным» не называется никогда: паролем входит новичок, а новички
    /// приходят раз в год. Снимать пароль по такой отметке значило бы ломать прием людей.
    /// </summary>
    [Fact]
    public void A_database_password_is_never_called_dead()
    {
        var listing = Build([Sqlite(1, "Склад", hasPassword: true, unlock: Now.AddYears(-2))]);
        var entry   = Only(listing, SecretKind.DatabasePassword);

        Assert.False(SecretsInventory.Stale(entry, Now));
        Assert.DoesNotContain("давно", entry.Status);
        Assert.Equal(SecretTone.Good, entry.Tone);
    }

    // ── Пароль сервера ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true,  "установлен",    SecretTone.Good)]
    [InlineData(false, "не установлен", SecretTone.Muted)]
    public void A_server_password_is_set_or_not(bool saved, string status, SecretTone tone)
    {
        var entry = Only(Build([Postgres(1, "Заказы", serverPassword: saved)]), SecretKind.PgPassword);

        Assert.Equal(status, entry.Status);
        Assert.Equal(tone, entry.Tone);
        Assert.False(SecretsInventory.Stale(entry, Now));
    }

    // ── Соединения ────────────────────────────────────────────────────────────

    [Fact]
    public void A_connection_used_yesterday_is_green()
    {
        var entry = Only(Build([Sqlite(1, "Склад")], [Connection(1, "Учетная система",
                                                                 lastRun: Now.AddDays(-1))]),
                         SecretKind.Connection);

        Assert.Equal("установлен", entry.Status);
        Assert.Equal(SecretTone.Good, entry.Tone);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_connection_untouched_for_a_quarter_or_never_is_red(bool neverRun)
    {
        var lastRun = neverRun ? (DateTimeOffset?)null : Now.AddDays(-SecretsInventory.StaleDays - 1);

        var entry = Only(Build([Sqlite(1, "Склад")], [Connection(1, "Учетная система",
                                                                 lastRun: lastRun)]),
                         SecretKind.Connection);

        Assert.True(SecretsInventory.Stale(entry, Now));
        Assert.Equal("давно не нужен", entry.Status);
        Assert.Equal(SecretTone.Alarm, entry.Tone);
    }

    /// <summary>Место без секрета — серое «не установлен», и мертвым оно не бывает: нечему быть забытым.</summary>
    [Fact]
    public void A_connection_without_a_secret_is_a_grey_empty_place()
    {
        var entry = Only(Build([Sqlite(1, "Склад")], [Connection(1, "Открытый обмен", secret: false)]),
                         SecretKind.Connection);

        Assert.Equal("не установлен", entry.Status);
        Assert.Equal(SecretTone.Muted, entry.Tone);
        Assert.Equal(SecretState.NotSet, entry.State);
        Assert.False(SecretsInventory.Stale(entry, Now));
        Assert.NotEmpty(entry.Guard);
    }

    // ── Токены ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Выключенный токен — не открытая дверь: его выключили нарочно, и «давно не нужным» он не
    /// считается, сколько бы им ни пользовались.
    /// </summary>
    [Fact]
    public void A_disabled_token_is_grey_and_never_dead()
    {
        var entry = Only(Build([Sqlite(1, "Склад")],
                               tokens: [Token(1, "Обмен", dbId: 1, enabled: false,
                                              created: Now.AddYears(-1))]),
                         SecretKind.RobotToken);

        Assert.Equal("выключен", entry.Status);
        Assert.Equal(SecretTone.Muted, entry.Tone);
        Assert.False(SecretsInventory.Stale(entry, Now));
    }

    [Fact]
    public void An_enabled_token_nobody_used_for_a_quarter_is_red()
    {
        var listing = Build([Sqlite(1, "Склад")],
                            tokens: [Token(1, "Забытый", dbId: 1, created: Now.AddDays(-200)),
                                     Token(2, "Замолчавший", dbId: 1, created: Now.AddDays(-400),
                                           used: Now.AddDays(-SecretsInventory.StaleDays - 1))]);

        foreach (var entry in listing.Entries.Where(e => e.Kind == SecretKind.RobotToken))
        {
            Assert.Equal("давно не нужен", entry.Status);
            Assert.Equal(SecretTone.Alarm, entry.Tone);
        }
    }

    /// <summary>
    /// Токен, выданный вчера, еще не мертв, даже если обращений не было: простой считается от
    /// выдачи. Иначе каждый новый токен рождался бы красным.
    /// </summary>
    [Fact]
    public void A_fresh_token_is_green_before_the_first_call()
    {
        var entry = Only(Build([Sqlite(1, "Склад")],
                               tokens: [Token(1, "Новый", dbId: 1, created: Now.AddDays(-1))]),
                         SecretKind.RobotToken);

        Assert.Equal("установлен", entry.Status);
        Assert.Equal(SecretTone.Good, entry.Tone);
    }

    // ── Мастер-пароль ─────────────────────────────────────────────────────────

    [Fact]
    public void A_defined_master_password_names_its_sources()
    {
        var entry = Only(Build([Sqlite(1, "Склад")],
                               master: new InventoryMaster(true, ["реестр HKLM", "папка установки"])),
                         SecretKind.PolicyMaster);

        Assert.Equal("задан", entry.Status);
        Assert.Equal(SecretTone.Good, entry.Tone);
        Assert.Equal("реестр HKLM, папка установки", entry.Where);
    }

    [Fact]
    public void A_missing_master_password_is_a_grey_row_about_the_policy()
    {
        var entry = Only(Build([Sqlite(1, "Склад")]), SecretKind.PolicyMaster);

        Assert.Equal("не задан", entry.Status);
        Assert.Equal(SecretTone.Muted, entry.Tone);
        Assert.Equal("политика организации", entry.Where);
        Assert.Equal(SecretState.NotSet, entry.State);
    }

    // ── Что можно показать ────────────────────────────────────────────────────

    /// <summary>
    /// Показать можно только секрет соединения, и только заданный: им подписывается каждый
    /// запрос, и программа достает его сама. У всего остального написано, почему нельзя.
    /// </summary>
    [Fact]
    public void Only_a_connection_secret_can_be_revealed_and_the_rest_say_why()
    {
        var listing = Build([Sqlite(1, "Склад", hasPassword: true), Postgres(2, "Заказы")],
                            [Connection(1, "С секретом", lastRun: Now), Connection(2, "Без", secret: false)],
                            [Token(1, "Обмен", dbId: 1, created: Now)],
                            new InventoryMaster(true, ["реестр HKLM"]));

        Assert.Equal([1], listing.Entries.Where(e => e.Revealable).Select(e => e.Id));
        Assert.All(listing.Entries.Where(e => e.Revealable),
                   e => Assert.Equal(SecretKind.Connection, e.Kind));
        Assert.All(listing.Entries, e => Assert.NotEmpty(e.Guard));
        Assert.All(listing.Entries, e => Assert.NotEmpty(e.What));
    }

    [Fact]
    public void The_tip_joins_what_and_where_it_is_kept()
    {
        var entry = Only(Build([Sqlite(1, "Склад", hasPassword: true)]), SecretKind.DatabasePassword);

        Assert.Equal(entry.What + "\n\n" + entry.Guard, SecretsInventory.Tip(entry));
    }

    // ── Порядок ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Мертвое — наверх, пустые места — вниз, внутри — по роду и имени. За мертвыми сюда и
    /// приходят; пустые не требуют действия, но и пропасть не должны.
    /// </summary>
    [Fact]
    public void Dead_first_empty_last_then_kind_and_name()
    {
        var listing = Build([Sqlite(1, "Склад")],
                            [Connection(1, "Живое", lastRun: Now),
                             Connection(2, "Забытое", lastRun: Now.AddYears(-1)),
                             Connection(3, "Без секрета", secret: false),
                             Connection(4, "Архив", lastRun: Now)],
                            master: new InventoryMaster(true, ["реестр HKLM"]));

        Assert.Equal(["Соединение «Забытое»",
                      "Соединение «Архив»", "Соединение «Живое»", "Мастер-пароль организации",
                      "Соединение «Без секрета»", "Пароль базы SQLite «Склад»"],
                     listing.Entries.Select(e => e.Title));
    }

    // ── Тексты, сверенные с кодом ─────────────────────────────────────────────

    /// <summary>
    /// Подсказка пароля базы говорит главное: этим паролем человек входит без администратора и
    /// получает права по умолчанию. Про устройство хранения — одной фразой, без разбора того, где
    /// какой файл лежит: плашку читают на бегу.
    /// </summary>
    [Fact]
    public void The_database_password_says_whom_it_lets_in()
    {
        var entry = Only(Build([Sqlite(1, "Склад", hasPassword: true)]), SecretKind.DatabasePassword);

        Assert.Contains("без администратора", entry.What);
        Assert.Contains("права по умолчанию", entry.What);
    }

    /// <summary>
    /// Каждая подсказка называет место в ПРОГРАММЕ, где секрет настраивается целиком: человек
    /// пришел сюда, чтобы не рыскать по разделам, и уйти отсюда с пустыми руками не должен.
    /// Устройство хранения (какой файл, какое хранилище системы) в плашке не разбирается — на
    /// решение «что делать дальше» оно не влияет.
    /// </summary>
    [Fact]
    public void Every_tip_names_where_the_secret_is_set_up()
    {
        var listing = Build([Sqlite(1, "Склад", hasPassword: true), Postgres(2, "Заказы")],
                            [Connection(1, "Обмен", lastRun: Now)],
                            [Token(1, "Робот", dbId: 1, created: Now)],
                            new InventoryMaster(true, ["реестр"]));

        // Не Only: баз в описи две, и строк пароля базы тоже две — здесь проверяется текст рода,
        // а не то, сколько строк этого рода вышло.
        string Tip(SecretKind kind) => listing.Entries.First(e => e.Kind == kind).Guard;

        Assert.Contains("Профиль → Панель администратора", Tip(SecretKind.DatabasePassword));
        Assert.Contains("Настройки → Управление базой данных", Tip(SecretKind.PgPassword));
        Assert.Contains("Настройки → Интеграции → Соединения", Tip(SecretKind.Connection));
        Assert.Contains("Настройки → Интеграции → Доступ извне", Tip(SecretKind.RobotToken));
        Assert.Contains("Мастер-пароль для политики", Tip(SecretKind.PolicyMaster));

        // Ни устройства хранения политики, ни имен операционных систем: это не помогает решить,
        // что делать со строкой, а плашку удлиняет вдвое.
        foreach (var entry in listing.Entries)
            foreach (var noise in new[] { "Windows", "macOS", "Linux", "DPAPI", "policy.json",
                                          "HKLM", "/etc/smartbase", "app.db" })
                Assert.DoesNotContain(noise, SecretsInventory.Tip(entry));
    }

    [Fact]
    public void The_master_password_says_it_opens_the_secret_reveal_too()
    {
        var entry = Only(Build([Sqlite(1, "Склад")]), SecretKind.PolicyMaster);

        Assert.Contains("отправок наружу", entry.What);
        Assert.Contains("выдач на чтение", entry.What);
        Assert.Contains("меток", entry.What);
        Assert.Contains("показа секретов", entry.What);
        Assert.Contains("вне программы", entry.Guard);
    }

    /// <summary>
    /// Опись обязана сказать, ГДЕ менять пароль сервера, и назвать путь, который существует:
    /// «Управление базой данных» и шестеренка у базы. Прежний путь «Настройки → Базы данных →
    /// Изменить» в программе не существовал вовсе.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_server_password_says_where_it_is_changed(bool saved)
    {
        var entry = Only(Build([Postgres(1, "Заказы", serverPassword: saved)]), SecretKind.PgPassword);

        Assert.Contains("Управление базой данных", entry.Guard);
        Assert.Contains("шестеренка у базы", entry.Guard);
        Assert.DoesNotContain("«Изменить»", entry.Guard);
        Assert.False(entry.Revealable);
    }

    /// <summary>
    /// Снятие пароля при уже выключенном входе ничего не меняет для пользователей — и вопрос не
    /// пугает последствием, которого не будет.
    /// </summary>
    [Fact]
    public void Removing_a_password_does_not_promise_what_the_switch_already_did()
    {
        var open   = SecretsInventory.RemovePasswordQuestion("Склад", passwordAccessEnabled: true);
        var closed = SecretsInventory.RemovePasswordQuestion("Склад", passwordAccessEnabled: false);

        Assert.StartsWith("Убрать пароль базы «Склад»?", open);
        Assert.Contains("больше не смогут", open);

        Assert.StartsWith("Убрать пароль базы «Склад»?", closed);
        Assert.DoesNotContain("больше не смогут", closed);
        Assert.Contains("уже выключен", closed);
    }

    /// <summary>У мертвого секрета подсказка говорит, почему он красный.</summary>
    [Fact]
    public void A_dead_secret_explains_itself_in_the_tip()
    {
        var entry = Only(Build([Sqlite(1, "Склад")], [Connection(1, "Забытое", lastRun: Now.AddYears(-1))]),
                         SecretKind.Connection);

        Assert.Contains($"{SecretsInventory.StaleDays} дней", SecretsInventory.Tip(entry));
    }
}
