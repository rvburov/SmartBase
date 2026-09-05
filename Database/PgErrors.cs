using Npgsql;

namespace SmartBase.Database;

/// <summary>
/// Русские тексты типовых ошибок сервера PostgreSQL — один словарь на все окна
/// (подключение, создание базы). Коды SqlState стабильны и документированы, текст
/// сервера — нет (зависит от локали сервера), поэтому маппим по кодам, а незнакомое
/// показываем как есть.
/// </summary>
public static class PgErrors
{
    public static string Describe(Exception ex)
    {
        if (ex is PostgresException pe)
            return pe.SqlState switch
            {
                "28P01" => "Неверный пароль или пользователь.",
                "28000" => "Сервер отклонил подключение — проверьте настройки доступа (pg_hba.conf).",
                "3D000" => "База данных не найдена на сервере. Создайте ее через меню " +
                           "«Создать базу данных на сервере PostgreSQL» и повторите.",
                "42P04" => "База с таким именем уже есть на сервере — подключите ее через " +
                           "«Подключить сервер PostgreSQL».",
                "42710" => "Роль с таким именем уже есть на сервере.",
                "42501" => "Недостаточно прав — нужна роль с правом создавать базы и роли " +
                           "(например postgres).",
                "53300" => "На сервере кончились свободные подключения (max_connections) — " +
                           "попробуйте позже.",
                _       => $"Ошибка сервера: {pe.MessageText}",
            };
        if (ex is System.Security.Authentication.AuthenticationException
            || ex.InnerException is System.Security.Authentication.AuthenticationException)
            return "Не удалось установить защищенное соединение — попробуйте другой режим SSL.";
        if (ex is TimeoutException or System.Net.Sockets.SocketException
            || ex.InnerException is TimeoutException or System.Net.Sockets.SocketException)
            return "Сервер недоступен — проверьте адрес, порт и сеть.";
        return ex.Message;
    }
}
