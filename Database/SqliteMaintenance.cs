using Microsoft.EntityFrameworkCore;

namespace SmartBase.Database;

/// <summary>
/// Обслуживание SQLite-файла. Сейчас — разовое сжатие раздутого файла: после массовых
/// удалений/импортов SQLite без <c>auto_vacuum</c> не возвращает освобождённые страницы
/// в ОС, и файл копится в разы больше реальных данных. Один общий хелпер для всех БД
/// (данные и системная app.db), чтобы логика не расходилась.
/// </summary>
internal static class SqliteMaintenance
{
    /// <summary>
    /// Разово ужать файл (VACUUM), если свободных страниц много: > 50% файла И больше
    /// <paramref name="minFreeBytes"/>. Порог с запасом — на нормально заполненных БД
    /// VACUUM не запускается. VACUUM переписывает файл (данные сохраняются), стоит секунды
    /// на больших объёмах, но выполняется один раз: после него freelist пуст и условие
    /// на следующем старте не срабатывает.
    /// </summary>
    public static void CompactIfBloated(DbContext db, long minFreeBytes)
    {
        var (freeBytes, freeRatio) = ReadFreeSpace(db);
        if (freeRatio > 0.5 && freeBytes > minFreeBytes)
            db.Database.ExecuteSqlRaw("VACUUM;");
    }

    /// <summary>Свободное место файла: (байты freelist, доля freelist от всех страниц).
    /// Наружу — для DatabaseManager.CompactWithProgress: сжатие данных-БД идёт с этапом
    /// и живым прогрессом на сплэше, здесь остаётся только «тихий» вариант для app.db.</summary>
    internal static (long FreeBytes, double FreeRatio) ReadFreeSpace(DbContext db)
    {
        var pageSize  = ReadPragmaLong(db, "PRAGMA page_size;");
        var pageCount = ReadPragmaLong(db, "PRAGMA page_count;");
        var freeList  = ReadPragmaLong(db, "PRAGMA freelist_count;");
        if (pageCount == 0 || pageSize == 0) return (0, 0);
        return (freeList * pageSize, (double)freeList / pageCount);
    }

    // Прочитать скалярное значение PRAGMA (напр. page_count) через соединение контекста.
    private static long ReadPragmaLong(DbContext db, string sql)
    {
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var v = cmd.ExecuteScalar();
            return v is null or DBNull ? 0 : Convert.ToInt64(v);
        }
        finally
        {
            if (opened) conn.Close();
        }
    }
}
