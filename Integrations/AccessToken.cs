using System.Globalization;
using System.Security.Cryptography;

namespace SmartBase.Integrations;

/// <summary>
/// Токен доступа — «кто стучится к нам» (docs/ROADMAP-API.md, 10.12).
///
/// Токен не самостоятельная система прав, а <b>пользователь-робот</b>: у каждого токена есть своя
/// строка в <c>db_users</c> целевой базы с обычной маской <c>PermAction</c>. Из этого следует все
/// остальное — в истории изменений видно «Обмен учетной системы создал запись», администратор
/// отзывает доступ роботу той же панелью, что и человеку, а недопущенная база остается
/// недоступной и по сети. Заводить параллельные права было бы вторым набором правил, который
/// однажды разойдется с первым.
///
/// <b>Самого токена в программе нет.</b> Хранится только проверочный хэш — тот же PBKDF2, что у
/// мастер-пароля политики (<see cref="MasterPassword"/>); своей криптографии в программе не
/// заводится. Показывается токен ОДИН раз, при создании: хранилище, из которого секрет можно
/// прочитать обратно, рано или поздно прочитают.
///
/// Текстовый вид — <c>sb_&lt;ключ&gt;_&lt;секрет&gt;</c>. Ключ строки впереди не украшение: по нему
/// находится одна строка и считается один хэш, иначе каждое обращение перебирало бы все токены
/// подряд, а PBKDF2 на то и медленный.
/// </summary>
/// <param name="Id">Ключ строки в <c>app.db</c>; он же первая часть текста токена.</param>
/// <param name="Name">Имя для человека: «Обмен учетной системы».</param>
/// <param name="DbId">База, в которую этому токену разрешено писать. Одна: токен выдается ПОД
/// базу, и роботу, заведенному для склада, нечего делать в базе конструкторской документации.</param>
/// <param name="Hash">Проверочный хэш секрета.</param>
/// <param name="RobotKey">Ключ пользователя-робота в <c>db_users</c> целевой базы.</param>
/// <param name="CreatedAt">Когда выдан (ISO-8601).</param>
/// <param name="UsedAt">Когда им пользовались в последний раз; пусто — ни разу.</param>
/// <param name="Enabled">Выключенный токен не пускают, но строку не удаляют: выключить обмен
/// нужно чаще, чем расстаться с ним навсегда.</param>
public sealed record AccessToken(
    int      Id,
    string   Name,
    int      DbId,
    string   Hash,
    string   RobotKey,
    string   CreatedAt,
    string   UsedAt,
    bool     Enabled)
{
    /// <summary>Начало текстового вида. Короткое и узнаваемое: по нему токен видно в чужом
    /// конфигурационном файле, и это не мелочь — случайно опубликованный секрет ищут поиском.</summary>
    public const string Prefix = "sb";

    /// <summary>Длина секрета в байтах. Шестнадцать — 128 бит: подбирать нечего, а строка
    /// остается такой, что ее можно продиктовать по телефону.</summary>
    public const int SecretBytes = 16;

    /// <summary>Сколько знаков в имени. Имя попадает в <c>db_users</c> и в историю изменений.</summary>
    public const int MaxNameLength = 60;

    /// <summary>Момент из строки хранения; <c>null</c> — пусто или испорчено правкой файла.</summary>
    public DateTimeOffset? Used => Moment(UsedAt);

    /// <inheritdoc cref="Used"/>
    public DateTimeOffset? Created => Moment(CreatedAt);

    private static DateTimeOffset? Moment(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                                DateTimeStyles.RoundtripKind, out var moment)
            ? moment
            : null;

    // ── Секрет ────────────────────────────────────────────────────────────────

    /// <summary>Новый секрет — шестнадцатеричной строкой из криптографического источника.</summary>
    public static string NewSecret() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>Проверочный хэш секрета.</summary>
    public static string HashOf(string secret) => MasterPassword.Hash(secret);

    /// <summary>Текст токена, который отдают отправителю. Собирается один раз и больше нигде.</summary>
    public static string Compose(int id, string secret) =>
        $"{Prefix}_{id.ToString(CultureInfo.InvariantCulture)}_{secret}";

    /// <summary>
    /// Разобрать текст токена на ключ строки и секрет. <c>false</c> — текст не похож на токен;
    /// отвечать на такое «неверный токен» правильно и достаточно: подсказывать, ЧТО именно не
    /// так, значит помогать подбирать.
    /// </summary>
    public static bool TryParse(string? text, out int id, out string secret)
    {
        id     = 0;
        secret = "";

        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Trim().Split('_');
        if (parts.Length != 3 || parts[0] != Prefix) return false;

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out id)
            || id <= 0)
            return false;

        if (parts[2].Length == 0) return false;

        secret = parts[2];
        return true;
    }

    /// <summary>Совпадает ли секрет с хэшем этой строки.</summary>
    public bool Matches(string? secret) => MasterPassword.Matches(Hash, secret);

    // ── Пользователь-робот ────────────────────────────────────────────────────

    /// <summary>
    /// Ключ робота в <c>db_users</c>. Вид тот же, что у человека («кто@где»), и машина названа
    /// намеренно: токены лежат в <c>app.db</c> одной машины, а база общая — два компьютера,
    /// выдавшие токен с одним именем, обязаны получить разных роботов.
    /// </summary>
    public static string KeyOf(int id, string machine) =>
        $"robot.{id.ToString(CultureInfo.InvariantCulture)}@{machine}".ToLowerInvariant();

    /// <summary>Приставка ключа робота — по ней строка <c>db_users</c> отличается от человека.</summary>
    private const string RobotPrefix = "robot.";

    /// <summary>
    /// Это строка робота, а не человека. Спрашивают служба прав (роль администратора роботу не
    /// назначается) и таблица пользователей (у робота своя подпись роли). Признак — вид ключа,
    /// а не отдельная колонка: ключ и так уникален, а колонка потребовала бы миграции ради
    /// того, что уже записано в самом ключе.
    /// </summary>
    public static bool IsRobotKey(string? key) =>
        key is not null && key.StartsWith(RobotPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Что не так с именем; пусто — годится. Имя увидит администратор базы в списке
    /// пользователей, и «» среди людей читается как сбой, а не как робот.
    /// </summary>
    public static string NameTrouble(string? name)
    {
        var trimmed = (name ?? "").Trim();

        if (trimmed.Length == 0)             return "У токена должно быть имя: оно попадет в историю изменений.";
        if (trimmed.Length > MaxNameLength)  return $"Имя длиннее {MaxNameLength} знаков.";

        return "";
    }
}
