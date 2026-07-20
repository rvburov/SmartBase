using Microsoft.Data.Sqlite;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Одноразовая SQLite-БД на временном файле, поднятая через настоящий composition root
/// <see cref="DatabaseManager.Open"/> — так интеграционные тесты проходят ровно тот же
/// путь инициализации, что и приложение: миграции, прагмы, регистрация текущего
/// пользователя администратором, сиды категорий, построение календарной проекции.
///
/// <c>:memory:</c> сознательно не используется — WAL и ATTACH (cross-DB перенос) требуют
/// файла на диске (см. заметки проекта про фоновые контексты и перенос через ATTACH).
/// </summary>
public sealed class TempDatabase : IDisposable
{
    /// <summary>Путь к временному файлу БД.</summary>
    public string Path { get; }

    public DatabaseManager Manager { get; }

    public AppDbContext          Context     => Manager.Context;
    public ConstructorRepository Constructor => Manager.Constructor;
    public CategoryRepository    Categories  => Manager.Categories;
    public PermissionService     Permissions => Manager.Permissions;

    public TempDatabase(int dbId = 1, string name = "test")
    {
        Path    = NewTempPath();
        Manager = DatabaseManager.Open(dbId, name, Path);
    }

    /// <summary>Новый уникальный путь во временной папке ОС (без создания файла).</summary>
    public static string NewTempPath() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sbtest_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { Manager.Dispose(); } catch { /* при падении теста файл всё равно чистим */ }
        Cleanup(Path);
    }

    /// <summary>Удалить файл БД и спутники WAL (-wal/-shm/-journal). Пул соединений
    /// Microsoft.Data.Sqlite держит файловый дескриптор — сбрасываем его, иначе на
    /// части платформ файл «занят» и не удаляется.</summary>
    public static void Cleanup(string path)
    {
        try { SqliteConnection.ClearAllPools(); } catch { /* best-effort */ }
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var p = path + suffix;
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
        }
    }
}
