namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository
{
    // Раскрытие значения «Файла» выполняется ДО счета, фильтра и страницы. Старый путь дает
    // одну строку с индексом 0; новый объект paths — отдельную строку для каждого файла.
    // В SQLite каждый JSON-вызов защищен CASE: произвольная строка прежнего типа остается
    // одним значением. В PostgreSQL тот же контракт выполняет функция из парной миграции.
    // JsonDocument и jsonb выбирают последнее одноименное свойство объекта. SQLite-путь
    // $.paths выбирает первое, поэтому читаем свойство по последнему id из json_each.
    private const string SqlitePathsValueSql = "(SELECT prop.value FROM json_each(v.Value) AS prop WHERE prop.key = 'paths' ORDER BY prop.id DESC LIMIT 1)";
    private const string SqlitePathsTypeSql = "(SELECT prop.type FROM json_each(v.Value) AS prop WHERE prop.key = 'paths' ORDER BY prop.id DESC LIMIT 1)";

    private string DocPathsJoinSql => db.IsPostgres
        ? "JOIN LATERAL smartbase_file_paths(v.Value) AS dp ON TRUE"
        : $@"JOIN json_each(
                CASE WHEN substr(v.Value, 1, 1) = '{{' THEN
                    CASE WHEN json_valid(v.Value) THEN
                        CASE WHEN {SqlitePathsTypeSql} = 'array' THEN
                            CASE WHEN NOT EXISTS (
                                SELECT 1 FROM json_each({SqlitePathsValueSql}) AS part
                                WHERE part.type <> 'text')
                            THEN {SqlitePathsValueSql} ELSE json_array(v.Value) END
                        ELSE json_array(v.Value) END
                    ELSE json_array(v.Value) END
                ELSE json_array(v.Value) END
            ) AS dp ON dp.type = 'text' AND dp.value <> ''";

    private string DocPathSql => db.IsPostgres ? "dp.path" : "dp.value";
    private string DocFileIndexSql => db.IsPostgres ? "dp.file_index" : "CAST(dp.key AS INTEGER)";
}
