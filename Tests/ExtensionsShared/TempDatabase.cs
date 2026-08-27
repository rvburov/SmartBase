using Microsoft.Data.Sqlite;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Extensions.Shared;

/// <summary>
/// Одноразовая SQLite-БД на временном файле, поднятая настоящим composition root
/// <see cref="DatabaseManager.Open"/> — расширение проходит ровно тот же путь инициализации,
/// что и в приложении: миграции, прагмы, регистрация текущего пользователя администратором.
///
/// Файл на диске, а не <c>:memory:</c>, — принципиально для расширений, которые пишут записи на
/// СВОЕМ втором подключении к тому же пути («Документы в коллекцию», «Сводная трудоемкость»):
/// у in-memory базы второго подключения к тем же данным не бывает.
///
/// <b>Уборка процесс-глобальна.</b> <see cref="Dispose"/> зовет
/// <see cref="SqliteConnection.ClearAllPools"/>, иначе пул держит файловый дескриптор и файл
/// остается «занят». Отсюда требование к набору, который этим пользуется: в нем должен лежать
/// <c>AssemblyInfo.cs</c> с <c>DisableTestParallelization</c> — параллельные тесты иначе изредка
/// задевают чужие подключения. Плюс <see cref="DatabaseManager.Open"/> трогает статику
/// <c>CurrentUser</c>, одну на процесс.
/// </summary>
public sealed class TempDatabase : IDisposable
{
    public string Path { get; }
    public DatabaseManager Manager { get; }

    public AppDbContext          Context     => Manager.Context;
    public ConstructorRepository Constructor => Manager.Constructor;
    public PermissionService     Permissions => Manager.Permissions;

    /// <param name="tag">Метка в имени файла — по ней в системной temp-папке видно, чей
    /// набор оставил файл, если уборка не отработала. Пусто — просто «sbext_&lt;guid&gt;.db».</param>
    public TempDatabase(string tag = "")
    {
        var prefix = tag.Length > 0 ? $"sbext_{tag}_" : "sbext_";
        Path    = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}{Guid.NewGuid():N}.db");
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
