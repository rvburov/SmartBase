namespace SmartBase.Database;

/// <summary>
/// Функции текста для запросов EF, которых нет в SQLite. Вызываются только внутри запросов:
/// EF переводит вызов в функцию базы, а сама функция регистрируется на каждом подключении
/// SQLite (AppDbContext.OnConnectionStateChange). На PostgreSQL их нет — там тот же смысл дает
/// <c>ILIKE</c>, вызывающий выбирает ветку по <c>IsPostgres</c>.
/// </summary>
public static class SqlText
{
    /// <summary>Имя функции в SQLite.</summary>
    public const string ContainsName = "sb_contains";

    /// <summary>Показанные имена файлов для сортировки SQLite; тот же разбор, что у ячейки.</summary>
    public const string FileDisplayName = "sb_file_display";

    /// <summary>Содержит ли <paramref name="value"/> подстроку <paramref name="part"/> без учета
    /// регистра, в том числе у кириллицы (встроенные LIKE и lower() SQLite различают регистр
    /// всего, кроме латиницы).</summary>
    public static bool ContainsIgnoreCase(string value, string part) =>
        throw new InvalidOperationException("SqlText.ContainsIgnoreCase работает только внутри запроса EF");
}
