using System.IO;
using Microsoft.Data.Sqlite;
using SmartBase.Core.Documents;
using SmartBase.Core.Models;

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
/// <param name="Revision">Какой редакцией разбора прочитан (<c>DocTextExtractor.RevisionOf</c>).</param>
public readonly record struct DocIndexEntry(long Mtime, long Size, DocTextStatus Status, int Pages,
                                            int Revision = 0);

/// <summary>
/// Перепись документов по состоянию текста — из нее собираются все числа подвала блока поиска.
/// </summary>
/// <param name="Total">Сколько документов в охвате (файлов, существующих на диске).</param>
/// <param name="Read">Текст прочитан — поиск по содержимому работает.</param>
/// <param name="NotRead">Не разобраны, изменились с прошлого разбора или прочитаны прежней
/// редакцией разбора.</param>
/// <param name="NoText">Сканы без текстового слоя — перечитывание не поможет.</param>
/// <param name="Unsupported">Форматы без извлечения текста — ищутся только по названию.</param>
/// <param name="Failed">Не удалось прочитать: битый, занят или тяжелее порога.</param>
public readonly record struct DocCensus(
    int Total, int Read, int NotRead, int NoText, int Unsupported, int Failed)
{
    /// <summary>Внутри скольких документов поиск искать не будет, сколько ни читай.</summary>
    public int NeverSearchable => NoText + Unsupported + Failed;

    /// <summary>
    /// Числа по состояниям документов — те же, что дала бы перепись этих путей
    /// (<see cref="DocTextIndex.Survey"/>): в счет идут только файлы, найденные на диске. Так числа
    /// подвала пересчитываются из карты состояний, не спрашивая диск заново, — легкое
    /// перечитывание ленты по чужой правке спрашивает его только о новых путях.
    /// </summary>
    public static DocCensus Of(IEnumerable<DocFileState> files)
    {
        int total = 0, read = 0, notRead = 0, noText = 0, unsupported = 0, failed = 0;
        foreach (var file in files)
        {
            if (!file.OnDisk) continue;
            total++;
            switch (file.State)
            {
                case DocTextState.Read:        read++;        break;
                case DocTextState.NotRead:     notRead++;     break;
                case DocTextState.NoText:      noText++;      break;
                case DocTextState.Unsupported: unsupported++; break;
                default:                       failed++;      break;
            }
        }
        return new DocCensus(total, read, notRead, noText, unsupported, failed);
    }
}

/// <summary>
/// Сводка кеша для экрана настроек — по исходам разбора, каждый отдельно. Раньше чужие форматы,
/// битые файлы и файлы тяжелее порога складывались в одно число, и по нему нельзя было понять,
/// поможет ли поднять порог — а задается он в соседней выпадашке.
/// </summary>
/// <param name="Indexed">Текст прочитан.</param>
/// <param name="NoText">Сканы без текстового слоя.</param>
/// <param name="Unsupported">Форматы без чтения текста.</param>
/// <param name="Failed">Не удалось прочитать: битый, занят, нет прав.</param>
/// <param name="TooBig">Тяжелее порога — не разбирались.</param>
/// <param name="Bytes">Размер файла кеша.</param>
public readonly record struct DocCacheStats(int Indexed, int NoText, int Unsupported, int Failed,
                                           int TooBig, long Bytes);

/// <summary>Что сказать про текст одного документа на его карточке.</summary>
/// <param name="State">Состояние текста — им подписан серый чип.</param>
/// <param name="Pages">Число страниц из кеша; 0 — неизвестно (есть только у прочитанных).</param>
public readonly record struct DocFileState(DocTextState State, int Pages)
{
    /// <summary>
    /// Файл нашелся на диске. В числа подвала идут только такие, и по этому признаку
    /// <see cref="DocCensus.Of"/> пересчитывает их из карты состояний без нового обхода диска.
    /// </summary>
    public bool OnDisk { get; init; } = true;
}

/// <summary>
/// Итог одной переписи: что разобрать, сколько документов в каком состоянии и что сказать про
/// каждый из них. Все три ответа — из ОДНОГО прохода по кешу и диску, поэтому числа подвала и
/// чипы карточек не расходятся между собой.
/// </summary>
/// <param name="Pending">Пути, которые надо разобрать, — в написании зовущего.</param>
/// <param name="Census">Разбивка по состояниям — числа подвала.</param>
/// <param name="Files">
/// Ключ пути (<see cref="DocTextIndex.KeyOf"/>) → состояние для карточки. Есть у КАЖДОГО
/// спрошенного пути, в том числе у файла, которого на диске нет.
/// </param>
public sealed record DocSurvey(List<string> Pending, DocCensus Census,
                               IReadOnlyDictionary<string, DocFileState> Files)
{
    /// <summary>Разбор на пару — там, где состояния карточек не нужны.</summary>
    public void Deconstruct(out List<string> pending, out DocCensus census) =>
        (pending, census) = (Pending, Census);
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
        try { Ensure(); }
        catch (Exception ex)
        {
            // Файл кеша поврежден (обрыв питания на записи, недокачанная копия) — сносим и
            // начинаем заново. Кеш пересобираемый: единственная плата — прочитать документы
            // еще раз, а без этого поиск по содержимому пропал бы до переустановки программы.
            // Не вышло и так — исключение уходит наружу: значит дело не в файле, а в папке.
            Serilog.Log.Warning(ex, "Кеш документов поврежден — файл пересоздан: {Path:l}", _path);
            Recreate();
        }
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

        // Колонки Pages и Revision добавлены позже. EF-миграций у кеша нет и быть не должно — он
        // пересобираемый файл, — поэтому догоняем старые кеши руками. Ошибку глотаем: у свежего
        // файла колонка уже есть, и SQLite на это честно ругается «duplicate column name».
        //
        // Revision старых строк — 0: прочитаны первой редакцией разбора. Формат, разбор которого
        // с тех пор поднял редакцию, перепись перечитает (<see cref="Survey"/>).
        foreach (var column in new[] { "Pages", "Revision" })
            try
            {
                using var add = connection.CreateCommand();
                add.CommandText = $"ALTER TABLE doc_files ADD COLUMN {column} INTEGER NOT NULL DEFAULT 0";
                add.ExecuteNonQuery();
            }
            catch (SqliteException) { /* колонка уже есть */ }
    }

    // Снести файл кеша и создать пустой. Зовется на поврежденном файле — из конструктора и из
    // <see cref="Clear"/>.
    private void Recreate()
    {
        // Пул держит файловый дескриптор и после Dispose соединения — без сброса удалить файл
        // не дала бы сама же программа. Сбрасываем только СВОЙ пул (он привязан к строке
        // подключения): общий ClearAllPools задел бы и app.db, и журналы.
        try
        {
            using var probe = new SqliteConnection(ConnectionString);
            SqliteConnection.ClearPool(probe);
        }
        catch { /* пула нет — тем лучше */ }

        // Вместе с файлом уходят WAL и разделяемая память: оставшийся WAL от прежнего файла
        // SQLite посчитал бы своим и восстановил бы из него ту же поломку.
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_path + suffix); }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Кеш документов: файл не удален: {Path:l}", _path + suffix);
            }

        Ensure();
    }

    private string ConnectionString => $"Data Source={_path}";

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(ConnectionString);
        try
        {
            connection.Open();

            using var pragma = connection.CreateCommand();
            // WAL — по той же причине, что у остальных файлов: кеш пишется фоновой очередью, а
            // читается поиском, и блокировать одно другим незачем.
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();

            return connection;
        }
        catch
        {
            // Сам файл открылся, а споткнулась уже PRAGMA — так ведет себя поврежденный кеш.
            // Без этого открытое соединение утекало бы: дескриптор висит, и файл потом нечем
            // ни удалить, ни пересоздать.
            connection.Dispose();
            throw;
        }
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

    /// <summary>
    /// Написания пути, по которым его ищут в <c>db_field_values</c>. Ключ кеша нормализован
    /// (<see cref="KeyOf"/>), а в поле записи путь лежит КАК ЕГО ЗАПИСАЛИ: тот же файл там
    /// может стоять с прямыми слешами — так его пишут импорт, вставка из буфера и правка
    /// значения руками.
    ///
    /// Сравнение в SQL идет по значению поля, и одного ключа мало: найденный по тексту
    /// документ молча не сошелся бы с записью, и человек увидел бы «ничего не найдено» ровно
    /// на том документе, который поиск нашел. Ошибка тихая — ни сообщения, ни следа в журнале.
    ///
    /// Вариант пишется только там, где разделитель меняет <c>GetFullPath</c>: на Windows
    /// прямой слеш становится обратным, а на Unix обратный слеш — обычный знак имени файла, и
    /// подменять его нельзя, это был бы уже другой файл.
    /// </summary>
    public static IReadOnlyList<string> Spellings(string key)
    {
        if (string.IsNullOrEmpty(key)) return [];
        if (System.IO.Path.DirectorySeparatorChar != '\\' || !key.Contains('\\')) return [key];

        var slashed = key.Replace('\\', '/');
        return slashed == key ? [key] : [key, slashed];
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
                cmd.CommandText = $"SELECT PathKey, Mtime, Size, Status, Pages, Revision FROM doc_files WHERE PathKey IN ({holes})";
                for (var i = 0; i < chunk.Count; i++) cmd.Parameters.AddWithValue("@p" + i, chunk[i]);

                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                    result[rdr.GetString(0)] = new DocIndexEntry(
                        rdr.GetInt64(1), rdr.GetInt64(2), (DocTextStatus)rdr.GetInt32(3), rdr.GetInt32(4),
                        rdr.GetInt32(5));
            }
        }
        return result;
    }

    /// <summary>
    /// Пути, которые надо разобрать: которых в кеше нет либо у которых изменились время правки
    /// или размер. Файл, признанный неподдержанным или битым, повторно не тревожится, пока сам
    /// не изменится, — иначе очередь каждый раз перемалывала бы одни и те же сканы.
    /// </summary>
    /// <param name="maxFileMb">Порог размера, с которым будут читать (см. <see cref="Survey"/>).</param>
    public List<string> Pending(IReadOnlyCollection<string> paths,
                                int maxFileMb = DocTextExtractor.DefaultMaxFileMb) =>
        Survey(paths, maxFileMb).Pending;

    /// <summary>
    /// Один проход по набору путей: что надо разобрать, сколько документов в каком состоянии и
    /// что сказать про каждый на его карточке.
    ///
    /// Раздельные ответы означали бы несколько полных обходов кеша и диска на каждый заход в
    /// раздел ради одних и тех же строк. Так и было: контроллер после переписи отдельно
    /// перечитывал кеш ради чипов, а чип каждой карточки еще раз спрашивал у диска время правки
    /// своего файла — в UI-потоке. Поэтому обход один, а <see cref="Pending"/> — обертка над ним.
    ///
    /// Диск спрашивается набором (<see cref="DocStamps"/>) — папками, а не по файлу: на общей
    /// папке сервера это разница между секундами и десятками секунд на каждый заход в раздел.
    ///
    /// Файл, признанный неподдержанным или битым, в <c>Pending</c> не попадает, пока сам не
    /// изменится: иначе очередь каждый раз перемалывала бы одни и те же сканы.
    ///
    /// Прочитанный прежней редакцией разбора (<c>DocTextExtractor.RevisionOf</c>) считается
    /// измененным: файл тот же, а текст, который из него извлекается, — уже другой. Поиск до
    /// перечитывания находит такой документ по прежнему тексту — слова в нем те же.
    ///
    /// Отложенный порогом (<see cref="DocTextStatus.TooBig"/>) читается заново, как только порог
    /// его пропускает: человек поднял порог — значит, хочет эти файлы прочитать. Раньше отметка
    /// «тяжелее порога» держалась до правки самого файла, и поднятый порог на уже встреченные
    /// файлы не действовал вовсе, хотя сводка кеша прямо предлагает его поднять.
    /// </summary>
    /// <param name="maxFileMb">Порог размера, с которым будут читать; 0 — без порога.</param>
    public DocSurvey Survey(IReadOnlyCollection<string> paths,
                            int maxFileMb = DocTextExtractor.DefaultMaxFileMb)
    {
        var keys    = paths.Select(KeyOf).ToList();
        var known   = Known(keys);
        var stamps  = DocStamps.Of(keys);
        var pending = new List<string>();
        var files   = new Dictionary<string, DocFileState>(StringComparer.Ordinal);

        int total = 0, read = 0, notRead = 0, noText = 0, unsupported = 0, failed = 0;

        var i = 0;
        foreach (var raw in paths)
        {
            var key = keys[i++];
            var (mtime, size) = stamps[key];

            // Нет в кеше или изменился — разбирать. Изменившийся считается непрочитанным даже
            // если прошлый разбор удался: в кеше лежит текст прежней редакции. У файла, которого
            // нет на диске, сверять нечего — ему остается то, что помнит кеш.
            var has     = known.TryGetValue(key, out var e);
            var changed = !has || (size >= 0 && (e.Mtime != mtime || e.Size != size ||
                                                 e.Revision < DocTextExtractor.RevisionOf(key) ||
                                                 (e.Status == DocTextStatus.TooBig &&
                                                  !DocTextExtractor.TooBig(size, maxFileMb))));

            files[key] = (changed
                ? new DocFileState(DocTextState.NotRead, 0)
                : new DocFileState(StateOf(e.Status), e.Pages)) with { OnDisk = size >= 0 };

            if (size < 0) continue;                        // файла нет на диске — он не документ
            total++;

            if (changed)
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

        return new DocSurvey(pending,
                             new DocCensus(total, read, notRead, noText, unsupported, failed),
                             files);
    }

    // Исход разбора → подпись чипа. Неудача любого рода — «не удалось прочитать».
    private static DocTextState StateOf(DocTextStatus status) => status switch
    {
        DocTextStatus.Ok          => DocTextState.Read,
        DocTextStatus.NoText      => DocTextState.NoText,
        DocTextStatus.Unsupported => DocTextState.Unsupported,
        _                         => DocTextState.Failed,
    };

    // ── Запись ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Кладет разобранный документ в кеш (или обновляет его). Текст пишется только для
    /// <see cref="DocTextStatus.Ok"/>: у остальных исходов хранить нечего, а строка о самом
    /// файле нужна — она и есть память «этот больше не трогаем».
    /// </summary>
    public void Put(string path, DocText text)
    {
        var key = KeyOf(path);
        var (mtime, size) = DocStamps.Of(key);

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
                    INSERT INTO doc_files (PathKey, Mtime, Size, Status, Chars, Pages, Revision, IndexedAt)
                    VALUES (@k, @m, @s, @st, @c, @p, @r, @at);
                    SELECT last_insert_rowid();
                    """;
                ins.Parameters.AddWithValue("@k",  key);
                ins.Parameters.AddWithValue("@m",  mtime);
                ins.Parameters.AddWithValue("@s",  size);
                ins.Parameters.AddWithValue("@st", (int)text.Status);
                ins.Parameters.AddWithValue("@c",  text.Text.Length);
                ins.Parameters.AddWithValue("@p",  text.Pages);
                ins.Parameters.AddWithValue("@r",  DocTextExtractor.RevisionOf(key));
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

    /// <summary>
    /// Уборка сирот: строки кеша, чьих файлов на диске больше нет. Возвращает, сколько убрано.
    ///
    /// Ключ кеша — путь, поэтому удаление и переименование документа оставляют строку с текстом
    /// навсегда: <see cref="Survey"/> спрашивает только про пути ЖИВЫХ коллекций, и про
    /// исчезнувший файл никто больше не заговорит. На папке, которую год перекладывают, такие сироты и есть
    /// основной вес кеша.
    ///
    /// <b>Папку, которой нет, уборка не трогает.</b> Библиотека обычно живет на общей папке
    /// сервера, а он выключается: спроси мы у диска «есть ли файл» при отвалившейся папке —
    /// «нет» ответили бы ВСЕ документы, и уборка снесла бы часы чтения. Поэтому сначала
    /// проверяется папка, и живой она считается, только если диск прямо сказал «есть». Тот же
    /// ответ спасает от снятой галочки коллекции и от отключенной базы: пока файлы лежат на
    /// месте, их текст в кеше остается.
    ///
    /// Убирать по СПИСКУ ЖИВЫХ документов (было бы естественно: «оставить только эти») нельзя
    /// по той же причине — снятая галочка коллекции или отключенная база означала бы часы
    /// перечитывания при возврате.
    /// </summary>
    public int Sweep()
    {
        var keys = AllKeys();
        if (keys.Count == 0) return 0;

        // Только пути из папок, которые диск подтвердил. Все остальное — не наше дело: папки
        // может не быть вовсе, а может не быть только сейчас, и различить это нельзя.
        var live = keys
            .GroupBy(FolderOf, StringComparer.Ordinal)
            .Where(g => g.Key.Length > 0 && DirectoryAlive(g.Key))
            .SelectMany(g => g)
            .ToList();
        if (live.Count == 0) return 0;

        var stamps = DocStamps.Of(live);
        var gone   = live.Where(k => stamps[k].Size < 0).ToList();
        return gone.Count == 0 ? 0 : Drop(gone);
    }

    /// <summary>
    /// Обслуживание FTS-индекса: слить сегменты, накопленные массовыми вставками и удалениями.
    ///
    /// Каждый разобранный документ — это <c>DELETE</c> и <c>INSERT</c> в индексе, а прогон их
    /// дает десятки тысяч. FTS5 сливает сегменты сам, но лениво, и без слияния поиск идет по
    /// длинной лестнице мелких сегментов. Работа ограничена страницами: полный <c>optimize</c>
    /// на индексе в гигабайты — это минуты, а зовется это в фоне, между делом.
    /// </summary>
    public void Maintain(int pages = 64)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            // Команды FTS5 подаются вставкой в одноименную колонку, а их аргумент — в rank
            // (у FTS4 это было 'merge=N,M' одной строкой, и такая строка здесь дает «SQL logic
            // error»).
            cmd.CommandText = "INSERT INTO doc_fts(doc_fts, rank) VALUES('merge', @n)";
            cmd.Parameters.AddWithValue("@n", Math.Max(1, pages));
            cmd.ExecuteNonQuery();
        }
    }

    // Все ключи кеша. Для уборки нужны именно ВСЕ: выбери мы первые N, уборка каждый раз
    // ходила бы по одному и тому же началу, а хвост копил бы сирот вечно.
    private List<string> AllKeys()
    {
        var keys = new List<string>();
        lock (_lock)
        {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT PathKey FROM doc_files";
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read()) keys.Add(rdr.GetString(0));
        }
        return keys;
    }

    // Выбросить перечисленные пути вместе с их текстом.
    private int Drop(IReadOnlyCollection<string> keys)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            var removed = 0;

            foreach (var chunk in Chunks(keys, 400))
            {
                var holes = string.Join(",", chunk.Select((_, i) => "@p" + i));
                using var del = connection.CreateCommand();
                del.Transaction = tx;
                del.CommandText = $"""
                    DELETE FROM doc_fts WHERE rowid IN
                        (SELECT Id FROM doc_files WHERE PathKey IN ({holes}));
                    DELETE FROM doc_files WHERE PathKey IN ({holes});
                    """;
                for (var i = 0; i < chunk.Count; i++) del.Parameters.AddWithValue("@p" + i, chunk[i]);
                del.ExecuteNonQuery();
                removed += chunk.Count;
            }

            tx.Commit();
            return removed;
        }
    }

    private static string FolderOf(string key)
    {
        try { return System.IO.Path.GetDirectoryName(key) ?? ""; }
        catch { return ""; }
    }

    // Папка, про которую диск сказал «есть». Отказ любого рода — «не знаю»: уборка тогда эту
    // папку не трогает.
    private static bool DirectoryAlive(string folder)
    {
        try { return Directory.Exists(folder); }
        catch { return false; }
    }

    /// <summary>
    /// Полностью очищает кеш — кнопка «Очистить» на экране настроек.
    ///
    /// Ходит на диск и сжимает файл (<c>VACUUM</c> на кеше в гигабайты — это секунды), поэтому
    /// зовется из фона, а не из обработчика кнопки. Очередь к этому времени должна быть
    /// остановлена и дождана (<c>DocIndexService.StopAsync</c>): иначе разобранный до остановки
    /// документ лег бы в кеш уже после очистки.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            try
            {
                using var connection = Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "DELETE FROM doc_fts; DELETE FROM doc_files; VACUUM;";
                cmd.ExecuteNonQuery();
                return;
            }
            catch (Exception ex)
            {
                // Битый файл запросом не очистить — и DELETE, и VACUUM спотыкаются о ту же
                // поломку. Итог человеку нужен один и тот же: пустой кеш, — поэтому файл
                // пересоздается. Именно «Очистить» и есть то место, где человек просит починить.
                Serilog.Log.Warning(ex, "Кеш документов не очистился запросом — файл пересоздан: {Path:l}", _path);
            }

            Recreate();
        }
    }

    /// <summary>
    /// Сводка кеша для экрана настроек: сколько документов в каком исходе разбора и сколько весит
    /// файл (<see cref="DocCacheStats"/>).
    /// </summary>
    public DocCacheStats Stats()
    {
        lock (_lock)
        {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT SUM(CASE WHEN Status = {(int)DocTextStatus.Ok}          THEN 1 ELSE 0 END),
                       SUM(CASE WHEN Status = {(int)DocTextStatus.NoText}      THEN 1 ELSE 0 END),
                       SUM(CASE WHEN Status = {(int)DocTextStatus.Unsupported} THEN 1 ELSE 0 END),
                       SUM(CASE WHEN Status = {(int)DocTextStatus.Failed}      THEN 1 ELSE 0 END),
                       SUM(CASE WHEN Status = {(int)DocTextStatus.TooBig}      THEN 1 ELSE 0 END)
                FROM doc_files
                """;
            using var rdr = cmd.ExecuteReader();
            int Count(int i) => rdr.IsDBNull(i) ? 0 : rdr.GetInt32(i);
            var read = rdr.Read();

            long bytes;
            try { bytes = new FileInfo(_path).Length; } catch { bytes = 0; }
            return read
                ? new DocCacheStats(Count(0), Count(1), Count(2), Count(3), Count(4), bytes)
                : new DocCacheStats(0, 0, 0, 0, 0, bytes);
        }
    }

    // ── Поиск ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ищет запрос в текстах документов. Возвращает пути с фрагментами вокруг совпадения,
    /// отсортированные по релевантности FTS5 (<c>ORDER BY rank</c>), не больше
    /// <paramref name="limit"/>.
    ///
    /// Сбой уходит исключением, а не пустой выдачей: «ничего не нашлось» и «поиск не сработал»
    /// — разные ответы человеку, и различать их обязан зовущий. Раньше ошибка запроса глоталась,
    /// и при испорченном кеше раздел честно писал «ничего не найдено». Набранный текст ошибкой
    /// стать не может: каждое слово идет фразой в кавычках (<see cref="BuildMatch"/>), — так что
    /// исключение здесь означает кеш (испорчен, занят, пропал) или наш дефект в сборке выражения.
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

            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
                result.Add(new DocHit(rdr.GetString(0), rdr.IsDBNull(1) ? "" : rdr.GetString(1)));
        }
        return result;
    }

    /// <summary>Фоновый поиск: файл кеша читается с диска, UI-поток ждать не должен.</summary>
    public Task<List<DocHit>> SearchAsync(string query, int limit = 500,
                                          int words = SnippetWordsDefault) =>
        Task.Run(() => Search(query, limit, words));

    /// <summary>
    /// Собирает выражение MATCH из пользовательского запроса. Каждое слово идет фразой в
    /// двойных кавычках (внутренние удваиваются) — так спецсимволы синтаксиса FTS5
    /// (<c>* : ^ - NEAR OR</c>) перестают быть синтаксисом. Последнему слову приписывается
    /// <c>*</c>: человек, набравший «автоматизир», ожидает найти «автоматизированных».
    /// Пустой или бессловесный запрос дает пустую строку — поиск не выполняется.
    ///
    /// Запрос режется на слова НЕ здесь, а в <see cref="DocHighlight.Tokens"/>, а слово — на
    /// части в <see cref="DocHighlight.Variants"/>: по этим же частям панель чтения подсвечивает
    /// найденное в документе, и второй разбор запроса означал бы «нашлось, но не подсвечено»
    /// при первом же расхождении. Фраза собирается из сложенных частей, а не из набранного
    /// слова: «ГОСТ-34.11» — это «гост 34 11», для токенизатора одно и то же. У слова бывает
    /// несколько вариантов — число «30772» ищется и как «30 772», так его показывает книга, а
    /// каждое «е» — и как «ё» (<see cref="YoSpellings"/>), — и варианты идут скобкой через OR.
    ///
    /// Слова соединяются ЯВНЫМ AND: у FTS5 неявное «и» связывает только фразы, а скобку с
    /// вариантами — нет. Слово из одной пунктуации («++») в выражение не идет вовсе: частей у
    /// него нет, неявное «и» такую пустую фразу пропускало, а явный AND с ней не находит
    /// ничего — «гост ++» перестал бы находить «гост». Подсветка его тоже пропускает.
    /// </summary>
    public static string BuildMatch(string? query)
    {
        var tokens = DocHighlight.Tokens(query);
        if (tokens.Count == 0) return "";

        var parts = new List<string>(tokens.Count);
        for (var i = 0; i < tokens.Count; i++)
        {
            var star = i == tokens.Count - 1 ? "*" : "";
            var alternatives = DocHighlight.Variants(tokens[i])
                .SelectMany(YoSpellings)
                .Select(v => Quote(string.Join(' ', v)) + star)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (alternatives.Count == 0) continue;

            parts.Add(alternatives.Count == 1
                ? alternatives[0]
                : "(" + string.Join(" OR ", alternatives) + ")");
        }
        return string.Join(" AND ", parts);
    }

    /// <summary>Больше скольких написаний через «е» и «ё» одно слово запроса не дает.</summary>
    public const int MaxYoSpellings = 64;

    /// <summary>
    /// Написания слова через «е» и «ё». Токенизатор индекса складывает регистр и снимает
    /// надстрочные знаки с латиницы, но «ё» для него своя буква: «учёт» по «учет» не находился,
    /// и наоборот, а набирают обычно без «ё». Сложить «ё» в самом кеше значило бы перечитать
    /// все библиотеки и показывать в карточках «е» там, где в документе «ё», поэтому уравнивает
    /// запрос: части в нем уже сложены в «е» (<see cref="DocHighlight.Fold"/>), и каждое «е»
    /// ищется и как «ё».
    ///
    /// «Ё» стоит под ударением, а ударение в слове одно — у сложных слов два («трёхмёрный»).
    /// Поэтому перебираются написания с нулем, одной и двумя «ё» — первым идет написание без
    /// «ё» — и не больше <see cref="MaxYoSpellings"/>: слово из одних «е» не раздует выражение.
    /// </summary>
    public static List<string[]> YoSpellings(string[] parts)
    {
        var spots = new List<(int Part, int At)>();
        for (var p = 0; p < parts.Length; p++)
            for (var at = 0; at < parts[p].Length; at++)
                if (parts[p][at] == 'е') spots.Add((p, at));

        var spellings = new List<string[]> { parts };
        string[] With(params (int Part, int At)[] yo)
        {
            var copy = (string[])parts.Clone();
            foreach (var (p, at) in yo)
            {
                var chars = copy[p].ToCharArray();
                chars[at] = 'ё';
                copy[p] = new string(chars);
            }
            return copy;
        }

        for (var a = 0; a < spots.Count && spellings.Count < MaxYoSpellings; a++)
            spellings.Add(With(spots[a]));
        for (var a = 0; a < spots.Count && spellings.Count < MaxYoSpellings; a++)
            for (var b = a + 1; b < spots.Count && spellings.Count < MaxYoSpellings; b++)
                spellings.Add(With(spots[a], spots[b]));
        return spellings;
    }

    // Фраза FTS5: в двойных кавычках, внутренние удвоены. Нулевой знак разбор выражения считает
    // концом строки: кавычка осталась бы незакрытой, и запрос, вставленный из буфера с таким
    // знаком, ронял бы поиск. Для токенизатора (и для подсветки) это разделитель — ставим пробел.
    private static string Quote(string text) => "\"" + text.Replace('\0', ' ').Replace("\"", "\"\"") + "\"";

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
