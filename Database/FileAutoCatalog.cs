using System.Buffers;
using System.IO.Enumeration;
using System.Text;
using Microsoft.Data.Sqlite;
using SmartBase.Core.Models;

namespace SmartBase.Database;

/// <summary>Слишком широкий поиск не записывает обрезанный список вместо полного результата.</summary>
public sealed class FileAutoMatchLimitException() : InvalidOperationException(
    "Слишком много найденных файлов. Уточните значение столбца или папку поиска.");

/// <summary>
/// Неизменяемый снимок имен файлов в локальном временном SQLite. Каталог заполняется потоком,
/// а не списком путей в памяти. Вызывать только в фоне: первый проход и короткая подстрока
/// могут читать миллионы имен. Владелец не удаляет снимок, пока им пользуется другой поток.
/// </summary>
public sealed class FileAutoCatalog : IDisposable
{
    public const int MaxMatches = 10_000;
    public const int MaxCombinedPathChars = 4 * 1024 * 1024;
    private const int BatchSize = 4096;

    private readonly string _path;
    private readonly string _format;
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private FileAutoCatalog(string path, string format, SqliteConnection connection) =>
        (_path, _format, _connection) = (path, format, connection);

    /// <summary>Файл принадлежит только этому снимку; удаляется при Dispose.</summary>
    public string StoragePath => _path;
    /// <summary>Сколько путей перечислено, включая файлы другого формата.</summary>
    public long ScannedCount { get; private set; }
    /// <summary>Сколько путей подходящего формата записано в снимок.</summary>
    public long MatchedFileCount { get; private set; }
    /// <summary>Есть ускоритель подстрок. Его отсутствие меняет скорость, а не результат.</summary>
    public bool HasSubstringIndex { get; private set; }

    public static FileAutoCatalog CreateFromFolder(FileAutoRule rule, CancellationToken ct = default,
                                                   Action<long>? scanned = null) =>
        Build(rule, EnumerateFiles(rule, ct), ct, scanned);

    /// <summary>Источник перечисляется ровно один раз. На ошибке или отмене удаляется только
    /// созданный здесь временный файл; рабочая база программы не открывается.</summary>
    public static FileAutoCatalog Build(FileAutoRule rule, IEnumerable<string> paths,
                                         CancellationToken ct = default, Action<long>? scanned = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(paths);
        if (rule.Format.Length == 0) throw new ArgumentException("Формат файла не задан.", nameof(rule));
        ct.ThrowIfCancellationRequested();

        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"smartbase-file-auto-{Guid.NewGuid():N}.db");
        SqliteConnection? connection = null;
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (new FileStream(path, options)) { }
            connection = SqliteGate.NewConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private, Pooling = false,
            }.ToString());
            connection.Open();
            var catalog = new FileAutoCatalog(path, rule.Format, connection);
            using var cancellation = InterruptOnCancellation(connection, ct);
            catalog.Fill(rule, paths, ct, scanned);
            ct.ThrowIfCancellationRequested();
            return catalog;
        }
        catch (Exception ex)
        {
            connection?.Dispose();
            DeleteOwnFile(path);
            if (ex is SqliteException && ct.IsCancellationRequested)
                throw new OperationCanceledException("Создание каталога файлов отменено.", ex, ct);
            throw;
        }
    }

    private void Fill(FileAutoRule rule, IEnumerable<string> paths, CancellationToken ct, Action<long>? scanned)
    {
        // Каталог одноразовый и до успешного возврата никому не виден. Журнал не нужен:
        // прерванный снимок удаляется. Кеш и временная сортировка не растут вместе с каталогом.
        Execute("""
            PRAGMA journal_mode=OFF;
            PRAGMA synchronous=OFF;
            PRAGMA cache_size=-8192;
            PRAGMA temp_store=FILE;
            PRAGMA mmap_size=0;
            CREATE TABLE file_entries (
                sequence INTEGER PRIMARY KEY,
                name TEXT NOT NULL COLLATE BINARY,
                stem TEXT NOT NULL,
                path TEXT NOT NULL);
            """);
        var suffix = "." + rule.Format;
        using var insert = _connection.CreateCommand();
        insert.CommandText = "INSERT INTO file_entries(name, stem, path) VALUES ($name, $stem, $path)";
        var nameParameter = insert.Parameters.Add("$name", SqliteType.Text);
        var stemParameter = insert.Parameters.Add("$stem", SqliteType.Text);
        var pathParameter = insert.Parameters.Add("$path", SqliteType.Text);
        SqliteTransaction? transaction = null;
        scanned?.Invoke(0);
        try
        {
            transaction = _connection.BeginTransaction();
            insert.Transaction = transaction;
            insert.Prepare();
            using var iterator = paths.GetEnumerator();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!iterator.MoveNext()) break;
                ct.ThrowIfCancellationRequested();
                var path = iterator.Current;
                ArgumentNullException.ThrowIfNull(path);
                ScannedCount++;
                var name = System.IO.Path.GetFileName(path);
                if (name.EndsWith(suffix, StringComparison.Ordinal))
                {
                    nameParameter.Value = name;
                    stemParameter.Value = name[..^suffix.Length];
                    pathParameter.Value = path;
                    insert.ExecuteNonQuery();
                    MatchedFileCount++;
                }
                if (ScannedCount % BatchSize != 0) continue;
                scanned?.Invoke(ScannedCount);
                ct.ThrowIfCancellationRequested();
                transaction.Commit();
                transaction.Dispose();
                transaction = _connection.BeginTransaction();
                insert.Transaction = transaction;
            }
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        finally { transaction?.Dispose(); }

        scanned?.Invoke(ScannedCount);
        ct.ThrowIfCancellationRequested();
        Execute("CREATE INDEX file_entries_name ON file_entries(name COLLATE BINARY)");
        if (!rule.PartialMatch) return;
        try
        {
            // Внешний content не копирует пути и имена во вторую обычную таблицу.
            Execute("""
                CREATE VIRTUAL TABLE file_stems USING fts5(stem, content='file_entries',
                    content_rowid='sequence', tokenize='trigram case_sensitive 1');
                INSERT INTO file_stems(file_stems) VALUES ('rebuild');
                """);
            HasSubstringIndex = true;
        }
        catch (SqliteException ex) when (!ct.IsCancellationRequested && ex.SqliteErrorCode == 1
            && (ex.Message.Contains("no such module: fts5", StringComparison.Ordinal)
                || ex.Message.Contains("no such tokenizer: trigram", StringComparison.Ordinal)
                || ex.Message.Contains("error in tokenizer constructor", StringComparison.Ordinal)))
        {
            // Другая сборка SQLite: подстрока остается доступна потоковым проходом в фоне.
            HasSubstringIndex = false;
        }
    }

    /// <summary>Точный режим возвращает первый перечисленный путь; подстрочный — все разные
    /// пути в Ordinal-порядке. Короткие и неиндексируемые запросы читаются потоком с отменой.
    /// Слишком широкий результат вызывает исключение до сериализации и не обрезается.</summary>
    public string Match(FileAutoRule rule, string? source, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _gate.Wait(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!string.Equals(rule.Format, _format, StringComparison.Ordinal))
                throw new ArgumentException("Формат правила не соответствует каталогу.", nameof(rule));
            if (string.IsNullOrEmpty(source) || rule.PartialMatch && string.IsNullOrWhiteSpace(source)) return "";
            using var cancellation = InterruptOnCancellation(_connection, ct);
            using var command = _connection.CreateCommand();
            if (!rule.PartialMatch)
            {
                command.CommandText = """
                    SELECT path, name FROM file_entries WHERE name = $name COLLATE BINARY
                    ORDER BY sequence LIMIT 1
                    """;
                command.Parameters.AddWithValue("$name", rule.TargetFileName(source));
            }
            else if (HasSubstringIndex && HasTrigram(source))
            {
                command.CommandText = """
                    SELECT f.path, f.name FROM file_stems
                    JOIN file_entries f ON f.sequence = file_stems.rowid
                    WHERE file_stems MATCH $query
                    """;
                command.Parameters.AddWithValue("$query", "\"" + source.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
            }
            else command.CommandText = "SELECT path, name FROM file_entries";

            using var reader = command.ExecuteReader();
            var paths = new HashSet<string>(StringComparer.Ordinal);
            long chars = 0;
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (!rule.MatchesFileName(source, reader.GetString(1))) continue;
                var path = reader.GetString(0);
                if (!rule.PartialMatch) return path;
                if (!paths.Add(path)) continue;
                chars += path.Length;
                if (paths.Count > MaxMatches || chars > MaxCombinedPathChars)
                    throw new FileAutoMatchLimitException();
            }
            ct.ThrowIfCancellationRequested();
            var stored = FileValue.Serialize(paths.Order(StringComparer.Ordinal));
            if (stored.Length > MaxCombinedPathChars) throw new FileAutoMatchLimitException();
            return stored;
        }
        catch (SqliteException ex) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("Поиск файлов отменен.", ex, ct);
        }
        finally { _gate.Release(); }
    }

    // SQLite-триграмма считает символы Unicode, а не половины суррогатной пары UTF-16.
    // Поврежденный UTF-16 и NUL не передаются парсеру FTS: финальная Ordinal-проверка в
    // потоковом запасном пути сохраняет буквальную семантику MatchesFileName.
    private static bool HasTrigram(string text)
    {
        if (text.Contains('\0')) return false;
        var remaining = text.AsSpan();
        var count = 0;
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != OperationStatus.Done) return false;
            remaining = remaining[consumed..];
            count++;
        }
        return count >= 3;
    }

    // Глубинный обход держит один открытый перечислитель на уровень, а не список всех
    // подпапок. ReparsePoint пропускается: ссылки на родителей не превращают скан в цикл.
    private static IEnumerable<string> EnumerateFiles(FileAutoRule rule, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var stack = new Stack<IEnumerator<CatalogEntry>>();
        try
        {
            stack.Push(Entries(rule.Folder).GetEnumerator());
            while (stack.TryPeek(out var iterator))
            {
                ct.ThrowIfCancellationRequested();
                if (!iterator.MoveNext()) { stack.Pop().Dispose(); continue; }
                ct.ThrowIfCancellationRequested();
                var item = iterator.Current;
                if (!item.Directory) yield return item.Path;
                else if (rule.Recursive) stack.Push(Entries(item.Path).GetEnumerator());
            }
        }
        finally { while (stack.TryPop(out var iterator)) iterator.Dispose(); }
    }

    private readonly record struct CatalogEntry(string Path, bool Directory);

    private static IEnumerable<CatalogEntry> Entries(string folder) => new FileSystemEnumerable<CatalogEntry>(
        folder, (ref FileSystemEntry entry) => new(entry.ToFullPath(), entry.IsDirectory),
        new EnumerationOptions
        {
            RecurseSubdirectories = false, IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false,
        });

    private static CancellationTokenRegistration InterruptOnCancellation(SqliteConnection connection,
        CancellationToken ct) => ct.Register(() =>
        {
            if (connection.Handle is { } handle) SQLitePCL.raw.sqlite3_interrupt(handle);
        });

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _connection.Dispose();
            DeleteOwnFile(_path);
        }
        finally { _gate.Release(); }
    }

    private static void DeleteOwnFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { /* свой временный файл занят; последующая очистка временной папки подберет его */ }
    }
}
