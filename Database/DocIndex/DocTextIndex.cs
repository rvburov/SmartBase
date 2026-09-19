using System.IO;
using Microsoft.Data.Sqlite;
using SmartBase.Core.Documents;

namespace SmartBase.Database.DocIndex;

/// <summary>Найденный документ: путь плюс фрагмент текста с маркерами подсветки.</summary>
/// <param name="Path">Путь к документу — им находка сшивается с записями базы.</param>
/// <param name="Snippet">
/// Фрагмент вокруг совпадения. Совпавшие слова обрамлены
/// <see cref="DocTextIndex.HitOpen"/>/<see cref="DocTextIndex.HitClose"/> — карточка режет строку
/// по ним и рисует подсветку.
/// </param>
public readonly record struct DocHit(string Path, string Snippet);

/// <summary>Что кеш знает про файл, не открывая его.</summary>
public readonly record struct DocIndexEntry(long Mtime, long Size, DocTextStatus Status, int Pages);

/// <summary>
/// Перепись документов по состоянию текста — из нее собираются все числа подвала блока поиска.
/// </summary>
/// <param name="Total">Сколько документов в охвате (файлов, существующих на диске).</param>
/// <param name="Read">Текст прочитан — поиск по содержимому работает.</param>
/// <param name="NotRead">Не разобраны или изменились с прошлого разбора.</param>
/// <param name="NoText">Сканы без текстового слоя — перечитывание не поможет.</param>
/// <param name="Unsupported">Форматы без извлечения текста — ищутся только по названию.</param>
/// <param name="Failed">Не удалось прочитать: битый, занят или тяжелее порога.</param>
public readonly record struct DocCensus(
    int Total, int Read, int NotRead, int NoText, int Unsupported, int Failed)
{
    /// <summary>Внутри скольких документов поиск искать не будет, сколько ни читай.</summary>
    public int NeverSearchable => NoText + Unsupported + Failed;
}

/// <summary>
/// Кеш текста документов для поиска по содержимому — отдельный файл <c>docs_index.db</c> рядом с
/// <c>app.db</c>, <c>logs.db</c> и <c>integrations.db</c>. Без EF, сырой SQLite (как
/// <c>Logging/LogReader.cs</c>): у кеша нет модели EF и не должно быть — он пересобираемый.
///
/// <b>Почему отдельный файл, а не таблица в базе.</b> Текст документов измеряется гигабайтами.
/// В файле базы он раздул бы и саму базу, и каждый ее бэкап, и каждый дубль коллекции, и переезд
/// по сетевой папке — при том что восстанавливается он из самих документов за одну индексацию.
/// Кеш всегда локальный, поэтому раздел одинаково работает и с SQLite-, и с PostgreSQL-базами.
///
/// <b>Ключ — путь файла, а не запись.</b> Один и тот же документ, на который ссылаются разные
/// записи и разные базы, разбирается ровно один раз.
///
/// <b>Почему здесь уместен FTS5, хотя триграммный FTS заголовков откатили.</b> Тот индекс был
/// обязательным и глобальным: он строился бэкафиллом при первом открытии большой базы — минуты
/// ожидания на ровном месте, — а триграммный токенизатор вдобавок деградировал на частых
/// подстроках. Этот индекс в отдельном пересобираемом файле, наполняется лениво и только по
/// документам подключенных коллекций, токенизатор словесный, а альтернативы нет вовсе:
/// <c>LIKE</c> по гигабайтам текста — полный скан на каждый поиск.
///
/// Таблица FTS обычная, а не <c>content=''</c>: contentless-вариант не умеет <c>snippet()</c>,
/// а сниппет — это и есть средняя строка карточки.
/// </summary>
public sealed class DocTextIndex
{
    /// <summary>Имя файла кеша в папке данных приложения.</summary>
    public const string FileName = "docs_index.db";

    /// <summary>
    /// Маркеры совпадения в сниппете. Управляющие символы STX/ETX выбраны намеренно: в текстах
    /// документов их не бывает, а обычные скобки или теги встретились бы в самом документе и
    /// разъехались бы с разбором (<see cref="SplitSnippet"/>). Нормализация вычищает управляющие
    /// символы (<c>DocTextExtractor.Normalize</c>), поэтому подделать маркер содержимым нельзя.
    /// </summary>
    public const char HitOpen  = '\u0002';
    public const char HitClose = '\u0003';

    /// <summary>
    /// Сколько слов показывает сниппет вокруг совпадения по умолчанию. Зовущий может попросить
    /// больше: в ленте библиотеки число строк сниппета зависит от размера карточки, и на шесть
    /// строк четырнадцати слов не хватает — строки остались бы пустыми.
    /// </summary>
    public const int SnippetWordsDefault = 14;

    private readonly string _path;
    private readonly object _lock = new();

    public DocTextIndex(string path)
    {
        _path = path;
        Ensure();
    }

    /// <summary>Путь к файлу кеша — для экрана настроек и для тестов.</summary>
    public string Path => _path;

    // ── Схема ─────────────────────────────────────────────────────────────────

    private void Ensure()
    {
        using var connection = Open();
        using var command    = connection.CreateCommand();

        // Аддитивно (IF NOT EXISTS): файл никогда не пересоздается сам — обновление программы
        // не должно стирать часы индексации. Осознанный сброс делает Clear().
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS doc_files (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                PathKey   TEXT    NOT NULL UNIQUE,
                Mtime     INTEGER NOT NULL,
                Size      INTEGER NOT NULL,
                Status    INTEGER NOT NULL,
                Chars     INTEGER NOT NULL,
                Pages     INTEGER NOT NULL DEFAULT 0,
                IndexedAt TEXT    NOT NULL);

            CREATE VIRTUAL TABLE IF NOT EXISTS doc_fts
                USING fts5(Text, tokenize='unicode61 remove_diacritics 2');
            """;
        command.ExecuteNonQuery();

        // Колонка Pages добавлена позже. EF-миграций у кеша нет и быть не должно — он
        // пересобираемый файл, — поэтому догоняем старые кеши руками. Ошибку глотаем: у свежего
        // файла колонка уже есть, и SQLite на это честно ругается «duplicate column name».
        try
        {
            using var add = connection.CreateCommand();
            add.CommandText = "ALTER TABLE doc_files ADD COLUMN Pages INTEGER NOT NULL DEFAULT 0";
            add.ExecuteNonQuery();
        }
        catch (SqliteException) { /* колонка уже есть */ }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();

        using var pragma = connection.CreateCommand();
        // WAL — по той же причине, что у остальных файлов: кеш пишется фоновой очередью, а
        // читается поиском, и блокировать одно другим незачем.
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    /// <summary>
    /// Ключ пути: полный путь в нормальном виде. Регистр НЕ схлопываем — на Linux
    /// «Отчет.pdf» и «отчет.pdf» это разные файлы, и склеивать их нельзя.
    /// </summary>
    public static string KeyOf(string path)
    {
        try { return System.IO.Path.GetFullPath(path); }
        catch { return path ?? ""; }
    }

    // ── Что уже разобрано ─────────────────────────────────────────────────────

    /// <summary>
    /// Состояние кеша по набору путей: путь → что про него известно. Пути, которых в кеше нет,
    /// в результат не попадают. Читается пачками, чтобы не плодить запросов на каждый файл.
    /// </summary>
    public Dictionary<string, DocIndexEntry> Known(IReadOnlyCollection<string> paths)
    {
        var result = new Dictionary<string, DocIndexEntry>(StringComparer.Ordinal);
        if (paths.Count == 0) return result;

        lock (_lock)
        {
            using var connection = Open();
            foreach (var chunk in Chunks(paths, 400))
            {
                using var cmd = connection.CreateCommand();
                var holes = string.Join(",", chunk.Select((_, i) => "@p" + i));
                cmd.CommandText = $"SELECT PathKey, Mtime, Size, Status, Pages FROM doc_files WHERE PathKey IN ({holes})";
                for (var i = 0; i < chunk.Count; i++) cmd.Parameters.AddWithValue("@p" + i, chunk[i]);

                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                    result[rdr.GetString(0)] = new DocIndexEntry(
                        rdr.GetInt64(1), rdr.GetInt64(2), (DocTextStatus)rdr.GetInt32(3), rdr.GetInt32(4));
            }
        }
        return result;
    }

    /// <summary>
    /// Пути, которые надо разобрать: которых в кеше нет либо у которых изменились время правки
    /// или размер. Файл, признанный неподдержанным или битым, повторно не тревожится, пока сам
    /// не изменится, — иначе очередь каждый раз перемалывала бы одни и те же сканы.
    /// </summary>
    public List<string> Pending(IReadOnlyCollection<string> paths) => Survey(paths).Pending;

    /// <summary>
    /// Один проход по набору путей: что надо разобрать И сколько документов в каком состоянии.
    ///
    /// Раздельные «что разобрать» и «какая разбивка по состояниям» означали бы ДВА полных
    /// обхода кеша на каждый заход в раздел — на двадцати тысячах документов это полсотни
    /// запросов пачками, сделанных дважды подряд ради одних и тех же строк. Поэтому обход один,
    /// а <see cref="Pending"/> — обертка над ним.
    ///
    /// Файл, признанный неподдержанным или битым, в <c>Pending</c> не попадает, пока сам не
    /// изменится: иначе очередь каждый раз перемалывала бы одни и те же сканы.
    /// </summary>
    public (List<string> Pending, DocCensus Census) Survey(IReadOnlyCollection<string> paths)
    {
        var known   = Known([.. paths.Select(KeyOf)]);
        var pending = new List<string>();

        int total = 0, read = 0, notRead = 0, noText = 0, unsupported = 0, failed = 0;

        foreach (var raw in paths)
        {
            var key = KeyOf(raw);
            var (mtime, size) = Stamp(key);
            if (size < 0) continue;                        // файла нет на диске — он не документ
            total++;

            // Нет в кеше или изменился — разбирать. Изменившийся считается непрочитанным даже
            // если прошлый разбор удался: в кеше лежит текст прежней редакции.
            if (!known.TryGetValue(key, out var e) || e.Mtime != mtime || e.Size != size)
            {
                pending.Add(raw);
                notRead++;
                continue;
            }

            switch (e.Status)
            {
                case DocTextStatus.Ok:          read++;        break;
                case DocTextStatus.NoText:      noText++;      break;
                case DocTextStatus.Unsupported: unsupported++; break;
                default:                        failed++;      break;
            }
        }

        return (pending, new DocCensus(total, read, notRead, noText, unsupported, failed));
    }

    /// <summary>Время правки и размер файла; (0, −1) — файла нет или он недоступен.</summary>
    public static (long Mtime, long Size) Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc.Ticks, info.Length) : (0, -1);
        }
        catch { return (0, -1); }
    }

    // ── Запись ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Кладет разобранный документ в кеш (или обновляет его). Текст пишется только для
    /// <see cref="DocTextStatus.Ok"/>: у остальных исходов хранить нечего, а строка о самом
    /// файле нужна — она и есть память «этот больше не трогаем».
    /// </summary>
    public void Put(string path, DocText text)
    {
        var key = KeyOf(path);
        var (mtime, size) = Stamp(key);

        lock (_lock)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();

            // Старый текст выносим явно: FTS5 не знает про UPSERT основной таблицы, и без
            // удаления в индексе осталась бы прежняя редакция документа.
            using (var del = connection.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = """
                    DELETE FROM doc_fts WHERE rowid IN (SELECT Id FROM doc_files WHERE PathKey = @k);
                    DELETE FROM doc_files WHERE PathKey = @k;
                    """;
                del.Parameters.AddWithValue("@k", key);
                del.ExecuteNonQuery();
            }

            long id;
            using (var ins = connection.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO doc_files (PathKey, Mtime, Size, Status, Chars, Pages, IndexedAt)
                    VALUES (@k, @m, @s, @st, @c, @p, @at);
                    SELECT last_insert_rowid();
                    """;
                ins.Parameters.AddWithValue("@k",  key);
                ins.Parameters.AddWithValue("@m",  mtime);
                ins.Parameters.AddWithValue("@s",  size);
                ins.Parameters.AddWithValue("@st", (int)text.Status);
                ins.Parameters.AddWithValue("@c",  text.Text.Length);
                ins.Parameters.AddWithValue("@p",  text.Pages);
                ins.Parameters.AddWithValue("@at", DateTime.UtcNow.ToString("O"));
                id = Convert.ToInt64(ins.ExecuteScalar() ?? 0L);
            }

            if (text.Status == DocTextStatus.Ok && text.Text.Length > 0 && id > 0)
            {
                using var fts = connection.CreateCommand();
                fts.Transaction = tx;
                fts.CommandText = "INSERT INTO doc_fts (rowid, Text) VALUES (@id, @t)";
                fts.Parameters.AddWithValue("@id", id);
                fts.Parameters.AddWithValue("@t",  text.Text);
                fts.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    /// <summary>Убирает из кеша все, кроме перечисленных путей — уборка после удаления коллекций.</summary>
    public int Forget(IReadOnlyCollection<string> keep)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();

            using var temp = connection.CreateCommand();
            temp.Transaction = tx;
            temp.CommandText = "CREATE TEMP TABLE IF NOT EXISTS _keep(PathKey TEXT PRIMARY KEY)";
            temp.ExecuteNonQuery();

            foreach (var chunk in Chunks([.. keep.Select(KeyOf)], 400))
            {
                using var ins = connection.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT OR IGNORE INTO _keep(PathKey) VALUES " +
                                  string.Join(",", chunk.Select((_, i) => $"(@p{i})"));
                for (var i = 0; i < chunk.Count; i++) ins.Parameters.AddWithValue("@p" + i, chunk[i]);
                ins.ExecuteNonQuery();
            }

            using var del = connection.CreateCommand();
            del.Transaction = tx;
            del.CommandText = """
                DELETE FROM doc_fts WHERE rowid IN
                    (SELECT Id FROM doc_files WHERE PathKey NOT IN (SELECT PathKey FROM _keep));
                DELETE FROM doc_files WHERE PathKey NOT IN (SELECT PathKey FROM _keep);
                DROP TABLE IF EXISTS _keep;
                """;
            var removed = del.ExecuteNonQuery();
            tx.Commit();
            return removed;
        }
    }

    /// <summary>Полностью очищает кеш — кнопка «Очистить индекс» на экране настроек.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM doc_fts; DELETE FROM doc_files; VACUUM;";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Сводка кеша для экрана настроек: сколько документов разобрано, сколько без текста,
    /// сколько весит файл.</summary>
    public (int Indexed, int NoText, int Failed, long Bytes) Stats()
    {
        lock (_lock)
        {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT SUM(CASE WHEN Status = 0 THEN 1 ELSE 0 END),
                       SUM(CASE WHEN Status = 1 THEN 1 ELSE 0 END),
                       SUM(CASE WHEN Status IN (2,3,4) THEN 1 ELSE 0 END)
                FROM doc_files
                """;
            using var rdr = cmd.ExecuteReader();
            var (ok, noText, failed) = rdr.Read()
                ? (rdr.IsDBNull(0) ? 0 : rdr.GetInt32(0),
                   rdr.IsDBNull(1) ? 0 : rdr.GetInt32(1),
                   rdr.IsDBNull(2) ? 0 : rdr.GetInt32(2))
                : (0, 0, 0);

            long bytes;
            try { bytes = new FileInfo(_path).Length; } catch { bytes = 0; }
            return (ok, noText, failed, bytes);
        }
    }

    // ── Поиск ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ищет запрос в текстах документов. Возвращает пути с фрагментами вокруг совпадения,
    /// отсортированные по релевантности FTS5 (<c>ORDER BY rank</c>), не больше
    /// <paramref name="limit"/>.
    /// </summary>
    public List<DocHit> Search(string query, int limit = 500, int words = SnippetWordsDefault)
    {
        // Границы — от того же мусора, что и у лимита: ноль слов дал бы пустые цитаты, а
        // сотни — строку, которую никто не читает.
        words = Math.Clamp(words, 4, 64);

        var result = new List<DocHit>();
        var match  = BuildMatch(query);
        if (match.Length == 0) return result;

        lock (_lock)
        {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT f.PathKey, snippet(doc_fts, 0, char({(int)HitOpen}), char({(int)HitClose}), '…', {words})
                FROM doc_fts
                JOIN doc_files f ON f.Id = doc_fts.rowid
                WHERE doc_fts MATCH @q
                ORDER BY rank
                LIMIT @lim
                """;
            cmd.Parameters.AddWithValue("@q",   match);
            cmd.Parameters.AddWithValue("@lim", Math.Max(1, limit));

            try
            {
                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                    result.Add(new DocHit(rdr.GetString(0), rdr.IsDBNull(1) ? "" : rdr.GetString(1)));
            }
            catch (SqliteException)
            {
                // Синтаксическая ошибка выражения FTS (пользователь ввел что-то, что не пережил
                // экранирование) — это пустая выдача, а не сбой поиска.
            }
        }
        return result;
    }

    /// <summary>Фоновый поиск: файл кеша читается с диска, UI-поток ждать не должен.</summary>
    public Task<List<DocHit>> SearchAsync(string query, int limit = 500,
                                          int words = SnippetWordsDefault) =>
        Task.Run(() => Search(query, limit, words));

    /// <summary>
    /// Собирает выражение MATCH из пользовательского запроса. Каждое слово берется в двойные
    /// кавычки (внутренние удваиваются) — так спецсимволы синтаксиса FTS5 (<c>* : ^ - NEAR OR</c>)
    /// перестают быть синтаксисом и ищутся как текст. Последнему слову приписывается <c>*</c>:
    /// человек, набравший «автоматизир», ожидает найти «автоматизированных».
    /// Пустой или бессловесный запрос дает пустую строку — поиск не выполняется.
    /// </summary>
    public static string BuildMatch(string? query)
    {
        var tokens = (query ?? "")
            .Split([' ', '\t', '\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToList();
        if (tokens.Count == 0) return "";

        var parts = new List<string>(tokens.Count);
        for (var i = 0; i < tokens.Count; i++)
        {
            var quoted = "\"" + tokens[i].Replace("\"", "\"\"") + "\"";
            parts.Add(i == tokens.Count - 1 ? quoted + "*" : quoted);
        }
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Разбирает сниппет на куски «обычный текст / совпадение» в порядке следования.
    /// Карточка рисует по этому списку: обычные куски — как есть, совпадения — с подсветкой.
    /// </summary>
    public static List<(string Text, bool Hit)> SplitSnippet(string? snippet)
    {
        var parts = new List<(string, bool)>();
        if (string.IsNullOrEmpty(snippet)) return parts;

        var hit   = false;
        var start = 0;
        for (var i = 0; i < snippet.Length; i++)
        {
            if (snippet[i] != HitOpen && snippet[i] != HitClose) continue;
            if (i > start) parts.Add((snippet[start..i], hit));
            hit   = snippet[i] == HitOpen;
            start = i + 1;
        }
        if (start < snippet.Length) parts.Add((snippet[start..], hit));
        return parts;
    }

    // Пачки для IN (…): у SQLite ограничено число параметров и глубина выражения, а путей
    // может быть сотня тысяч.
    private static IEnumerable<List<T>> Chunks<T>(IReadOnlyCollection<T> items, int size)
    {
        var chunk = new List<T>(size);
        foreach (var item in items)
        {
            chunk.Add(item);
            if (chunk.Count < size) continue;
            yield return chunk;
            chunk = new List<T>(size);
        }
        if (chunk.Count > 0) yield return chunk;
    }
}
