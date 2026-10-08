using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Общая база в сетевой папке (docs/developer/architecture/database/multi-database.md,
/// «Перезагрузка сервера»): сосед удаляет строки под нашими правками, сервер перезагружают
/// (мертвые хендлы в пуле, BUSY на постоянном соединении поллера), базы одного сервера
/// пропадают вместе, а в старом файле встречается NULL в «Названии».
///
/// Настоящую пропажу шары в тесте не устроить (открытый SQLite-файл на Windows не удалить и
/// не переименовать), поэтому проба монитора подменяется (<see cref="DbHealthMonitor.ProbeOverride"/>),
/// а «сервер» для группировки баз задается <see cref="NetworkPath.ForceServerRoot"/>.
/// </summary>
public sealed class SharedDbResilienceTests
{
    // ── 1. Фантомы трекера: сосед удалил строку, которую мы правим ─────────────

    /// <summary>
    /// Открытый диалог держит поле в трекере общего контекста; сосед поле удалил; мы жмем
    /// «Сохранить». Раньше UPDATE в 0 строк выходил наверх DbUpdateConcurrencyException
    /// и ронял приложение (PropertyEditorDialog.TrySave в crash.log). Теперь строка
    /// отцепляется, сохранение проходит, о выброшенной правке сообщает хук — и удаленное
    /// не воскрешается.
    /// </summary>
    [Fact]
    public void Edit_of_row_deleted_by_neighbour_is_dropped_and_reported()
    {
        using var db  = new TempDatabase();
        var col   = SampleData.AddCollection(db.Constructor);
        var field = SampleData.AddField(db.Constructor, col.Id!.Value, "Поле");

        var dropped = 0;
        var prev    = AppDbContext.PhantomRowsDropped;
        AppDbContext.PhantomRowsDropped = n => Interlocked.Add(ref dropped, n);
        try
        {
            // Строка в трекере (как у открытого диалога свойства)...
            Assert.NotNull(db.Context.DbFields.Single(f => f.Id == field.Id));
            // ...а сосед ее удалил.
            using (var neighbour = new AppDbContext(db.Path))
            {
                neighbour.DbFields.Remove(neighbour.DbFields.Single(f => f.Id == field.Id));
                neighbour.SaveChanges();
            }

            field.Name = "Переименовано";
            db.Constructor.UpdateField(field);   // не должно бросить

            Assert.True(dropped >= 1, "хук PhantomRowsDropped не сработал");
            using var check = new AppDbContext(db.Path);
            Assert.False(check.DbFields.Any(f => f.Id == field.Id));   // удаленное не воскресло
        }
        finally { AppDbContext.PhantomRowsDropped = prev; }
    }

    /// <summary>
    /// Регистрация КД: в трекере повисла несохраненная правка записи (так остается после
    /// упавшего SaveChanges — BUSY на сетевой базе), сосед эту запись удалил, а мы вставляем
    /// новую запись после соседней. Вставка обязана доехать, а не упасть вместе с фантомом
    /// («Регистрация КД: непредвиденная ошибка» в диагностике).
    /// </summary>
    [Fact]
    public void Insert_survives_stale_edit_of_row_deleted_by_neighbour()
    {
        using var db = new TempDatabase();
        var col  = SampleData.AddCollection(db.Constructor);
        var recs = SampleData.AddRecords(db.Constructor, col.Id!.Value, 3);

        var stale = db.Context.DbRecords.Single(r => r.Id == recs[1].Id);
        stale.Title = "правка, которая не доедет";
        using (var neighbour = new AppDbContext(db.Path))
        {
            neighbour.DbRecords.Remove(neighbour.DbRecords.Single(r => r.Id == recs[1].Id));
            neighbour.SaveChanges();
        }

        var added = db.Constructor.AddRecordAfter(
            new DbRecord { CollectionId = col.Id.Value, Title = "Новая" }, recs[0].Id!.Value);

        Assert.NotNull(added.Id);
        using var check = new AppDbContext(db.Path);
        Assert.True(check.DbRecords.Any(r => r.Id == added.Id));
        Assert.False(check.DbRecords.Any(r => r.Id == recs[1].Id));
    }

    // ── 2. NULL в «Названии» старого файла ──────────────────────────────────────

    /// <summary>
    /// Файл, созданный до миграции AddRecordTitleAndDescription иным путем, не несет NOT NULL
    /// на Title. Чтение через проекции обязано отдавать пустое название (раньше — «The data
    /// is NULL at ordinal» и падение), а монтирование — один раз починить данные (флаг в db_meta).
    /// Ограничение снимается прямо в схеме (writable_schema) — только ради теста.
    /// </summary>
    [Fact]
    public void Null_title_rows_read_as_empty_and_are_repaired_on_mount()
    {
        var path = TempDatabase.NewTempPath();
        try
        {
            int colId;
            using (var m = DatabaseManager.Open(1, "old", path))
                colId = SampleData.AddCollection(m.Constructor).Id!.Value;

            using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                raw.Open();
                var version = Convert.ToInt64(Scalar(raw, "PRAGMA schema_version;"));
                Exec(raw, "PRAGMA writable_schema=ON;");
                Exec(raw, "UPDATE sqlite_master SET sql = replace(sql, '\"Title\" TEXT NOT NULL DEFAULT ''''', '\"Title\" TEXT NULL') WHERE name = 'db_records';");
                Exec(raw, $"PRAGMA schema_version={version + 1};");
                Exec(raw, "PRAGMA writable_schema=OFF;");
                // Первое монтирование выше уже пометило ремонт сделанным (NULL тогда не было);
                // старый файл этой версией еще не открывался — снимаем флаг.
                Exec(raw, "DELETE FROM db_meta WHERE Key = 'repair.title-null';");
            }
            using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                raw.Open();
                Exec(raw, "INSERT INTO db_records (CollectionId, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy, Position, AllDay, Title) " +
                          $"VALUES ({colId}, '2020-01-01', '2020-01-01', '', '', 1000, 0, NULL);");
                Assert.Equal(1L, Convert.ToInt64(Scalar(raw, "SELECT COUNT(*) FROM db_records WHERE Title IS NULL;")));
            }

            // Чтение без ремонта (обычное подключение, как фоновые контексты): NULL → «».
            using (var ctx = new AppDbContext(path))
            {
                var repo = new ConstructorRepository(ctx);
                Assert.Contains(repo.GetRecords(colId), r => r.Title == "");
                Assert.Contains(repo.GetRecordsPageAfter(colId, null, null, 10), r => r.Title == "");
            }
            SqliteGate.ClearAllPools();

            // Монтирование: фоновый ремонт (Dispose его дожидается) и флаг «сделано».
            using (DatabaseManager.Open(1, "old", path)) { }

            using var check = new SqliteConnection($"Data Source={path};Pooling=False");
            check.Open();
            Assert.Equal(0L, Convert.ToInt64(Scalar(check, "SELECT COUNT(*) FROM db_records WHERE Title IS NULL;")));
            Assert.Equal("1", Convert.ToString(Scalar(check, "SELECT Value FROM db_meta WHERE Key = 'repair.title-null';")));
        }
        finally { TempDatabase.Cleanup(path); }
    }

    // ── 3. Пул подключений отпускает файл вместе с базой ─────────────────────────

    /// <summary>
    /// Закрыли базу — пул Microsoft.Data.Sqlite больше не держит ее файл: иначе повторное
    /// монтирование сетевой базы после перезагрузки сервера брало из пула мертвый хендл,
    /// а WAL→DELETE при переносе файла на шару падал «database is locked». На Windows без
    /// сброса пула File.Delete здесь падал бы «файл занят».
    /// </summary>
    [Fact]
    public void Closing_database_releases_its_file_from_the_connection_pool()
    {
        var path = TempDatabase.NewTempPath();
        try
        {
            using (var m = DatabaseManager.Open(1, "t", path)) SampleData.AddCollection(m.Constructor);
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally { TempDatabase.Cleanup(path); }
    }

    // ── 4. Потеря одной базы проверяет соседей по серверу ───────────────────────

    /// <summary>
    /// Три базы: A и B на одном «сервере», C — на другом. Рабочий код сообщает об ошибке
    /// доступа к A; пробы A и B падают (сервер перезагружается), C жива. Монитор обязан
    /// объявить потерю A и B одной пачкой, не трогая C.
    /// </summary>
    [Fact]
    public void Lost_database_takes_its_server_neighbours_with_it()
    {
        var appDb = TempDatabase.NewTempPath();
        var def   = TempDatabase.NewTempPath();
        var a     = TempDatabase.NewTempPath();
        var b     = TempDatabase.NewTempPath();
        var c     = TempDatabase.NewTempPath();
        DbHealthMonitor.ProbeOverride = null;
        try
        {
            using var registry = DatabaseRegistry.Open(def, appDb);
            using var defDb    = DatabaseManager.Open(registry.GetAll().First(d => d.IsDefault).Id, "def", def);
            using var ws       = Workspace.Attach(registry, defDb);

            foreach (var p in new[] { a, b, c }) NetworkPath.ForceNetwork(p);
            NetworkPath.ForceServerRoot(a, @"\\stand\share");
            NetworkPath.ForceServerRoot(b, @"\\stand\share");
            NetworkPath.ForceServerRoot(c, @"\\other\share");
            Assert.NotNull(ws.Mount(new DatabaseRefEntity { Id = 11, Name = "A", Path = a, Provider = "sqlite" }));
            Assert.NotNull(ws.Mount(new DatabaseRefEntity { Id = 12, Name = "B", Path = b, Provider = "sqlite" }));
            Assert.NotNull(ws.Mount(new DatabaseRefEntity { Id = 13, Name = "C", Path = c, Provider = "sqlite" }));

            using var monitor = new DbHealthMonitor(ws, new MountQueue(ws));
            var lost    = new ConcurrentBag<int>();
            var twoLost = new ManualResetEventSlim();
            monitor.DatabaseLost += (id, _) => { lost.Add(id); if (lost.Count >= 2) twoLost.Set(); };

            DbHealthMonitor.ProbeOverride = p =>
                string.Equals(p, a, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p, b, StringComparison.OrdinalIgnoreCase)
                    ? (false, true)   // файл есть, не читается — «связь потеряна»
                    : null;
            monitor.ReportFailure(11, new IOException("сеть отвалилась"));

            Assert.True(twoLost.Wait(TimeSpan.FromSeconds(20)), "потеря A и B не объявлена");
            Thread.Sleep(500);   // лишним событиям (C) — шанс проявиться
            Assert.Equal(new[] { 11, 12 }, lost.OrderBy(x => x).ToArray());
        }
        finally
        {
            DbHealthMonitor.ProbeOverride = null;
            foreach (var p in new[] { a, b, c, def, appDb }) TempDatabase.Cleanup(p);
        }
    }

    // ── 5. Поллер переживает BUSY, не теряя курсор журнала ───────────────────────

    /// <summary>
    /// Чужой BEGIN EXCLUSIVE на DELETE-журнале (сетевой профиль) блокирует читателей:
    /// раньше PRAGMA data_version падала BUSY, наблюдение сбрасывалось вместе с курсором,
    /// и следующая чужая правка терялась. Теперь тик просто пропускается, а правка,
    /// записанная после снятия замка, доезжает.
    /// </summary>
    [Fact]
    public void Poller_survives_busy_and_keeps_its_cursor()
    {
        var appDb  = TempDatabase.NewTempPath();
        var def    = TempDatabase.NewTempPath();
        var shared = TempDatabase.NewTempPath();
        try
        {
            using var registry = DatabaseRegistry.Open(def, appDb);
            using var defDb    = DatabaseManager.Open(registry.GetAll().First(d => d.IsDefault).Id, "def", def);
            using var ws       = Workspace.Attach(registry, defDb);
            NetworkPath.ForceNetwork(shared);
            Assert.NotNull(ws.Mount(new DatabaseRefEntity { Id = 21, Name = "S", Path = shared, Provider = "sqlite" }));

            using var monitor = new DbHealthMonitor(ws, new MountQueue(ws));
            using var poller  = new ChangePoller(ws, monitor);
            var got = new List<ChangePoller.RemoteChange>();
            poller.RemoteChanges += (_, rows) => { lock (got) got.AddRange(rows); };

            poller.PollNow();   // знакомство: курсор на хвосте журнала

            using (var holder = new SqliteConnection($"Data Source={shared};Pooling=False"))
            {
                holder.Open();
                Exec(holder, "BEGIN EXCLUSIVE;");
                poller.PollNow();   // BUSY — без исключения
                Exec(holder, "COMMIT;");
                // Чужая правка — как ее пишет копия приложения на соседней машине.
                Exec(holder, "INSERT INTO change_log (Ts, UserKey, UserName, Action, TargetType, TargetName, CollectionName) " +
                             "VALUES ('2026-01-01T00:00:00Z', 'neighbour@pc2', 'Сосед', 'update', 'record', 'R', 'Стенд');");
            }

            poller.PollNow();
            lock (got)
                Assert.Contains(got, r => r.Action == "update" && r.TargetType == "record" && r.CollectionName == "Стенд");
        }
        finally
        {
            foreach (var p in new[] { shared, def, appDb }) TempDatabase.Cleanup(p);
        }
    }

    // ── 6. Ключ «сервера» сетевого пути ──────────────────────────────────────────

    [Fact]
    public void Server_root_groups_databases_of_one_share()
    {
        var x = TempDatabase.NewTempPath();
        var y = TempDatabase.NewTempPath();
        NetworkPath.ForceNetwork(x);
        NetworkPath.ForceNetwork(y);
        NetworkPath.ForceServerRoot(x, @"\\SRV\Share");
        NetworkPath.ForceServerRoot(y, @"\\srv\share");
        Assert.Equal(NetworkPath.ServerRoot(x), NetworkPath.ServerRoot(y));   // без учета регистра

        // Локальный путь — не сеть, соседей у него нет.
        Assert.Null(NetworkPath.ServerRoot(TempDatabase.NewTempPath()));

        if (OperatingSystem.IsWindows())
            Assert.Equal(@"\\server\share", NetworkPath.ServerRoot(@"\\Server\Share\папка\база.db"));
    }

    private static void Exec(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
