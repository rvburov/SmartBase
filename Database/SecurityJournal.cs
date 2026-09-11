using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace SmartBase.Database;

/// <summary>Что за событие записано в журнал безопасности.</summary>
public enum SecurityEvent
{
    /// <summary>Попытка отправить данные наружу — разрешенная или заблокированная.</summary>
    Send = 0,

    /// <summary>Направление подтверждено мастер-паролем.</summary>
    Confirm,

    /// <summary>Подтверждение снято: направление правили, вышел срок, оно простаивало.</summary>
    Revoke,

    /// <summary>Понижена метка конфиденциальности поля или коллекции.</summary>
    Downgrade,

    /// <summary>Человек выгрузил данные руками. Функция рабочая, убирать ее нельзя, но след нужен.</summary>
    Export,
}

/// <summary>
/// Строка журнала безопасности — та, по которой проверяющий восстанавливает, что и куда уходило.
/// </summary>
/// <param name="Id">Ключ строки; 0 — строка еще не записана.</param>
/// <param name="At">Когда (ISO-8601, UTC).</param>
/// <param name="Event">Что произошло.</param>
/// <param name="Allowed">Разрешено ли. У блокировки причина лежит в <paramref name="Explanation"/>.</param>
/// <param name="Who">Кто — пользователь программы, как его знает база.</param>
/// <param name="Scenario">Имя сценария; пусто — человек нажал кнопку сам.</param>
/// <param name="Address">Куда — БЕЗ секрета, замаскированным.</param>
/// <param name="Fields">Какие поля ушли, через запятую. Значений полей здесь нет и не будет:
/// журнал выгружают проверяющему, и данные в нем — вторая утечка следом за первой.</param>
/// <param name="Rows">Сколько записей.</param>
/// <param name="Bytes">Сколько байт.</param>
/// <param name="BodyHash">SHA-256 отправленного тела. По нему сверяют «то же самое ушло или
/// другое», не храня самого тела.</param>
/// <param name="Explanation">Словами: причина отказа или пояснение к разрешению.</param>
/// <param name="DbId">Ключ базы, которую задело событие; 0 — ни одной.</param>
/// <param name="Confirmed">Кто подтвердил направление. Пусто — подтверждения не было.</param>
/// <param name="Prev">Хэш предыдущей строки; у первой пусто.</param>
/// <param name="Hash">Хэш этой строки вместе с <paramref name="Prev"/>.</param>
public sealed record SecurityEntry(
    long          Id,
    string        At,
    SecurityEvent Event,
    bool          Allowed,
    string        Who,
    string        Scenario,
    string        Address,
    string        Fields,
    int           Rows,
    long          Bytes,
    string        BodyHash,
    string        Explanation,
    int           DbId,
    string        Confirmed,
    string        Prev = "",
    string        Hash = "")
{
    /// <summary>
    /// Новая строка «сейчас». Момент передается явно — тест не должен зависеть от часов, а
    /// журнал безопасности проверяется тестами построчно.
    /// </summary>
    public static SecurityEntry New(SecurityEvent kind, bool allowed, string who, string scenario,
                                    string address, string fields, int rows, long bytes,
                                    string bodyHash, string explanation, int dbId,
                                    DateTimeOffset at, string confirmed = "") =>
        new(0, at.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            kind, allowed, who, scenario, address, fields, rows, bytes, bodyHash, explanation,
            dbId, confirmed);

    /// <summary>Момент записи; <c>null</c> — строка испорчена правкой файла руками.</summary>
    public DateTimeOffset? Moment =>
        DateTimeOffset.TryParse(At, CultureInfo.InvariantCulture,
                                DateTimeStyles.RoundtripKind, out var moment)
            ? moment
            : null;

    /// <summary>
    /// Знаки-разделители из ASCII: <c>0x1F</c> между полями строки, <c>0x1E</c> между предыдущим
    /// хэшем и содержимым. Записаны КОДОМ, а не самим знаком: невидимый знак в исходнике не
    /// переживет ни первой правки, ни смены кодировки файла, а вместе с ним разойдется и вся
    /// цепочка.
    ///
    /// Взять запятую значило бы позволить перенести знак из поля в поле, не меняя хэша: «а,б» и
    /// «а» + «б» склеились бы в одно и то же, и подмена прошла бы проверку.
    /// </summary>
    private const char Separator = (char)0x1F;

    /// <inheritdoc cref="Separator"/>
    private const char Chain = (char)0x1E;

    /// <summary>Содержимое строки одной канонической строкой — то, что идет в хэш.</summary>
    public string Canonical() => string.Join(Separator,
        At, ((int)Event).ToString(CultureInfo.InvariantCulture), Allowed ? "1" : "0",
        Who, Scenario, Address, Fields,
        Rows.ToString(CultureInfo.InvariantCulture),
        Bytes.ToString(CultureInfo.InvariantCulture),
        BodyHash, Explanation,
        DbId.ToString(CultureInfo.InvariantCulture), Confirmed);

    /// <summary>Хэш строки в цепочке: предыдущий хэш плюс содержимое этой.</summary>
    public string ChainHash(string previous) =>
        SecurityJournal.Sha256(previous + Chain + Canonical());
}

/// <summary>
/// Журнал безопасности — отдельный от рабочего и от «Диагностики» (docs/ROADMAP-API.md, 10.10).
///
/// Разница с журналом запусков не в колонках, а в назначении. Рабочий журнал читает тот, кто
/// налаживает обмен: его чистят, он ротируется, ошибка записи в него ничего не останавливает.
/// Этот читает ПРОВЕРЯЮЩИЙ, и отсюда три правила, которых нет больше нигде.
///
/// <list type="number">
///   <item><b>Только добавление и цепочка хэшей.</b> Каждая строка включает хэш предыдущей,
///   поэтому вырезать или подправить строку задним числом незаметно не выйдет: проверка
///   (<see cref="Verify"/>) назовет первую разошедшуюся. Цепочка не защищает от удаления файла
///   целиком — от этого спасает только копия в папке аудита организации (10.6), и делать вид,
///   что хэши закрывают и это, нельзя.</item>
///   <item><b>Пользователь его не чистит.</b> Ни кнопки, ни метода: журнал, который чистит тот,
///   за кем он ведется, не журнал. Ротация придет вместе с правилами политики.</item>
///   <item><b>Не записалось — не отправляем.</b> Это правило живет в шлюзе, но начинается здесь:
///   <see cref="Write"/> честно возвращает 0, когда строка не легла, и НЕ глушит сбой молча, как
///   рабочий журнал. Отправка, о которой не осталось следа, — ровно то, чего требование службы
///   безопасности не допускает.</item>
/// </list>
///
/// <b>Ни секретов, ни данных.</b> Адрес пишется замаскированным, поля — только именами, тело —
/// одним SHA-256. Журнал выгружают проверяющему целиком, и значение поля в нем стало бы второй
/// утечкой следом за первой.
///
/// Файл <c>security.db</c> лежит в папке данных приложения, рядом с <c>app.db</c> и
/// <c>integrations.db</c>, и НЕ внутри файла базы: файл базы ездит по сетевой папке, а журнал
/// безопасности локален и общий на все базы — один запуск задевает несколько, и ключ базы пишется
/// полем строки.
/// </summary>
public sealed class SecurityJournal
{
    /// <summary>Имя файла журнала в папке данных приложения.</summary>
    public const string FileName = "security.db";

    private readonly string _path;
    private readonly object _lock = new();

    public SecurityJournal(string path)
    {
        _path = path;
        Ensure();
    }

    /// <summary>Путь к файлу — для экрана «где это лежит» и для выгрузки проверяющему.</summary>
    public string Path => _path;

    // ── Схема ─────────────────────────────────────────────────────────────────

    private void Ensure()
    {
        using var connection = Open();
        using var command    = connection.CreateCommand();

        command.CommandText = """
            CREATE TABLE IF NOT EXISTS events (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                At          TEXT    NOT NULL,
                Event       INTEGER NOT NULL,
                Allowed     INTEGER NOT NULL,
                Who         TEXT    NOT NULL,
                Scenario    TEXT    NOT NULL,
                Address     TEXT    NOT NULL,
                Fields      TEXT    NOT NULL,
                Rows        INTEGER NOT NULL,
                Bytes       INTEGER NOT NULL,
                BodyHash    TEXT    NOT NULL,
                Explanation TEXT    NOT NULL,
                DbId        INTEGER NOT NULL,
                Confirmed   TEXT    NOT NULL,
                Prev        TEXT    NOT NULL,
                Hash        TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS events_at ON events (At);
            """;

        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    // ── Запись ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Записать строку, продолжив цепочку. Возвращает ключ строки; <b>0 — записать не вышло</b>, и
    /// это не мелочь: отправку, о которой не осталось следа, вызывающий обязан отменить.
    ///
    /// Чтение хвоста и вставка идут под одним замком и одной транзакцией: два потока, взявшие
    /// один и тот же «предыдущий» хэш, построили бы вилку вместо цепочки, и проверка сочла бы
    /// испорченной честную строку.
    /// </summary>
    public long Write(SecurityEntry entry)
    {
        try
        {
            lock (_lock)
            {
                using var connection  = Open();
                using var transaction = connection.BeginTransaction();

                var previous = LastHash(connection, transaction);
                var hash     = entry.ChainHash(previous);

                using var command = connection.CreateCommand();
                command.Transaction = transaction;

                command.CommandText = """
                    INSERT INTO events (At, Event, Allowed, Who, Scenario, Address, Fields,
                                        Rows, Bytes, BodyHash, Explanation, DbId, Confirmed,
                                        Prev, Hash)
                    VALUES ($at, $event, $allowed, $who, $scenario, $address, $fields,
                            $rows, $bytes, $bodyHash, $explanation, $db, $confirmed,
                            $prev, $hash);
                    SELECT last_insert_rowid();
                    """;

                command.Parameters.AddWithValue("$at",          entry.At);
                command.Parameters.AddWithValue("$event",       (int)entry.Event);
                command.Parameters.AddWithValue("$allowed",     entry.Allowed ? 1 : 0);
                command.Parameters.AddWithValue("$who",         entry.Who ?? "");
                command.Parameters.AddWithValue("$scenario",    entry.Scenario ?? "");
                command.Parameters.AddWithValue("$address",     entry.Address ?? "");
                command.Parameters.AddWithValue("$fields",      entry.Fields ?? "");
                command.Parameters.AddWithValue("$rows",        entry.Rows);
                command.Parameters.AddWithValue("$bytes",       entry.Bytes);
                command.Parameters.AddWithValue("$bodyHash",    entry.BodyHash ?? "");
                command.Parameters.AddWithValue("$explanation", entry.Explanation ?? "");
                command.Parameters.AddWithValue("$db",          entry.DbId);
                command.Parameters.AddWithValue("$confirmed",   entry.Confirmed ?? "");
                command.Parameters.AddWithValue("$prev",        previous);
                command.Parameters.AddWithValue("$hash",        hash);

                var id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);

                transaction.Commit();
                return id;
            }
        }
        catch (SqliteException failure)
        {
            // Молчать нельзя: строка журнала безопасности не легла, и тот, кто собирался
            // отправлять, обязан узнать об этом по возвращенному нулю. «Диагностика» —
            // подстраховка для того, кто разбирается потом.
            Serilog.Log.Error("Журнал безопасности: строка НЕ записана — {Reason:l}", failure.Message);
            return 0;
        }
    }

    private static string LastHash(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Hash FROM events ORDER BY Id DESC LIMIT 1;";

        return command.ExecuteScalar() as string ?? "";
    }

    // ── Чтение ────────────────────────────────────────────────────────────────

    /// <summary>Последние строки, свежие сверху.</summary>
    public IReadOnlyList<SecurityEntry> Recent(int take = 200)
    {
        try
        {
            lock (_lock)
            {
                using var connection = Open();
                using var command    = connection.CreateCommand();

                command.CommandText = Select + " ORDER BY Id DESC LIMIT $take;";
                command.Parameters.AddWithValue("$take", Math.Max(1, take));

                return Read(command);
            }
        }
        catch (SqliteException failure)
        {
            Serilog.Log.Warning("Журнал безопасности: не прочитался — {Reason:l}", failure.Message);
            return [];
        }
    }

    /// <summary>Сколько строк в журнале.</summary>
    public long Count()
    {
        try
        {
            lock (_lock)
            {
                using var connection = Open();
                using var command    = connection.CreateCommand();

                command.CommandText = "SELECT COUNT(*) FROM events;";
                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    private const string Select =
        "SELECT Id, At, Event, Allowed, Who, Scenario, Address, Fields, Rows, Bytes, BodyHash, " +
        "Explanation, DbId, Confirmed, Prev, Hash FROM events";

    private static List<SecurityEntry> Read(SqliteCommand command)
    {
        var rows = new List<SecurityEntry>();

        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add(new SecurityEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                (SecurityEvent)reader.GetInt32(2),
                reader.GetInt32(3) != 0,
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetInt32(8),
                reader.GetInt64(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetInt32(12),
                reader.GetString(13),
                reader.GetString(14),
                reader.GetString(15)));

        return rows;
    }

    // ── Проверка цепочки ──────────────────────────────────────────────────────

    /// <summary>
    /// Пройти цепочку от начала и назвать ПЕРВУЮ строку, которая с ней разошлась; 0 — цепочка
    /// цела. Ловятся оба вида правки файла руками: поправленное содержимое (хэш строки перестает
    /// сходиться со своим же содержимым) и вырезанная строка (у следующей не сходится
    /// <c>Prev</c>).
    ///
    /// Чего проверка НЕ ловит: файл, удаленный или подмененный целиком. Цепочку можно
    /// пересчитать заново, если знать правило, — от этого спасает не хэш, а копия в папке аудита
    /// организации, и путать одно с другим нельзя.
    /// </summary>
    public long Verify()
    {
        try
        {
            lock (_lock)
            {
                using var connection = Open();
                using var command    = connection.CreateCommand();

                command.CommandText = Select + " ORDER BY Id;";

                var previous = "";

                foreach (var row in Read(command))
                {
                    if (row.Prev != previous)            return row.Id;
                    if (row.ChainHash(previous) != row.Hash) return row.Id;

                    previous = row.Hash;
                }

                return 0;
            }
        }
        catch (SqliteException failure)
        {
            Serilog.Log.Warning("Журнал безопасности: цепочка не проверилась — {Reason:l}",
                                failure.Message);
            return 0;
        }
    }

    // ── Общий доступ ──────────────────────────────────────────────────────────

    /// <summary>
    /// Журнал приложения. Заводится один раз при запуске; <c>null</c> — файл не открылся.
    ///
    /// Отдельная статика по тому же образцу, что и у рабочего журнала: писать в него будут из
    /// шлюза, из сценариев и из ручного экспорта, а таскать ссылку через все эти слои значило бы
    /// связать их между собой ради одной строки.
    /// </summary>
    public static SecurityJournal? Current { get; private set; }

    public static void Attach(SecurityJournal? journal) => Current = journal;

    /// <summary>
    /// Записать событие в журнал приложения. <c>false</c> — <b>следа не осталось</b>: журнала нет
    /// или строка не легла. Вызывающий, если он собирался что-то отправить, обязан отменить
    /// отправку — именно этот ответ и делает правило «не записалось — не отправляем» исполнимым.
    /// </summary>
    public static bool Add(SecurityEntry entry) => Current?.Write(entry) > 0;

    // ── Мелочи ────────────────────────────────────────────────────────────────

    /// <summary>SHA-256 в шестнадцатеричном виде — им считается и цепочка, и хэш тела.</summary>
    public static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? "")));

    /// <summary>SHA-256 тела запроса как оно уходит в сеть.</summary>
    public static string Sha256(byte[]? body) =>
        Convert.ToHexStringLower(SHA256.HashData(body ?? []));
}
