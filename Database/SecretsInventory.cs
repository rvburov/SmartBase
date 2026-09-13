using System.Globalization;

namespace SmartBase.Database;

/// <summary>Род секрета — от него зависит и то, что о нем можно сказать, и то, что с ним можно
/// сделать.</summary>
public enum SecretKind
{
    /// <summary>Секрет соединения интеграции: токен или пароль чужой программы.</summary>
    Connection,

    /// <summary>Токен-робот: им чужая программа стучится к нам.</summary>
    RobotToken,

    /// <summary>Пароль базы данных — им человек допускает себя сам.</summary>
    DatabasePassword,

    /// <summary>Пароль подключения к серверу PostgreSQL.</summary>
    PgPassword,

    /// <summary>Мастер-пароль организации: он живет в файле политики, а не в программе.</summary>
    PolicyMaster,
}

/// <summary>
/// Строка описи секретов (docs/ROADMAP-API.md, шаг 16).
/// </summary>
/// <param name="Kind">Род секрета.</param>
/// <param name="Key">Ключ для действий: <c>«conn:5»</c>, <c>«token:3»</c>, <c>«db:2»</c>.
/// Собирается здесь, а не в экране: по нему экран находит, что именно менять.</param>
/// <param name="Title">Как назвать человеку: «Соединение „Учетная система“».</param>
/// <param name="What">Для чего он: одна фраза.</param>
/// <param name="Where">Где применяется: имя базы, число запросов.</param>
/// <param name="Created">Когда заведен; <c>null</c> — неизвестно.</param>
/// <param name="Used">Когда им пользовались в последний раз; <c>null</c> — ни разу или
/// программа этого не записывает.</param>
/// <param name="UseKnown">Записывает ли программа пользование этим секретом. Разница между
/// «не пользовались» и «мы не считаем» принципиальна: первое — повод отозвать, второе — нет,
/// и валить их в одно «—» значило бы подталкивать к отзыву работающего секрета.</param>
/// <param name="Revealable">Можно ли показать значение.</param>
/// <param name="Guard">Почему показать нельзя — словами, а не пустотой.</param>
public sealed record SecretEntry(SecretKind      Kind,
                                 string          Key,
                                 string          Title,
                                 string          What,
                                 string          Where,
                                 DateTimeOffset? Created,
                                 DateTimeOffset? Used,
                                 bool            UseKnown,
                                 bool            Revealable,
                                 string          Guard);

/// <summary>
/// Опись секретов машины — «что где заведено», а не просмотрщик паролей
/// (docs/ROADMAP-API.md, шаг 16).
///
/// <b>Зачем она вообще.</b> Секреты заводятся в пяти разных местах: соединение интеграции, токен
/// робота, пароль базы, пароль сервера PostgreSQL, мастер-пароль организации. Заводят их в разное
/// время и разные люди, а спрашивают потом одним вопросом — «что у нас тут заведено и что из
/// этого давно мертво». Опись отвечает на него одним списком.
///
/// <b>Главная польза — мертвые секреты.</b> Секрет, которым год не пользовались, это не
/// бездействующая настройка, а открытая дверь, о которой никто не думает. Поэтому в описи есть
/// столбец «когда пользовались» и отметка <see cref="Stale"/>.
///
/// <b>Чего опись не делает.</b> Она не показывает значения. Показать можно ровно там, где секрет
/// восстановим ПО ПОСТРОЕНИЮ — у соединения, потому что им подписывается каждый запрос, — и даже
/// там только под мастер-паролем и со строкой в журнале безопасности. От мастер-пароля и токенов
/// роботов хранится один отпечаток: это устройство хранения, а не ограничение экрана, и написано
/// об этом прямо (<see cref="SecretEntry.Guard"/>) — иначе отсутствие кнопки читается как
/// недоделка.
///
/// Сборка описи — чистая функция от уже прочитанных данных: она не ходит ни в базу, ни в
/// хранилище ОС. Так ее правила проверяются тестами, а не глазами на живом экране.
/// </summary>
public static class SecretsInventory
{
    /// <summary>
    /// С какого возраста секрет считается мертвым. Три месяца — срок, за который любой рабочий
    /// обмен успевает сработать хоть раз: и месячные отчеты, и квартальные выгрузки.
    /// </summary>
    public const int StaleDays = 90;

    /// <summary>
    /// Секрет, которым давно не пользовались. Секрет, о пользовании которым программа не знает
    /// (<see cref="SecretEntry.UseKnown"/> — ложь), мертвым НЕ считается: отозвать работающий
    /// пароль по догадке хуже, чем не отозвать заброшенный.
    /// </summary>
    public static bool Stale(SecretEntry entry, DateTimeOffset now) =>
        entry.UseKnown && (entry.Used is null || now - entry.Used.Value > TimeSpan.FromDays(StaleDays));

    /// <summary>Ключ строки описи для действий экрана.</summary>
    public static string KeyOf(SecretKind kind, int id) => kind switch
    {
        SecretKind.Connection       => $"conn:{id}",
        SecretKind.RobotToken       => $"token:{id}",
        SecretKind.DatabasePassword => $"db:{id}",
        SecretKind.PgPassword       => $"pg:{id}",
        _                           => "master",
    };

    /// <summary>Род и ключ обратно из строки <see cref="KeyOf"/>; ключ мастер-пароля — 0.</summary>
    public static (SecretKind Kind, int Id) Parse(string key)
    {
        var parts = key.Split(':', 2);
        var id    = parts.Length == 2 && int.TryParse(parts[1], NumberStyles.Integer,
                                                     CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

        return parts[0] switch
        {
            "conn"  => (SecretKind.Connection,       id),
            "token" => (SecretKind.RobotToken,       id),
            "db"    => (SecretKind.DatabasePassword, id),
            "pg"    => (SecretKind.PgPassword,       id),
            _       => (SecretKind.PolicyMaster,     0),
        };
    }

    // ── Строки описи ──────────────────────────────────────────────────────────

    /// <summary>
    /// Секрет соединения. Показать его можно: им подписывается каждый запрос, и программа
    /// расшифровывает его сама при каждой отправке — прятать от человека то, что она достает
    /// сама, было бы притворством.
    /// </summary>
    /// <param name="requests">Сколько запросов ходит через это соединение.</param>
    /// <param name="lastRun">Последний запуск любого из них — он же последнее пользование
    /// секретом.</param>
    public static SecretEntry Connection(int id, string name, string address,
                                         int requests, DateTimeOffset? lastRun,
                                         DateTimeOffset? created) =>
        new(SecretKind.Connection,
            KeyOf(SecretKind.Connection, id),
            $"Соединение «{name}»",
            "Пароль или токен чужой программы: им подписывается каждый запрос к ней.",
            requests switch
            {
                0 => $"{address} · запросов нет ни одного",
                1 => $"{address} · 1 запрос",
                _ => $"{address} · запросов: {requests}",
            },
            created, lastRun, UseKnown: true, Revealable: true, Guard: "");

    /// <summary>
    /// Токен-робот. Не показывается никогда: от него хранится один отпечаток, и показать его
    /// программа не может физически, а не «не хочет».
    /// </summary>
    public static SecretEntry RobotToken(int id, string name, string database,
                                         DateTimeOffset? created, DateTimeOffset? used,
                                         bool enabled) =>
        new(SecretKind.RobotToken,
            KeyOf(SecretKind.RobotToken, id),
            $"Токен «{name}»",
            "Им чужая программа стучится к нам — как пользователь-робот со своими правами.",
            (database.Length > 0 ? $"база «{database}»" : "база не найдена")
            + (enabled ? "" : " · выключен"),
            created, used, UseKnown: true, Revealable: false,
            Guard: "Хранится только отпечаток: токен показывается один раз, при выдаче. "
                 + "Забыли — выпустите заново.");

    /// <summary>
    /// Пароль базы. Им человек допускает себя сам, вместо того чтобы ждать администратора.
    /// Пользование им программа не записывает — и об этом сказано прямо.
    /// </summary>
    public static SecretEntry DatabasePassword(int dbId, string database) =>
        new(SecretKind.DatabasePassword,
            KeyOf(SecretKind.DatabasePassword, dbId),
            $"Пароль базы «{database}»",
            "Им новый человек открывает себе доступ к базе, не дожидаясь администратора.",
            $"база «{database}»",
            Created: null, Used: null, UseKnown: false, Revealable: false,
            Guard: "Хранится только отпечаток — тот же способ, каким хранят пароли везде.");

    /// <summary>
    /// Пароль подключения к серверу PostgreSQL. Лежит в хранилище ОС (Связка ключей, DPAPI), и
    /// показывать его отсюда незачем: подключение работает — значит пароль верен.
    /// </summary>
    public static SecretEntry PgPassword(int dbId, string database, string server) =>
        new(SecretKind.PgPassword,
            KeyOf(SecretKind.PgPassword, dbId),
            $"Пароль сервера для базы «{database}»",
            "Им программа подключается к серверу PostgreSQL.",
            server.Length > 0 ? server : "сервер не указан",
            Created: null, Used: null, UseKnown: false, Revealable: false,
            Guard: "Лежит в хранилище операционной системы и на другую машину не переезжает.");

    /// <summary>
    /// Мастер-пароль организации. Его в программе нет вовсе — есть проверочный отпечаток в файле
    /// политики, и заменить его можно только там же, где правят саму политику.
    /// </summary>
    public static SecretEntry PolicyMaster(bool defined, string source) =>
        new(SecretKind.PolicyMaster,
            "master",
            "Мастер-пароль организации",
            "Им подтверждают исходящие направления, выдачи на чтение и понижение меток.",
            defined
                ? (source.Length > 0 ? $"задан политикой: {source}" : "задан политикой")
                : "не задан — подтверждать направления нечем",
            Created: null, Used: null, UseKnown: false, Revealable: false,
            Guard: "Задается вне программы, в подписанном файле политики. Программа хранит "
                 + "только отпечаток и менять его не может — в этом смысл контура.");

    // ── Порядок ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Опись по порядку показа: сперва мертвые, потом остальные, внутри — по роду и имени.
    ///
    /// Мертвые наверх не ради красоты: именно за ними сюда и приходят, а список из тридцати
    /// строк, где две нужные лежат где-то в середине, отвечает на вопрос хуже, чем не отвечает
    /// вовсе.
    /// </summary>
    public static IReadOnlyList<SecretEntry> Ordered(IEnumerable<SecretEntry> entries,
                                                     DateTimeOffset now) =>
        [.. entries.OrderByDescending(e => Stale(e, now))
                   .ThenBy(e => e.Kind)
                   .ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase)];
}
