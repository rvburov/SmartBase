using Microsoft.Data.Sqlite;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Extensions.TimeCollector;

/// <summary>
/// Одноразовая SQLite-БД на временном файле, поднятая настоящим composition root
/// <see cref="DatabaseManager.Open"/> — выгрузка свода в коллекцию проходит ровно тот же путь
/// инициализации, что и в приложении: миграции, прагмы, регистрация текущего пользователя
/// администратором.
///
/// Урезанная ЛОКАЛЬНАЯ копия обвязки ядра (Tests/Integration/TempDatabase.cs), как и у
/// «Документов в коллекцию»: тесты расширения не должны зависеть от тестов программы —
/// удаление расширения уносит и эту копию, и больше ничего.
///
/// Файл на диске, а не <c>:memory:</c>, — принципиально: <c>TimeCollectionWriter.FillRecords</c>
/// открывает СВОЕ второе подключение к тому же пути, а у in-memory базы второго подключения
/// к тем же данным не бывает.
/// </summary>
internal sealed class TempDatabase : IDisposable
{
    public string Path { get; }
    public DatabaseManager Manager { get; }

    public AppDbContext          Context     => Manager.Context;
    public ConstructorRepository Constructor => Manager.Constructor;
    public PermissionService     Permissions => Manager.Permissions;

    public TempDatabase()
    {
        Path    = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sbext_time_{Guid.NewGuid():N}.db");
        Manager = DatabaseManager.Open(1, "test", Path);
    }

    public void Dispose()
    {
        try { Manager.Dispose(); } catch { /* при падении теста файл все равно чистим */ }

        // Пул Microsoft.Data.Sqlite держит файловый дескриптор — без сброса файл «занят».
        try { SqliteConnection.ClearAllPools(); } catch { /* best-effort */ }
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var p = Path + suffix;
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
        }
    }
}
