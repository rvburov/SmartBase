using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SmartBase.Database;

/// <summary>Каким каналом шел обмен — колонка журнала и разрез в отчете.</summary>
public enum RunChannel
{
    /// <summary>Запрос к сторонней программе по сети.</summary>
    Request = 0,

    /// <summary>Файл из папки обмена.</summary>
    Inbox,

    /// <summary>
    /// Сценарий целиком: итоговая строка поверх строк его шагов. Нужна ради неудач, которые
    /// случаются ДО первого запроса, — нет привязки, база не открыта, соединение потеряно: своей
    /// строки у них не будет вовсе, и без этой причина не видна нигде.
    ///
    /// </summary>
    Scenario,

    /// <summary>
    /// Обращение сторонней программы к нашему слушателю. Свой канал, а не «папка»: путь другой, и
    /// разбирающему «почему записи не появились» первым делом нужно знать, каким именно путем
    /// они шли.
    /// </summary>
    Ingress,

    /// <summary>
    /// Поток событий сторонней программы: подключился, потерял связь, прислал событие, которое не
    /// разобралось. Сами события сюда не пишутся — их след оставляет сценарий, который они
    /// завели. Ответ на событие — отдельная отправка со своей строкой.
    /// </summary>
    Stream,
}

/// <summary>
/// Строка журнала запусков: что дергали, чем кончилось, сколько прошло данных.
/// </summary>
/// <param name="Id">Ключ строки; 0 — строка еще не записана.</param>
/// <param name="At">Когда (ISO-8601, UTC).</param>
/// <param name="Channel">Канал.</param>
/// <param name="Name">Что запускали: имя запроса либо имя файла.</param>
/// <param name="Address">Куда ходили — БЕЗ секрета. В журнал попадает только замаскированный
/// адрес: журнал экспортируют и пересылают, и токен в нем — это утечка.</param>
/// <param name="Ok">Обмен удался.</param>
/// <param name="Explanation">Что случилось, человеческим языком.</param>
/// <param name="Rows">Сколько записей прошло.</param>
/// <param name="Bytes">Сколько байт получено или отправлено.</param>
/// <param name="Ms">Сколько заняло, миллисекунд.</param>
/// <param name="DbId">Ключ базы, которую задел запуск; 0 — ни одной.</param>
/// <param name="Counts">Обращение дошло до сети. Сохранено для совместимости строк журнала.</param>
/// <param name="Direction">Ключ карточки запроса для повтора; 0 — запуск без карточки.</param>
/// <param name="SentBody">Тело запроса, как оно ушло: без секрета и не длиннее потолка. Пусто —
/// тела не было либо запуск до сети не дошел.</param>
/// <param name="GotBody">Тело ответа сторонней программы, по тем же правилам.</param>
/// <param name="Scenario">Каким сценарием запущено; пусто — руками с экрана. Отдельно от
/// <paramref name="Name"/> намеренно: у строки шага в имени стоит запрос, и без этого поля
/// ответить «а чей это был запуск» можно только по времени соседних строк.</param>
/// <param name="Step">Какой это был шаг — «шаг 2 из 5: запрос». Пусто — запуск не из сценария.
/// Номер и род шага, а не его настройки: сценарий с тех пор могли поправить, и хранить в журнале
/// то, что уже неправда, хуже, чем не хранить ничего.</param>
public sealed record RunEntry(
    long       Id,
    string     At,
    RunChannel Channel,
    string     Name,
    string     Address,
    bool       Ok,
    string     Explanation,
    int        Rows,
    long       Bytes,
    long       Ms,
    int        DbId,
    bool       Counts,
    int        Direction,
    string     SentBody = "",
    string     GotBody  = "",
    string     Scenario = "",
    string     Step     = "")
{
    /// <summary>Новая строка «сейчас»; момент передается явно — тест не должен зависеть от часов.</summary>
    public static RunEntry New(RunChannel channel, string name, string address, bool ok,
                               string explanation, int rows, long bytes, TimeSpan elapsed,
                               int dbId, DateTimeOffset at, bool counts = false, int direction = 0,
                               string sentBody = "", string gotBody = "",
                               string scenario = "", string step = "") =>
        new(0, at.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            channel, name, address, ok, explanation, rows, bytes,
            (long)elapsed.TotalMilliseconds, dbId, counts, direction, sentBody, gotBody,
            scenario, step);

    /// <summary>Момент записи; <c>null</c> — строка испорчена правкой файла руками.</summary>
    public DateTimeOffset? Moment =>
        DateTimeOffset.TryParse(At, CultureInfo.InvariantCulture,
                                DateTimeStyles.RoundtripKind, out var moment)
            ? moment
            : null;
}

/// <summary>
/// Что показывать и что выгружать из журнала запусков.
/// </summary>
/// <param name="Channel">Только этот канал; <c>null</c> — любые.</param>
/// <param name="FailedOnly">Только неудачные строки — с них начинается всякий разбор.</param>
/// <param name="Since">Не раньше этого момента; <c>null</c> — с самого начала. Тот же отбор, что
/// у журнала безопасности, и по той же причине: «что было за последнюю неделю» — первый вопрос
/// разбора, а без периода на него отвечают, крутя список и считая глазами.</param>
/// <param name="Columns">Отбор по колонкам — «как в Excel»: имя колонки и выбранные значения
/// (docs/ROADMAP-API.md, шаг 14). Пустое множество значений означает «все», и такой колонки в
/// словаре не бывает. Имена колонок проверяются по белому списку
/// (<see cref="RunJournal.Filterable"/>): они уходят в SQL, а подставлять в SQL имя, пришедшее
/// с экрана, нельзя даже в своей программе.</param>
public sealed record RunFilter(
    RunChannel?                                                  Channel    = null,
    bool                                                         FailedOnly = false,
    IReadOnlyDictionary<string, IReadOnlyCollection<string>>?    Columns    = null,
    DateTimeOffset?                                              Since      = null)
{
    /// <summary>Без сужения — весь журнал.</summary>
    public static RunFilter Any { get; } = new();

    /// <summary>Отбор по колонкам; пусто — по колонкам не сужали.</summary>
    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> Picked =>
        Columns ?? new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);

    /// <summary>Сужено ли хоть чем-нибудь: окно выгрузки говорит об этом вслух.</summary>
    public bool Narrow => Channel is not null || FailedOnly || Since is not null || Picked.Count > 0;
}

/// <summary>
/// Журнал запусков обмена — рабочий журнал для отладки интеграций (docs/ROADMAP-API.md, 10.10).
///
/// Свой файл <c>integrations.db</c> рядом с <c>app.db</c> и <c>logs.db</c>, а не таблица внутри
/// файла базы данных, и это решение, а не случайность: файл базы ездит по сетевой папке, и тела
/// запросов раздули бы его на всех машинах разом. И не «Диагностика»: Serilog хранит текст, а
/// здесь нужны результаты и счетчики передачи.
///
/// <b>Секрета в журнале нет ни при каких настройках.</b> Адрес записывается замаскированным —
/// тем самым <c>SafeAddress</c>, который готовит отправляющий слой. Журнал выгружают и
/// пересылают, и токен в нем — это утечка, которую потом не отозвать.
///
/// <b>Журнал общий на все базы</b>, а не по базе: соединения и запросы тоже локальные, а один
/// запуск может задеть несколько баз. Ключ базы пишется полем строки.
///
/// Схема заводится аддитивно (<c>CREATE TABLE IF NOT EXISTS</c>): файл никогда не
/// пересоздается, иначе обновление программы стирало бы историю обмена.
/// </summary>
public sealed class RunJournal
{
    /// <summary>Имя файла журнала в папке данных приложения.</summary>
    public const string FileName = "integrations.db";

    /// <summary>
    /// Сколько строк хранить. Журнал чистит сам себя: обмен, идущий каждые полчаса, за год дает
    /// десятки тысяч строк, и разбираться в них все равно никто не будет, а файл вырастет.
    /// </summary>
    public const int Keep = 20_000;

    /// <summary>
    /// У скольких последних строк держатся ТЕЛА запроса и ответа. Числа в журнале дешевы и
    /// нужны долго — по ним видно, когда и с каким результатом шел обмен. Тела дороги и
    /// нужны коротко: их смотрят, пока разбираются со свежей бедой, а не через полгода.
    ///
    /// Держи тела у всех двадцати тысяч строк — и журнал отладки вырос бы в сотни мегабайт на
    /// ровном месте. Поэтому уборка стирает тела у строк старше этого числа, оставляя сами
    /// строки на месте: «запуск был, тело уже не хранится» честнее, чем пропавшая строка.
    /// </summary>
    public const int KeepBodies = 500;

    private readonly string _path;
    private readonly object _lock = new();

    public RunJournal(string path)
    {
        _path = path;
        Ensure();
    }

    /// <summary>Путь к файлу — для экрана «где это лежит» и для тестов.</summary>
    public string Path => _path;

    // ── Схема ─────────────────────────────────────────────────────────────────

    private void Ensure()
    {
        using var connection = Open();
        using var command    = connection.CreateCommand();

        command.CommandText = """
            CREATE TABLE IF NOT EXISTS runs (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                At          TEXT    NOT NULL,
                Channel     INTEGER NOT NULL,
                Name        TEXT    NOT NULL,
                Address     TEXT    NOT NULL,
                Ok          INTEGER NOT NULL,
                Explanation TEXT    NOT NULL,
                Rows        INTEGER NOT NULL,
                Bytes       INTEGER NOT NULL,
                Ms          INTEGER NOT NULL,
                DbId        INTEGER NOT NULL,
                Counts      INTEGER NOT NULL,
                Direction   INTEGER NOT NULL DEFAULT 0,
                SentBody    TEXT    NOT NULL DEFAULT '',
                GotBody     TEXT    NOT NULL DEFAULT '',
                Scenario    TEXT    NOT NULL DEFAULT '',
                Step        TEXT    NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS runs_at ON runs (At);
            """;

        command.ExecuteNonQuery();

        AddColumn(connection, "Direction", "INTEGER NOT NULL DEFAULT 0");
        AddColumn(connection, "SentBody",  "TEXT NOT NULL DEFAULT ''");
        AddColumn(connection, "GotBody",   "TEXT NOT NULL DEFAULT ''");
        AddColumn(connection, "Scenario",  "TEXT NOT NULL DEFAULT ''");
        AddColumn(connection, "Step",      "TEXT NOT NULL DEFAULT ''");
    }

    /// <summary>
    /// Достроить колонку в журнале, заведенном прежней версией программы. Строки, написанные до
    /// нее, получают значение по умолчанию: направление 0 — «неизвестно чье» (раскладывать их по
    /// карточкам задним числом не по чему), тела — пустые.
    ///
    /// Отдельным шагом, а не переписыванием таблицы: журнал копится годами, и ронять его при
    /// обновлении программы нельзя.
    /// </summary>
    private static void AddColumn(SqliteConnection connection, string name, string declaration)
    {
        using (var look = connection.CreateCommand())
        {
            look.CommandText = "SELECT COUNT(*) FROM pragma_table_info('runs') WHERE name = $name;";
            look.Parameters.AddWithValue("$name", name);
            if (Convert.ToInt32(look.ExecuteScalar(), CultureInfo.InvariantCulture) > 0) return;
        }

        using var add = connection.CreateCommand();
        // Имя и объявление приходят только отсюда, константами: параметров в DDL не бывает.
        add.CommandText = $"ALTER TABLE runs ADD COLUMN {name} {declaration};";
        add.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = SqliteGate.NewConnection($"Data Source={_path}");
        connection.Open();

        using var pragma = connection.CreateCommand();
        // WAL — по той же причине, что и у остальных файлов: журнал пишется в фоне, а читается
        // экраном, и блокировать одно другим незачем.
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    // ── Запись ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Записать строку. Возвращает ее ключ; 0 — записать не вышло.
    ///
    /// Сбой записи журнала НЕ роняет обмен: запись уже уложена, и падать из-за того, что не
    /// удалось об этом рассказать, значит терять сделанную работу. Про сбой узнает
    /// «Диагностика».
    /// </summary>
    public long Write(RunEntry entry)
    {
        try
        {
            lock (_lock)
            {
                using var connection = Open();
                using var command    = connection.CreateCommand();

                command.CommandText = """
                    INSERT INTO runs (At, Channel, Name, Address, Ok, Explanation, Rows, Bytes, Ms, DbId, Counts, Direction, SentBody, GotBody, Scenario, Step)
                    VALUES ($at, $channel, $name, $address, $ok, $explanation, $rows, $bytes, $ms, $db, $counts, $direction, $sentBody, $gotBody, $scenario, $step);
                    SELECT last_insert_rowid();
                    """;

                command.Parameters.AddWithValue("$at",          entry.At);
                command.Parameters.AddWithValue("$channel",     (int)entry.Channel);
                command.Parameters.AddWithValue("$name",        entry.Name ?? "");
                command.Parameters.AddWithValue("$address",     entry.Address ?? "");
                command.Parameters.AddWithValue("$ok",          entry.Ok ? 1 : 0);
                command.Parameters.AddWithValue("$explanation", entry.Explanation ?? "");
                command.Parameters.AddWithValue("$rows",        entry.Rows);
                command.Parameters.AddWithValue("$bytes",       entry.Bytes);
                command.Parameters.AddWithValue("$ms",          entry.Ms);
                command.Parameters.AddWithValue("$db",          entry.DbId);
                command.Parameters.AddWithValue("$counts",      entry.Counts ? 1 : 0);
                command.Parameters.AddWithValue("$direction",   entry.Direction);
                command.Parameters.AddWithValue("$sentBody",    entry.SentBody ?? "");
                command.Parameters.AddWithValue("$gotBody",     entry.GotBody  ?? "");
                command.Parameters.AddWithValue("$scenario",    entry.Scenario ?? "");
                command.Parameters.AddWithValue("$step",        entry.Step     ?? "");

                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }
        catch (SqliteException failure)
        {
            Serilog.Log.Warning("Журнал обмена: строка не записалась — {Reason:l}", failure.Message);
            return 0;
        }
    }

    // ── Чтение ────────────────────────────────────────────────────────────────

    private const string Select =
        "SELECT Id, At, Channel, Name, Address, Ok, Explanation, Rows, Bytes, Ms, DbId, Counts, "
      + "Direction, SentBody, GotBody, Scenario, Step FROM runs";

    /// <summary>Последние строки, свежие сверху.</summary>
    public IReadOnlyList<RunEntry> Recent(int take = 200) => Fetch(RunFilter.Any, before: 0, take);

    /// <summary>
    /// Строки под условия, свежие сверху, не больше <paramref name="take"/> штук.
    ///
    /// Сужение уходит в SQL, а не в память: «прочитать последние пятьсот и отобрать из них»
    /// означало бы, что человек, ищущий единственную неудачу вчерашней ночи, ее не найдет —
    /// удачные строки вытеснят ее из прочитанной пачки.
    /// </summary>
    public IReadOnlyList<RunEntry> Page(RunFilter filter, int take = 200) =>
        Fetch(filter, before: 0, take);

    /// <summary>
    /// Все строки под условия, свежие сверху, ЛЕНИВОЙ последовательностью: страницы читаются по
    /// мере того, как их забирают. Так выгружают в CSV — двадцать тысяч строк с телами не должны
    /// подниматься в память целиком ради того, чтобы уехать в файл.
    ///
    /// Замок берется на каждую страницу отдельно, а не на весь обход: держать его, пока
    /// вызывающий пишет строки на диск, значило бы остановить запись в журнал на все время
    /// выгрузки — то есть остановить обмен.
    /// </summary>
    public IEnumerable<RunEntry> Stream(RunFilter filter, int page = 500)
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

    /// <summary>Сколько строк подходит под условия — чтобы окно выгрузки знало, сколько их.</summary>
    public long Count(RunFilter filter)
    {
        try
        {
            lock (_lock)
            {
                using var connection = Open();
                using var command    = connection.CreateCommand();

                command.CommandText = "SELECT COUNT(*) FROM runs" + Where(filter, before: 0, command);

                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    /// <summary>Одна страница: строки старше <paramref name="before"/> (0 — с самого конца).</summary>
    private IReadOnlyList<RunEntry> Fetch(RunFilter filter, long before, int take)
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

                var rows = new List<RunEntry>();
                using var reader = command.ExecuteReader();

                while (reader.Read())
                    rows.Add(new RunEntry(
                        reader.GetInt64(0), reader.GetString(1), (RunChannel)reader.GetInt32(2),
                        reader.GetString(3), reader.GetString(4), reader.GetInt32(5) != 0,
                        reader.GetString(6), reader.GetInt32(7), reader.GetInt64(8),
                        reader.GetInt64(9), reader.GetInt32(10), reader.GetInt32(11) != 0,
                        reader.GetInt32(12), reader.GetString(13), reader.GetString(14),
                        reader.GetString(15), reader.GetString(16)));

                return rows;
            }
        }
        catch (SqliteException failure)
        {
            Serilog.Log.Warning("Журнал обмена: не читается — {Reason:l}", failure.Message);
            return [];
        }
    }

    /// <summary>
    /// Колонки, по которым можно отбирать значениями. Белый список, а не «любое имя»: имя
    /// колонки уходит в SQL как есть, и подставлять туда пришедшее с экрана нельзя даже в своей
    /// программе. Заголовки — те же, что в шапке таблицы на экране.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Filterable =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Channel"]  = "Канал",
            ["Name"]     = "Что запускали",
            ["Scenario"] = "Сценарий",
            ["Step"]     = "Шаг",
            ["Ok"]       = "Итог",
        };

    /// <summary>
    /// Какие значения бывают в колонке и сколько строк на каждое — для отбора «как в Excel».
    ///
    /// Считается ПОД ТЕКУЩИМ отбором, но без условия по самой этой колонке: иначе, сняв галочку,
    /// человек больше не увидел бы снятое значение в списке и не смог бы вернуть его обратно.
    /// </summary>
    /// <param name="column">Имя колонки из <see cref="Filterable"/>; чужое — пустой список.</param>
    /// <param name="filter">Отбор, под которым считаем.</param>
    /// <param name="take">Сколько значений вернуть, свежих и частых сверху.</param>
    public IReadOnlyList<(string Value, int Count)> Values(string column, RunFilter filter,
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
                    $"SELECT {column} AS V, COUNT(*) AS C FROM runs"
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
            Serilog.Log.Warning("Журнал обмена: значения колонки не читаются — {Reason:l}",
                                failure.Message);
            return [];
        }
    }

    /// <summary>Условия отбора одной строкой — и те же параметры в команду.</summary>
    /// <param name="except">Колонка, условие по которой не ставится: так считаются значения для
    /// ее же списка отбора.</param>
    private static string Where(RunFilter filter, long before, SqliteCommand command,
                                string except = "")
    {
        var parts = new List<string>();

        if (before > 0)
        {
            parts.Add("Id < $before");
            command.Parameters.AddWithValue("$before", before);
        }

        if (filter.Channel is { } channel)
        {
            parts.Add("Channel = $channel");
            command.Parameters.AddWithValue("$channel", (int)channel);
        }

        if (filter.FailedOnly) parts.Add("Ok = 0");

        // Период — такой же отбор в SQL, как и прочие: отбери его из прочитанной пачки, и
        // единственная неудача недельной давности не нашлась бы, вытесненная свежими строками.
        if (filter.Since is { } since)
        {
            parts.Add("At >= $since");
            command.Parameters.AddWithValue("$since",
                since.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        }

        var at = 0;

        foreach (var (column, values) in filter.Picked)
        {
            if (!Filterable.ContainsKey(column))                   continue;
            if (string.Equals(column, except, StringComparison.Ordinal)) continue;
            if (values.Count == 0)                                 continue;

            var names = new List<string>(values.Count);

            foreach (var value in values)
            {
                var name = "$v" + at++.ToString(CultureInfo.InvariantCulture);
                names.Add(name);
                command.Parameters.AddWithValue(name, value ?? "");
            }

            // Числовые колонки сравниваются как текст (CAST): значения приходят с экрана
            // строками, а «1» и 1 в SQLite — разные вещи.
            parts.Add($"CAST({column} AS TEXT) IN ({string.Join(", ", names)})");
        }

        return parts.Count > 0 ? " WHERE " + string.Join(" AND ", parts) : "";
    }

    // ── Выгрузка ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Колонки выгрузки — те же сведения, что и в подробностях строки на экране.
    ///
    /// Тел здесь нет, и это не забывчивость: в CSV они превратили бы файл в нечитаемый — тело
    /// бывает в восемь килобайт, с переносами строк и кавычками. Выгрузку открывают таблицей и
    /// смотрят «что, когда и чем кончилось»; тело смотрят на экране, у одной строки.
    /// </summary>
    public static readonly IReadOnlyList<string> CsvHeaders =
    [
        "Время", "Канал", "Сценарий", "Шаг", "Что запускали", "Адрес", "Итог",
        "Объяснение", "Строк", "Байт", "Миллисекунд", "База", "В расход",
    ];

    /// <summary>Одна строка выгрузки — в том же порядке, что и заголовки.</summary>
    public static IReadOnlyList<string> CsvRow(RunEntry entry) =>
    [
        entry.Moment?.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.CurrentCulture)
            ?? entry.At,
        ChannelName(entry.Channel),
        entry.Scenario,
        entry.Step,
        entry.Name,
        entry.Address,
        entry.Ok ? "удача" : "ошибка",
        entry.Explanation,
        entry.Rows.ToString(CultureInfo.CurrentCulture),
        entry.Bytes.ToString(CultureInfo.CurrentCulture),
        entry.Ms.ToString(CultureInfo.CurrentCulture),
        entry.DbId > 0 ? entry.DbId.ToString(CultureInfo.CurrentCulture) : "",
        entry.Counts ? "да" : "нет",
    ];

    /// <summary>Название канала для выгрузки — то же слово, что и на экране.</summary>
    public static string ChannelName(RunChannel channel) => channel switch
    {
        RunChannel.Request  => "сеть",
        RunChannel.Inbox    => "папка",
        RunChannel.Scenario => "сценарий",
        RunChannel.Ingress  => "прием",
        RunChannel.Stream   => "поток",
        _                   => channel.ToString(),
    };

    // ── Уборка ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Оставить последние <see cref="Keep"/> строк, а тела — только у последних
    /// <see cref="KeepBodies"/>. Возвращает, сколько строк удалено; стертые тела не считаются —
    /// строка осталась, и пропажей это не было.
    ///
    /// Уборка идет по КЛЮЧУ, а не по дате: часы на машине переводят, а ключ растет всегда.
    /// </summary>
    public int Trim(int keep = Keep, int keepBodies = KeepBodies)
    {
        try
        {
            lock (_lock)
            {
                using var connection = Open();
                using var command    = connection.CreateCommand();

                // Два шага, и порядок между ними не важен: сперва уходят старые строки
                // целиком, потом у оставшихся, но уже немолодых, стираются тела.
                command.CommandText = """
                    DELETE FROM runs WHERE Id <= (
                        SELECT IFNULL(MAX(Id), 0) - $keep FROM runs
                    );
                    """;

                command.Parameters.AddWithValue("$keep", Math.Max(1, keep));

                var removed = command.ExecuteNonQuery();

                using var bodies = connection.CreateCommand();

                bodies.CommandText = """
                    UPDATE runs SET SentBody = '', GotBody = ''
                    WHERE (SentBody <> '' OR GotBody <> '')
                      AND Id <= (SELECT IFNULL(MAX(Id), 0) - $keepBodies FROM runs);
                    """;

                bodies.Parameters.AddWithValue("$keepBodies", Math.Max(1, keepBodies));
                bodies.ExecuteNonQuery();

                return removed;
            }
        }
        catch (SqliteException failure)
        {
            Serilog.Log.Warning("Журнал обмена: уборка не удалась — {Reason:l}", failure.Message);
            return 0;
        }
    }

    /// <summary>
    /// Очистить журнал целиком. Рабочий журнал чистить пользователю МОЖНО — в отличие от
    /// журнала безопасности, который заводится отдельным файлом и не чистится никем.
    /// </summary>
    public void Clear()
    {
        try
        {
            lock (_lock)
            {
                using var connection = Open();
                using var command    = connection.CreateCommand();
                command.CommandText = "DELETE FROM runs;";
                command.ExecuteNonQuery();
            }
        }
        catch (SqliteException failure)
        {
            Serilog.Log.Warning("Журнал обмена: очистка не удалась — {Reason:l}", failure.Message);
        }
    }

    // ── Единственный экземпляр на приложение ──────────────────────────────────

    /// <summary>
    /// Журнал приложения; <c>null</c> — еще не подключен. Прием тот же, что у
    /// <c>AppSettings.AttachStore</c>: контейнера зависимостей
    /// в приложении нет, а тащить журнал параметром через десяток экранов незачем.
    ///
    /// Писать через <c>null</c> безопасно — <see cref="Add"/> просто ничего не сделает. Обмен
    /// не должен падать из-за того, что о нем не удалось рассказать.
    /// </summary>
    public static RunJournal? Current { get; private set; }

    /// <summary>Подключить журнал приложения; <c>null</c> — отключить (тесты, разбор).</summary>
    public static void Attach(RunJournal? journal) => Current = journal;

    /// <summary>
    /// Записать строку в журнал приложения, если он подключен. Ради этой строчки холдер и
    /// заведен: места записи разбросаны по каналам, и в каждом проверять наличие журнала
    /// значило бы повторить одно и то же условие пять раз.
    /// </summary>
    public static void Add(RunEntry entry) => Current?.Write(entry);
}
