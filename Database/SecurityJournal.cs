using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace SmartBase.Database;

/// <summary>Что за событие записано в журнал безопасности.</summary>
public enum SecurityEvent
{
    /// <summary>
    /// Попытка отправить данные наружу — разрешенная или заблокированная. Пишется ДО первого
    /// сокета: правило «не записалось — не отправляем» иначе не значило бы ничего. Поэтому здесь
    /// и не может быть кода ответа — его в тот момент еще нет; чем кончилось, говорит
    /// <see cref="Result"/>.
    /// </summary>
    Send = 0,

    /// <summary>Направление подтверждено мастер-паролем.</summary>
    Confirm,

    /// <summary>Подтверждение снято: направление правили, вышел срок, оно простаивало.</summary>
    Revoke,

    /// <summary>Понижена метка конфиденциальности поля или коллекции.</summary>
    Downgrade,

    /// <summary>Человек выгрузил данные руками. Функция рабочая, убирать ее нельзя, но след нужен.</summary>
    Export,

    /// <summary>
    /// Программа открыла или закрыла порт приема. Данные при этом никуда не уходят, но открытый
    /// порт на машине с секретными данными — первое, о чем спросит проверяющий, и ответ «когда
    /// именно он был открыт» должен лежать в том же журнале, что и остальное.
    /// </summary>
    Listen,

    /// <summary>
    /// Чужая программа прочитала записи по выдаче — либо попробовала и получила отказ. Ответ на
    /// чтение — данные, покидающие программу, поэтому след тот же, что у отправки: кто, какие
    /// поля, сколько строк и отпечаток ответа (docs/ROADMAP-API.md, шаг 10).
    /// </summary>
    Read,

    /// <summary>
    /// Человек посмотрел секрет соединения в менеджере паролей — либо попробовал и не сошелся
    /// мастер-паролем (docs/ROADMAP-API.md, шаг 16). Сам секрет наружу при этом не уходит, но
    /// уходит из программы в голову и, скорее всего, в чужой конфигурационный файл: с этого
    /// момента отвечать за него некому, и запись — единственное, что об этом помнит.
    /// </summary>
    Reveal,

    /// <summary>
    /// Из журнала удалены строки старше срока, названного политикой организации
    /// (docs/ROADMAP-API.md, 10.10). Событие пишется В САМ ЖУРНАЛ и не случайно: проверяющий,
    /// увидевший, что записей за прошлый год нет, обязан прочитать здесь, что их удалила
    /// программа по правилу организации, а не кто-то руками. Строка называет и сколько удалено,
    /// и с каким сроком.
    /// </summary>
    Trim,

    /// <summary>
    /// Чем кончилась отправка: код ответа чужой программы, объем и время — либо причина, по
    /// которой до сети не дошло (docs/ROADMAP-API.md, 10.10).
    ///
    /// <b>Почему строк две, а не одна.</b> Первая (<see cref="Send"/>) пишется ДО сокета, иначе
    /// правило «не записалось — не отправляем» не значит ничего: след обязан лечь раньше, чем
    /// данные уйдут. Но в тот момент кода ответа еще не существует, и приписать его к той же
    /// строке нельзя — только переписав ее задним числом, то есть сломав цепочку, которая ровно
    /// от этого и бережет.
    ///
    /// Без второй строки проверяющий не отличает дошедшую отправку от застрявшей: «собирались
    /// отправить» выглядит одинаково и когда ушло, и когда сервер ответил 500.
    ///
    /// Код ответа лежит в ПОЯСНЕНИИ, а не отдельной колонкой, и это осознанно: колонки входят в
    /// канонический вид строки (<see cref="SecurityEntry.Canonical"/>), а он — в хэш. Новая
    /// колонка сделала бы недействительными хэши ВСЕХ прежних строк, и первая же проверка после
    /// обновления объявила бы честный журнал испорченным.
    /// </summary>
    Result,

    /// <summary>
    /// Человек скопировал значения таблицы в буфер обмена (<see cref="CopyTrace"/>). Тот же вынос
    /// данных, что и выгрузка, только без файла. Стоит последним: номер события входит в хэш
    /// строки, и вставка в середину сдвинула бы номера — и хэши — всех прежних строк.
    /// </summary>
    Copy,
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
/// Чем сузить выборку журнала. Пустой фильтр — все строки подряд.
/// </summary>
/// <param name="Event">Только события этого рода; <c>null</c> — любые.</param>
/// <param name="BlockedOnly">Только заблокированные попытки — их ищут чаще всего.</param>
/// <param name="Since">Не раньше этого момента; <c>null</c> — с самого начала.</param>
/// <param name="Columns">Отбор по колонкам — «как в Excel»: имя колонки и выбранные значения
/// (docs/ROADMAP-API.md, шаг 14). Имена проверяются по белому списку
/// (<see cref="SecurityJournal.Filterable"/>): они уходят в SQL, а подставлять туда пришедшее с
/// экрана нельзя даже в своей программе.</param>
public sealed record SecurityFilter(
    SecurityEvent?  Event       = null,
    bool            BlockedOnly = false,
    DateTimeOffset? Since       = null,
    IReadOnlyDictionary<string, IReadOnlyCollection<string>>? Columns = null)
{
    /// <summary>Отбор по колонкам; пусто — по колонкам не сужали.</summary>
    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> Picked =>
        Columns ?? new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);

    /// <summary>
    /// Сужен ли отбор хоть чем-нибудь — от этого зависят и текст пустого списка, и пометка
    /// «отобрано» в выгрузке.
    ///
    /// Живет у ФИЛЬТРА, а не у экрана: экран считал то же самое дважды своими руками — в
    /// списке и в выгрузке, — и третье место (или новое поле отбора) разошлось бы с ними молча.
    /// </summary>
    public bool Narrow =>
        Event is not null || BlockedOnly || Since is not null || Picked.Count > 0;
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
///   <item><b>Пользователь его не чистит.</b> Ни кнопки, ни пункта меню: журнал, который чистит
///   тот, за кем он ведется, не журнал. Убирается он только по сроку из файла политики
///   (<see cref="Trim"/>, поле <c>journalKeepDays</c>), и уборка оставляет в самом журнале строку
///   о себе, а в цепочке — шов, по которому проверка продолжает счет.</item>
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

            -- ШОВ ЦЕПОЧКИ после уборки по сроку. Без него удаление старых строк ломало бы
            -- проверку: у самой ранней уцелевшей строки «предыдущий хэш» указывает на строку,
            -- которой больше нет, и Verify честно назвал бы ее испорченной. Здесь лежит хэш
            -- ПОСЛЕДНЕЙ удаленной строки — с него проверка и начинает считать.
            CREATE TABLE IF NOT EXISTS trims (
                Id       INTEGER PRIMARY KEY AUTOINCREMENT,
                At       TEXT    NOT NULL,
                UpToId   INTEGER NOT NULL,
                LastHash TEXT    NOT NULL,
                Removed  INTEGER NOT NULL,
                KeepDays INTEGER NOT NULL
            );
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

    // ── Уборка по сроку политики ──────────────────────────────────────────────

    /// <summary>
    /// Удалить строки старше срока, названного политикой организации (docs/ROADMAP-API.md, 10.10).
    /// Возвращает, сколько строк удалено; 0 — удалять было нечего либо срок не назначен.
    ///
    /// <b>Кнопки для этого нет и не будет.</b> Журнал, который чистит тот, за кем он ведется, —
    /// не журнал. Срок назначает файл политики, пользователь его не правит, а программа лишь
    /// исполняет: это ровно то, чего второе правило журнала и требовало, когда говорило «ротация
    /// придет вместе с правилами политики».
    ///
    /// <b>Цепочка при этом не рвется.</b> Наивное удаление сломало бы проверку: у самой ранней
    /// уцелевшей строки «предыдущий хэш» указывал бы на строку, которой больше нет, и
    /// <see cref="Verify"/> честно назвал бы ЧЕСТНУЮ строку испорченной — а проверка, которая
    /// врет после штатной уборки, хуже отсутствия проверки. Поэтому в таблице <c>trims</c>
    /// остается шов: хэш последней удаленной строки, с которого проверка продолжает счет.
    ///
    /// <b>В самом журнале остается строка о том, что уборка была</b> — с числом удаленных и
    /// сроком. Проверяющий, не нашедший записей за прошлый год, обязан прочитать, куда они
    /// делись, в том же файле, а не догадываться.
    ///
    /// Шов и удаление идут ОДНОЙ транзакцией: удаление без шва оставило бы журнал непроверяемым.
    /// </summary>
    /// <param name="keepDays">Сколько дней хранить; 0 и меньше — не убирать ничего.</param>
    /// <param name="now">Момент уборки; от него отсчитывается срок.</param>
    public int Trim(int keepDays, DateTimeOffset now)
    {
        if (keepDays <= 0) return 0;

        var cutoff = now.ToUniversalTime().AddDays(-keepDays)
                        .ToString("o", CultureInfo.InvariantCulture);

        try
        {
            long removed;
            int  keep = keepDays;

            lock (_lock)
            {
                using var connection  = Open();
                using var transaction = connection.BeginTransaction();

                // Граница: последняя строка, которая под срок уже не подходит. Берется ключ, а не
                // дата: время в строках — текст, и сравнение по ключу после однократного отбора
                // надежнее повторного сравнения строк.
                using var edge = connection.CreateCommand();
                edge.Transaction = transaction;
                edge.CommandText = "SELECT Id, Hash FROM events WHERE At < $cutoff "
                                 + "ORDER BY Id DESC LIMIT 1;";
                edge.Parameters.AddWithValue("$cutoff", cutoff);

                long   upTo = 0;
                string hash = "";

                using (var reader = edge.ExecuteReader())
                    if (reader.Read())
                    {
                        upTo = reader.GetInt64(0);
                        hash = reader.GetString(1);
                    }

                if (upTo == 0) return 0;

                using var cut = connection.CreateCommand();
                cut.Transaction = transaction;
                cut.CommandText = "DELETE FROM events WHERE Id <= $upTo;";
                cut.Parameters.AddWithValue("$upTo", upTo);
                removed = cut.ExecuteNonQuery();

                using var seam = connection.CreateCommand();
                seam.Transaction = transaction;
                seam.CommandText = """
                    INSERT INTO trims (At, UpToId, LastHash, Removed, KeepDays)
                    VALUES ($at, $upTo, $hash, $removed, $keep);
                    """;
                seam.Parameters.AddWithValue("$at",      now.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                seam.Parameters.AddWithValue("$upTo",    upTo);
                seam.Parameters.AddWithValue("$hash",    hash);
                seam.Parameters.AddWithValue("$removed", removed);
                seam.Parameters.AddWithValue("$keep",    keep);
                seam.ExecuteNonQuery();

                transaction.Commit();
            }

            // Строка о самой уборке пишется ПОСЛЕ и обычным путем: она продолжает цепочку с
            // уцелевшего хвоста и сама под уборку не попадает — ей ноль дней от роду.
            Write(SecurityEntry.New(
                SecurityEvent.Trim, allowed: true, who: "программа", scenario: "",
                address: Path, fields: "", rows: (int)Math.Min(int.MaxValue, removed), bytes: 0,
                bodyHash: "",
                explanation: $"по сроку политики организации ({keep} дн.) удалено строк: {removed:N0}",
                dbId: 0, at: now));

            return (int)Math.Min(int.MaxValue, removed);
        }
        catch (SqliteException failure)
        {
            Serilog.Log.Warning("Журнал безопасности: уборка по сроку не прошла — {Reason:l}",
                                failure.Message);
            return 0;
        }
    }

    /// <summary>
    /// Хэш, с которого начинается уцелевшая часть цепочки: последний шов уборки. Пусто — уборок
    /// не было, и цепочка начинается с нуля, как у нового файла.
    /// </summary>
    private static string SeamHash(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT LastHash FROM trims ORDER BY UpToId DESC LIMIT 1;";

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

    /// <summary>
    /// Строки под условия, свежие сверху, не больше <paramref name="take"/> штук.
    ///
    /// Сужение уходит в SQL, а не в память: журнал не чистит никто, он копится годами, и
    /// «прочитать последние пятьсот и отобрать из них» означало бы, что проверяющий, ищущий одно
    /// заблокированное событие полугодовой давности, его просто не найдет.
    /// </summary>
    public IReadOnlyList<SecurityEntry> Page(SecurityFilter filter, int take = 200) =>
        Fetch(filter, before: 0, take);

    /// <summary>
    /// Все строки под условия, свежие сверху, ЛЕНИВОЙ последовательностью: страницы читаются по
    /// мере того, как их забирают. Так выгружают в CSV — журнал за год не должен подниматься в
    /// память целиком ради того, чтобы уехать в файл.
    ///
    /// Замок берется на каждую страницу отдельно, а не на весь обход: держать его, пока
    /// вызывающий пишет строки на диск, значило бы остановить запись в журнал на все время
    /// выгрузки — то есть остановить исходящий обмен.
    /// </summary>
    public IEnumerable<SecurityEntry> Stream(SecurityFilter filter, int page = 500)
    {
        var size   = Math.Max(1, page);
        var before = 0L;

        while (true)
        {
            var rows = Fetch(filter, before, size);
            if (rows.Count == 0) yield break;

            foreach (var row in rows) yield return row;

            before = rows[^1].Id;
            if (rows.Count < size) yield break;
        }
    }

    /// <summary>Сколько строк подходит под условия.</summary>
    public long Count(SecurityFilter filter)
    {
        try
        {
            lock (_lock)
            {
                using var connection = Open();
                using var command    = connection.CreateCommand();

                command.CommandText = "SELECT COUNT(*) FROM events" + Where(filter, before: 0, command);

                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    /// <summary>Одна страница: строки старше <paramref name="before"/> (0 — с самого конца).</summary>
    private IReadOnlyList<SecurityEntry> Fetch(SecurityFilter filter, long before, int take)
    {
        try
        {
            lock (_lock)
            {
                using var connection = Open();
                using var command    = connection.CreateCommand();

                command.CommandText = Select + Where(filter, before, command)
                                    + " ORDER BY Id DESC LIMIT $take;";

                command.Parameters.AddWithValue("$take", Math.Max(1, take));

                return Read(command);
            }
        }
        catch (SqliteException failure)
        {
            Serilog.Log.Warning("Журнал безопасности: страница не прочиталась — {Reason:l}",
                                failure.Message);
            return [];
        }
    }

    /// <summary>Условия отбора одной строкой — и те же параметры в команду.</summary>
    private static string Where(SecurityFilter filter, long before, SqliteCommand command,
                                string except = "")
    {
        var parts = new List<string>();

        if (before > 0)
        {
            parts.Add("Id < $before");
            command.Parameters.AddWithValue("$before", before);
        }

        if (filter.Event is { } kind)
        {
            parts.Add("Event = $event");
            command.Parameters.AddWithValue("$event", (int)kind);
        }

        if (filter.BlockedOnly) parts.Add("Allowed = 0");

        var at = 0;

        foreach (var (column, values) in filter.Picked)
        {
            if (!Filterable.ContainsKey(column))                         continue;
            if (string.Equals(column, except, StringComparison.Ordinal)) continue;
            if (values.Count == 0)                                       continue;

            var names = new List<string>(values.Count);

            foreach (var value in values)
            {
                var name = "$v" + at++.ToString(CultureInfo.InvariantCulture);
                names.Add(name);
                command.Parameters.AddWithValue(name, value ?? "");
            }

            // Числовые колонки сравниваются как текст: значения приходят с экрана строками.
            parts.Add($"CAST({column} AS TEXT) IN ({string.Join(", ", names)})");
        }

        // Момент сравнивается строкой, и это работает ровно потому, что все строки записаны
        // одним форматом: ISO-8601 в UTC сортируется как текст.
        if (filter.Since is { } since)
        {
            parts.Add("At >= $since");
            command.Parameters.AddWithValue("$since",
                since.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        }

        return parts.Count > 0 ? " WHERE " + string.Join(" AND ", parts) : "";
    }

    // ── Выгрузка ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Колонки выгрузки. Те же сведения, что и в подробностях строки на экране, и ни одного
    /// сверх того: то, чего нет на экране, в файле для проверяющего взяться неоткуда.
    /// </summary>
    public static readonly IReadOnlyList<string> CsvHeaders =
    [
        "Время", "Событие", "Решение", "Кто", "Сценарий", "Адрес", "Поля",
        "Записей", "Байт", "Отпечаток тела", "Подтвердил", "База", "Пояснение",
    ];

    /// <summary>
    /// Событие словами. Одно на экран и на выгрузку: проверяющий, сверяющий файл с экраном,
    /// должен видеть одни и те же слова, а не догадываться, что «Send» и «отправка» — одно и то
    /// же событие.
    /// </summary>
    public static string Name(SecurityEvent kind) => kind switch
    {
        SecurityEvent.Send      => "отправка данных наружу",
        SecurityEvent.Confirm   => "подтверждение направления",
        SecurityEvent.Revoke    => "снятие подтверждения",
        SecurityEvent.Downgrade => "понижение метки конфиденциальности",
        SecurityEvent.Listen    => "порт приема",
        SecurityEvent.Read      => "чтение по выдаче",
        SecurityEvent.Reveal    => "показан секрет соединения",
        SecurityEvent.Trim      => "уборка журнала по сроку политики",
        SecurityEvent.Result    => "итог отправки",
        SecurityEvent.Copy      => "копирование в буфер обмена",
        _                       => "ручная выгрузка данных",
    };

    /// <summary>Строка журнала колонками выгрузки, в порядке <see cref="CsvHeaders"/>.</summary>
    public static IReadOnlyList<string> CsvRow(SecurityEntry entry) =>
    [
        entry.Moment is { } moment
            ? moment.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture)
            : entry.At,
        Name(entry.Event),
        entry.Allowed ? "разрешено" : "заблокировано",
        entry.Who,
        entry.Scenario,
        entry.Address,
        entry.Fields,
        entry.Rows.ToString(CultureInfo.InvariantCulture),
        entry.Bytes.ToString(CultureInfo.InvariantCulture),
        entry.BodyHash,
        entry.Confirmed,
        entry.DbId > 0 ? entry.DbId.ToString(CultureInfo.InvariantCulture) : "",
        entry.Explanation,
    ];

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

    /// <summary>
    /// Колонки, по которым можно отбирать значениями, и их заголовки на экране. Белый список, а
    /// не «любое имя»: имя колонки уходит в SQL как есть.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Filterable =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Event"]    = "Событие",
            ["Allowed"]  = "Итог",
            ["Who"]      = "Кто",
            ["Scenario"] = "Сценарий",
        };

    /// <summary>
    /// Какие значения бывают в колонке и сколько строк на каждое — для отбора «как в Excel».
    /// Считается под текущим отбором, но без условия по самой этой колонке: иначе снятое
    /// значение исчезало бы из списка и вернуть его было бы нечем.
    /// </summary>
    public IReadOnlyList<(string Value, int Count)> Values(string column, SecurityFilter filter,
                                                          int take = 200)
    {
        if (!Filterable.ContainsKey(column)) return [];

        try
        {
            lock (_lock)
            {
                using var connection = Open();
                using var command    = connection.CreateCommand();

                command.CommandText =
                    $"SELECT {column} AS V, COUNT(*) AS C FROM events"
                  + Where(filter, before: 0, command, except: column)
                  + " GROUP BY V ORDER BY C DESC, V LIMIT $take;";

                command.Parameters.AddWithValue("$take", Math.Max(1, take));

                var values = new List<(string, int)>();
                using var reader = command.ExecuteReader();

                while (reader.Read())
                    values.Add((reader.IsDBNull(0) ? "" : reader.GetValue(0)?.ToString() ?? "",
                                reader.GetInt32(1)));

                return values;
            }
        }
        catch (SqliteException failure)
        {
            Serilog.Log.Warning("Журнал безопасности: значения колонки не читаются — {Reason:l}",
                                failure.Message);
            return [];
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

                // Счет начинается со шва, если уборка по сроку политики уже была: иначе самая
                // ранняя уцелевшая строка ссылалась бы на удаленную, и честная строка сошла бы
                // за испорченную.
                var previous = SeamHash(connection);

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
