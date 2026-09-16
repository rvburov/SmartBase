namespace SmartBase.Database;

/// <summary>Установлен ли пароль в этом месте.</summary>
public enum SecretState
{
    /// <summary>Пароль установлен.</summary>
    Set,

    /// <summary>Место есть, пароля в нем нет.</summary>
    NotSet,
}

/// <summary>Род секрета — от него зависит и то, что о нем можно сказать, и то, что с ним можно
/// сделать.</summary>
public enum SecretKind
{
    /// <summary>Секрет соединения интеграции: токен или пароль сторонней программы.</summary>
    Connection,

    /// <summary>Токен-робот: им сторонняя программа стучится к нам.</summary>
    RobotToken,

    /// <summary>Пароль базы данных — им человек допускает себя сам.</summary>
    DatabasePassword,

    /// <summary>Пароль подключения к серверу PostgreSQL.</summary>
    PgPassword,

    /// <summary>Мастер-пароль организации: он живет в политике, а не в программе.</summary>
    PolicyMaster,
}

/// <summary>
/// Цвет строки состояния — категория, а не кисть: опись живет рядом с данными и о палитре не
/// знает, а экран переводит категорию в токен цвета.
/// </summary>
public enum SecretTone
{
    /// <summary>Серым: пароля нет либо он есть, но сейчас никого не пускает (выключен вход по
    /// паролю, выключен токен). Не беда и не достижение — красить это тревожным цветом значило бы
    /// звать чинить исправное.</summary>
    Muted,

    /// <summary>Зеленым: установлен и в деле.</summary>
    Good,

    /// <summary>Красным: давно не нужен — открытая дверь, о которой никто не думает.</summary>
    Alarm,
}

/// <summary>
/// Строка менеджера паролей (docs/ROADMAP-API.md, шаг 16).
/// </summary>
/// <param name="Kind">Род секрета.</param>
/// <param name="Id">Ключ того, к чему секрет относится: соединения, токена или базы (у пароля базы
/// и пароля ее сервера он общий — различает их род). У мастер-пароля — 0.</param>
/// <param name="Title">Название: «Пароль базы SQLite «Заказы»».</param>
/// <param name="Status">Состояние словами — вторая строка.</param>
/// <param name="Tone">Цвет второй строки.</param>
/// <param name="Setup">Где секрет настраивается — путь по разделам программы, третья строка.
/// Стоит в самой строке, а не в подсказке: подсказку надо вызвать и дождаться, а «куда идти
/// менять» нужно сразу — ради этого вопроса в опись и заходят.</param>
/// <param name="Where">Путь, адрес или сервер — четвертая строка.</param>
/// <param name="State">Установлен ли пароль в этом месте.</param>
/// <param name="Since">Момент, от которого считается простой: последнее пользование, а у ни разу
/// не использованного токена — его выдача.</param>
/// <param name="Watched">Может ли секрет «умереть»: программа отмечает пользование им, и он
/// сейчас открывает дверь. Пароль базы, пароль сервера, мастер-пароль и выключенный токен —
/// нет.</param>
/// <param name="Revealable">Можно ли показать значение.</param>
public sealed record SecretEntry(SecretKind      Kind,
                                 int             Id,
                                 string          Title,
                                 string          Status,
                                 SecretTone      Tone,
                                 string          Setup,
                                 string          Where,
                                 SecretState     State,
                                 DateTimeOffset? Since,
                                 bool            Watched,
                                 bool            Revealable);

/// <summary>Открытая база глазами описи — уже прочитанные сведения, без походов в базу.</summary>
/// <param name="Id">Ключ базы в реестре.</param>
/// <param name="Name">Имя базы.</param>
/// <param name="IsAdmin">Текущий пользователь — ее администратор.</param>
/// <param name="IsPostgres">База живет на сервере PostgreSQL.</param>
/// <param name="Path">Путь файла SQLite-базы.</param>
/// <param name="PgHost">Сервер PostgreSQL.</param>
/// <param name="PgPort">Порт сервера.</param>
/// <param name="PgDatabase">Имя базы на сервере.</param>
/// <param name="HasServerPassword">Сохранен ли пароль подключения к серверу.</param>
/// <param name="HasPassword">Задан ли пароль базы.</param>
/// <param name="PasswordAccessEnabled">Администратор не выключал вход по паролю.</param>
/// <param name="LastPasswordUnlock">Последний вход по паролю; <c>null</c> — не отмечен.</param>
public sealed record InventoryDatabase(int             Id,
                                       string          Name,
                                       bool            IsAdmin,
                                       bool            IsPostgres,
                                       string          Path,
                                       string          PgHost,
                                       int             PgPort,
                                       string          PgDatabase,
                                       bool            HasServerPassword,
                                       bool            HasPassword,
                                       bool            PasswordAccessEnabled,
                                       DateTimeOffset? LastPasswordUnlock);

/// <summary>Соединение интеграции: есть ли секрет и когда им последний раз подписали запрос.</summary>
/// <param name="LastRun">Последний запуск любого запроса этого соединения — он же последнее
/// пользование секретом: секрет подписывает каждый из них, и отдельной отметки ему не надо.</param>
public sealed record InventoryConnection(int             Id,
                                         string          Name,
                                         string          Address,
                                         bool            HasSecret,
                                         DateTimeOffset? LastRun);

/// <summary>Токен входящего канала.</summary>
/// <param name="DbId">База, в которую токену разрешено писать.</param>
public sealed record InventoryToken(int             Id,
                                    string          Name,
                                    int             DbId,
                                    bool            Enabled,
                                    DateTimeOffset? Created,
                                    DateTimeOffset? Used);

/// <summary>Мастер-пароль организации.</summary>
/// <param name="Defined">Задан ли он хотя бы одним источником политики.</param>
/// <param name="Sources">Источники, где он задан: они стоят третьей строкой, чтобы администратор
/// знал, откуда пришло действующее правило.</param>
public sealed record InventoryMaster(bool                  Defined,
                                     IReadOnlyList<string> Sources);

/// <summary>Все, из чего складывается опись.</summary>
/// <param name="Databases">ОТКРЫТЫЕ базы: у закрытой, недоступной или сжимаемой читать нечего,
/// и в описи ее нет вовсе.</param>
/// <param name="IntegrationsAllowed">Разрешена ли человеку настройка обмена — право
/// <see cref="SmartBase.Core.Models.PermAction.IntegrationsSetup"/> во ВСЕХ допущенных базах, то
/// самое, которым проверяются кнопки этих строк и экраны раздела «Интеграции».</param>
public sealed record InventoryFacts(IReadOnlyList<InventoryDatabase>   Databases,
                                    IReadOnlyList<InventoryConnection> Connections,
                                    IReadOnlyList<InventoryToken>      Tokens,
                                    InventoryMaster                    Master,
                                    bool                               IntegrationsAllowed);

/// <summary>Итог описи.</summary>
/// <param name="Admin">Текущий пользователь — администратор хотя бы одной открытой базы. Ложь —
/// экран показывает одну строку-заглушку (<see cref="SecretsInventory.NoAccessTitle"/>).</param>
/// <param name="Integrations">В описи есть то, что заводит меню «Добавить пароль»: соединения,
/// токены, мастер-пароль. Ложь — меню прячут целиком: все три его пункта уперлись бы в отказ.</param>
/// <param name="Entries">Строки по порядку показа.</param>
public sealed record InventoryListing(bool                       Admin,
                                      bool                       Integrations,
                                      IReadOnlyList<SecretEntry> Entries);

/// <summary>
/// Опись секретов машины — «что где заведено», а не просмотрщик паролей
/// (docs/ROADMAP-API.md, шаг 16).
///
/// <b>Зачем она вообще.</b> Секреты заводятся в пяти разных местах: соединение интеграции, токен
/// робота, пароль базы, пароль сервера PostgreSQL, мастер-пароль организации. Заводят их в разное
/// время и разные люди, а спрашивают потом одним вопросом — «что у нас тут заведено и что из
/// этого давно мертво». Опись отвечает на него одним списком.
///
/// <b>Кто что видит.</b> Пароли видит и меняет администратор, и границ у описи две. Первая по
/// базе: пароль базы и пароль ее сервера — только ее администратору. Вторая по праву на обмен:
/// соединения, токены роботов и мастер-пароль показываются, только если человеку разрешена
/// настройка интеграций (<see cref="InventoryFacts.IntegrationsAllowed"/>) — тем же правом
/// проверяются кнопки этих строк, и показывать то, чего трогать нельзя, значит дразнить, а не
/// защищать. Токену вдобавок нужно админство его базы. Закрытых баз в описи нет: пароль базы лежит
/// внутри ее файла, у закрытой читать его негде, а строка «неизвестно» ничего не дает тому, кто
/// пришел что-то поменять. Человек без админства видит одну строку с объяснением — пустой экран
/// читался бы как поломка.
///
/// <b>В списке все МЕСТА, а не только заполненные.</b> Вопрос «где я поставил пароль, а где нет»
/// — ровно тот, ради которого опись и заводят: место без пароля стоит в списке наравне с
/// остальными и отмечено словами (<see cref="SecretState.NotSet"/>).
///
/// <b>Главная польза — мертвые секреты.</b> Секрет, которым три месяца не пользовались, это не
/// бездействующая настройка, а открытая дверь (<see cref="Stale"/>). Такие строки наверху.
///
/// <b>Чего опись не делает.</b> Она не показывает значения. Показать можно ровно там, где секрет
/// восстановим ПО ПОСТРОЕНИЮ, — у соединения, потому что им подписывается каждый запрос, — и даже
/// там только под мастер-паролем и со строкой в журнале безопасности. От мастер-пароля, токенов и
/// паролей баз хранится один отпечаток: это устройство хранения, а не ограничение экрана, и
/// написано об этом прямо (<see cref="SecretEntry.Guard"/>).
///
/// Сборка описи — чистая функция от уже прочитанных данных (<see cref="Build"/>): она не ходит ни
/// в базу, ни в хранилище ОС. Так правила проверяются тестами, а не глазами на живом экране.
/// </summary>
public static class SecretsInventory
{
    /// <summary>
    /// С какого возраста секрет считается мертвым. Три месяца — срок, за который любой рабочий
    /// обмен успевает сработать хоть раз: и месячные отчеты, и квартальные выгрузки.
    /// </summary>
    public const int StaleDays = 90;

    /// <summary>Заголовок строки-заглушки для того, кто не администратор ни одной открытой базы.</summary>
    public const string NoAccessTitle = "Нет прав администратора";

    /// <summary>
    /// Пояснение заглушки. Граница названа целиком: человек, который администрирует соседнюю
    /// базу у коллеги, должен понять, почему здесь пусто, а не искать настройку видимости.
    /// </summary>
    public const string NoAccessText =
        "Пароли видит и меняет только администратор. Пароль базы данных, пароль ее сервера "
        + "PostgreSQL и токены роботов этой базы доступны администратору самой базы. Соединения "
        + "со сторонними программами и мастер-пароль организации доступны тому, кому разрешена "
        + "настройка интеграций. Вы не администратор ни одной из открытых баз данных.";

    // ── Опись ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Собрать опись: кто что видит, как называется, в каком состоянии и где лежит, — по порядку
    /// показа.
    /// </summary>
    public static InventoryListing Build(InventoryFacts facts, DateTimeOffset now)
    {
        var admins = facts.Databases.Where(d => d.IsAdmin).ToList();

        if (admins.Count == 0) return new InventoryListing(false, false, []);

        var entries = new List<SecretEntry>();

        foreach (var database in admins)
        {
            entries.Add(DatabasePassword(database));

            if (database.IsPostgres) entries.Add(ServerPassword(database));
        }

        // Соединения, токены и мастер-пароль общие на машину, и порог у них общий: право
        // «Интеграции: настройка» во всех допущенных базах. Тем же правом проверяются кнопки этих
        // строк и весь раздел «Интеграции» — одно правило вместо двух. Разъедься они, и человек
        // видел бы ровно то, чего ему делать нельзя, а администратор соседней базы не мог бы
        // закрыть ему обмен целиком: открыл бы ту базу, где право осталось.
        if (facts.IntegrationsAllowed)
        {
            foreach (var connection in facts.Connections)
                entries.Add(Connection(connection, now));

            // Токен — только базы, которую человек администрирует: права роботу выдает ее
            // администратор, и чужой токен ему здесь нечем ни настроить, ни отозвать осмысленно.
            foreach (var token in facts.Tokens)
                if (admins.FirstOrDefault(d => d.Id == token.DbId) is { } owner)
                    entries.Add(RobotToken(token, owner.Name, now));

            entries.Add(Master(facts.Master));
        }

        return new InventoryListing(true, facts.IntegrationsAllowed, Ordered(entries, now));
    }

    /// <summary>
    /// Секрет, которым давно не пользовались. Секрет, который мертвым быть не может
    /// (<see cref="SecretEntry.Watched"/> — ложь), не считается им никогда: отозвать работающий
    /// пароль по догадке хуже, чем не отозвать заброшенный.
    /// </summary>
    public static bool Stale(SecretEntry entry, DateTimeOffset now) =>
        entry.State == SecretState.Set
        && entry.Watched
        && (entry.Since is null || now - entry.Since.Value > TimeSpan.FromDays(StaleDays));

    /// <summary>
    /// Вопрос перед снятием пароля базы — один на менеджер паролей и панель администратора.
    /// Обещать «больше не смогут открыть доступ сами» можно, только пока вход по паролю
    /// включен: при выключенном пароль и так никого не пускает, и такое предупреждение пугало бы
    /// последствием, которого не будет.
    /// </summary>
    public static string RemovePasswordQuestion(string database, bool passwordAccessEnabled) =>
        $"Убрать пароль базы «{database}»?\n\n"
        + (passwordAccessEnabled
            ? "Новые пользователи больше не смогут открыть себе доступ сами — их будет допускать "
              + "администратор."
            : "Вход по паролю у этой базы уже выключен, поэтому для пользователей ничего не "
              + "изменится: их и сейчас допускает администратор. Включить вход по паролю снова "
              + "можно будет, только задав новый пароль.");

    /// <summary>
    /// Цвет состояния — одно правило на всю опись, и живет оно здесь, а не в каждой строке: пять
    /// копий правила разошлись бы при первой же правке.
    ///
    /// Зеленый значит «пароль тут есть», а не «пускает прямо сейчас». Выключенный вход по паролю
    /// и выключенный токен остаются зелеными: пароль задан, никуда не делся и снова заработает,
    /// как только тумблер вернут, — серым он звал бы чинить исправное. Что он сейчас не пускает,
    /// сказано словами в самом статусе.
    /// </summary>
    public static SecretTone ToneOf(SecretState state, bool stale) =>
        stale                      ? SecretTone.Alarm
        : state == SecretState.Set ? SecretTone.Good
                                   : SecretTone.Muted;

    /// <summary>Сервер базы PostgreSQL одной строкой: «хост:порт/база».</summary>
    public static string ServerOf(string host, int port, string database) =>
        host.Length == 0 ? "сервер не указан" : $"{host}:{port}/{database}";

    // ── Строки описи ──────────────────────────────────────────────────────────

    /// <summary>
    /// Пароль базы. Им человек допускает себя сам, вместо того чтобы ждать администратора.
    /// «Давно не нужным» не называется никогда: вход по паролю бывает раз в год (новичок пришел),
    /// и снимать пароль по такой отметке значило бы ломать прием людей.
    /// </summary>
    private static SecretEntry DatabasePassword(InventoryDatabase database)
    {
        var status = !database.HasPassword
            ? "не установлен"
            : !database.PasswordAccessEnabled
                ? "установлен · вход по паролю выключен"
                : database.LastPasswordUnlock is { } at
                    ? $"установлен · последний вход по паролю {at.ToLocalTime():dd.MM.yyyy}"
                    : "установлен";

        var state = database.HasPassword ? SecretState.Set : SecretState.NotSet;

        return new SecretEntry(
            SecretKind.DatabasePassword,
            database.Id,
            database.IsPostgres
                ? $"Пароль базы PostgreSQL «{database.Name}»"
                : $"Пароль базы SQLite «{database.Name}»",
            status,
            ToneOf(state, stale: false),
            "Настройка: «Профиль → Панель администратора»",
            database.IsPostgres
                ? ServerOf(database.PgHost, database.PgPort, database.PgDatabase)
                : database.Path,
            state,
            Since: null, Watched: false, Revealable: false);
    }

    /// <summary>
    /// Пароль подключения к серверу PostgreSQL. Хранится обратимо — программа достает его при
    /// каждом подключении, — и потому сказано, где именно и что из этого следует.
    /// </summary>
    private static SecretEntry ServerPassword(InventoryDatabase database) =>
        new(SecretKind.PgPassword,
            database.Id,
            $"Пароль сервера PostgreSQL для базы «{database.Name}»",
            database.HasServerPassword ? "установлен" : "не установлен",
            ToneOf(database.HasServerPassword ? SecretState.Set : SecretState.NotSet, stale: false),
            "Настройка: «Настройки → Управление базой данных», шестеренка у базы",
            ServerOf(database.PgHost, database.PgPort, database.PgDatabase),
            database.HasServerPassword ? SecretState.Set : SecretState.NotSet,
            Since: null, Watched: false, Revealable: false);

    /// <summary>
    /// Секрет соединения. Показать его можно: им подписывается каждый запрос, и программа
    /// расшифровывает его сама при каждой отправке — прятать от человека то, что она достает
    /// сама, было бы притворством.
    /// </summary>
    private static SecretEntry Connection(InventoryConnection connection, DateTimeOffset now)
    {
        var entry = new SecretEntry(
            SecretKind.Connection,
            connection.Id,
            $"Соединение «{connection.Name}»",
            "", SecretTone.Muted,
            "Настройка: «Настройки → Интеграции → Соединения»",
            connection.Address.Length > 0 ? connection.Address : "адрес не указан",
            connection.HasSecret ? SecretState.Set : SecretState.NotSet,
            connection.LastRun,
            Watched:    connection.HasSecret,
            Revealable: connection.HasSecret);

        if (!connection.HasSecret)
            return entry with { Status = "не установлен" };

        var stale = Stale(entry, now);

        return entry with
        {
            Status = stale ? "давно не нужен" : "установлен",
            Tone   = ToneOf(SecretState.Set, stale),
        };
    }

    /// <summary>
    /// Токен-робот. Не показывается никогда: от него хранится один отпечаток. Выключенный токен
    /// дверь не открывает, и «давно не нужным» не считается — его выключили нарочно.
    /// </summary>
    private static SecretEntry RobotToken(InventoryToken token, string database, DateTimeOffset now)
    {
        var entry = new SecretEntry(
            SecretKind.RobotToken,
            token.Id,
            $"Токен «{token.Name}»",
            "", SecretTone.Muted,
            "Выдача и отзыв: «Настройки → Интеграции → Доступ извне»",
            $"база «{database}»",
            SecretState.Set,
            // Ни разу не использованный токен отсчитывает простой от выдачи: выданный вчера еще
            // не мертв, а выданный полгода назад и ни разу не пришедший — да.
            token.Used ?? token.Created,
            Watched:    token.Enabled,
            Revealable: false);

        var stale = Stale(entry, now);

        return entry with
        {
            Status = !token.Enabled ? "выключен" : stale ? "давно не нужен" : "установлен",
            Tone   = ToneOf(SecretState.Set, stale),
        };
    }

    /// <summary>
    /// Мастер-пароль организации. Его в программе нет вовсе — есть проверочный хэш в политике, и
    /// заменить его можно только там же, где правят саму политику.
    /// </summary>
    private static SecretEntry Master(InventoryMaster master) =>
        new(SecretKind.PolicyMaster,
            0,
            "Мастер-пароль организации",
            master.Defined ? "задан" : "не задан",
            ToneOf(master.Defined ? SecretState.Set : SecretState.NotSet, stale: false),
            // Единственная строка описи, которую в программе не настраивают вовсе: путь назван
            // тем, что есть, — политикой. Строку для нее считает окно «Мастер-пароль».
            "Настройка: политика организации, вне программы",
            master.Defined && master.Sources.Count > 0
                ? string.Join(", ", master.Sources)
                : "политика организации",
            master.Defined ? SecretState.Set : SecretState.NotSet,
            Since: null, Watched: false, Revealable: false);

    // ── Порядок ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Опись по порядку показа: сперва мертвые, потом установленные, внизу места без пароля;
    /// внутри — по роду и имени.
    ///
    /// Мертвые наверх не ради красоты: именно за ними сюда и приходят, а список из тридцати
    /// строк, где две нужные лежат где-то в середине, отвечает на вопрос хуже, чем не отвечает
    /// вовсе. Пустые места — вниз: они не требуют действия, но и пропасть не должны, иначе
    /// вопрос «где я пароль не поставил» останется без ответа.
    /// </summary>
    public static IReadOnlyList<SecretEntry> Ordered(IEnumerable<SecretEntry> entries,
                                                     DateTimeOffset now) =>
        [.. entries.OrderByDescending(e => Stale(e, now))
                   .ThenBy(e => Rank(e.State))
                   .ThenBy(e => e.Kind)
                   .ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>
    /// Место в списке по состоянию. Порядок написан числами, а не взят из порядка значений
    /// перечисления: дописать туда состояние однажды придется, и сортировка не должна от этого
    /// молча переехать.
    /// </summary>
    private static int Rank(SecretState state) => state switch
    {
        SecretState.Set => 0,
        _               => 1,
    };
}
