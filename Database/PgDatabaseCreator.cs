using System.Text.RegularExpressions;
using Npgsql;
using Serilog;

namespace SmartBase.Database;

/// <summary>Что реально произошло при создании: роль могла существовать заранее —
/// тогда она переиспользуется, а ее пароль не меняется (сообщаем это пользователю).</summary>
public sealed record PgCreateOutcome(bool RoleCreated, bool RoleReused);

/// <summary>
/// Создание базы данных и роли-владельца на сервере PostgreSQL — логика окна
/// PgCreateDatabaseDialog, вынесенная из UI ради тестов. Подключается администратором
/// (роль с правом CREATEDB/CREATEROLE, обычно postgres) к служебной базе postgres и
/// выполняет CREATE ROLE + CREATE DATABASE OWNER. OWNER обязателен: с PostgreSQL 15
/// писать в схему public по умолчанию может только владелец базы.
///
/// Экранирование имен и пароля делает САМ сервер: DDL не принимает параметры, поэтому
/// текст команды собирается запросом SELECT format('… %I … %L', $1, $2) — %I/%L это
/// штатное квотирование идентификаторов/литералов PostgreSQL, ручной сборки строк нет.
/// CREATE DATABASE нельзя выполнять в транзакции — каждая команда идет отдельно.
/// </summary>
public static class PgDatabaseCreator
{
    /// <summary>Имя служебной базы, к которой подключается администратор.</summary>
    public const string MaintenanceDb = "postgres";

    // Строчные латинские, цифры и подчеркивание, не с цифры — такие имена не требуют
    // кавычек в psql и не превращаются в сюрприз при ручном администрировании сервера.
    private static readonly Regex NameRx = new("^[a-z_][a-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>Проверка имени базы/роли: null — имя годится, иначе русский текст ошибки.</summary>
    public static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Укажите имя";
        if (name.Length > 63)
            return "Имя длиннее 63 символов — сервер такое не примет";
        if (!NameRx.IsMatch(name))
            return "Только строчные латинские буквы, цифры и подчеркивание; первая — буква";
        return null;
    }

    /// <summary>
    /// Создать роль-владельца и базу. <paramref name="admin"/> — параметры администратора,
    /// база в них игнорируется (подключаемся к служебной). Роль создается первой; если база
    /// не создалась, а роль создали мы этой же попыткой — роль подчищается (повтор кнопки
    /// идемпотентен). Ошибки сервера пробрасываются как есть — русские тексты дает PgErrors.
    /// </summary>
    public static async Task<PgCreateOutcome> CreateAsync(
        PgConnectionInfo admin, string dbName, string roleName, string rolePassword)
    {
        var maint = admin with { Database = MaintenanceDb };
        await using var conn = new NpgsqlConnection(maint.ToConnectionString());
        await conn.OpenAsync();

        var roleCreated = false;
        var roleReused  = false;
        try
        {
            await ExecFormatted(conn, "CREATE ROLE %I LOGIN PASSWORD %L", roleName, rolePassword);
            roleCreated = true;
        }
        catch (PostgresException e) when (e.SqlState == "42710")
        {
            roleReused = true;   // роль уже есть — используем, пароль не трогаем
        }

        try
        {
            await ExecFormatted(conn, "CREATE DATABASE %I OWNER %I", dbName, roleName);
        }
        catch
        {
            if (roleCreated)
                await TryDropRole(conn, roleName);
            throw;
        }

        return new PgCreateOutcome(roleCreated, roleReused);
    }

    // Текст DDL собирает сервер: format('%I'/'%L') квотирует идентификаторы и литералы,
    // значения уходят обычными параметрами. Полученную команду выполняем второй.
    private static async Task ExecFormatted(NpgsqlConnection conn, string template, params string[] args)
    {
        string ddl;
        await using (var fmt = conn.CreateCommand())
        {
            var placeholders = string.Join(", ", args.Select((_, i) => $"${i + 2}"));
            fmt.CommandText = $"SELECT format($1, {placeholders})";
            fmt.Parameters.Add(new NpgsqlParameter { Value = template });
            foreach (var a in args)
                fmt.Parameters.Add(new NpgsqlParameter { Value = a });
            ddl = (string)(await fmt.ExecuteScalarAsync())!;
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ddl;
        await cmd.ExecuteNonQueryAsync();
    }

    // Подчистка роли-сироты после неудачного CREATE DATABASE — best-effort: не вышло —
    // записали в журнал, пользователю об этом не сообщаем (повтор все равно сработает
    // по ветке «роль уже есть»).
    private static async Task TryDropRole(NpgsqlConnection conn, string roleName)
    {
        try
        {
            await ExecFormatted(conn, "DROP ROLE %I", roleName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Не удалось подчистить роль {Role:l} после неудачного создания базы", roleName);
        }
    }
}
