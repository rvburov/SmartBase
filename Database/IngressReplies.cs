using System.Globalization;
using Microsoft.Data.Sqlite;
using SmartBase.Integrations;

namespace SmartBase.Database;

/// <summary>
/// Память о принятых обращениях входящего канала — защита от повтора (docs/ROADMAP-API.md,
/// раздел 7: «сторонняя система при таймауте пошлет запрос дважды, это норма»).
///
/// У папки обмена такой памятью служит квитанция, лежащая рядом с файлом, и лежит она там не
/// случайно: вторая доставка файла может попасть на СОСЕДНЮЮ машину, а память на одной машине ее
/// пропустила бы. У сетевого канала этой беды нет по построению — стучатся всегда в один и тот же
/// процесс, — поэтому память живет своим файлом рядом с рабочим журналом (<c>integrations.db</c>),
/// а не в папке обмена, которой у HTTP может не быть вовсе.
///
/// <b>Хранится ОТВЕТ, а не отметка «было».</b> Повтор обязан получить то же самое, что и первая
/// доставка: отправитель, не дождавшийся ответа, повторяет запрос именно затем, чтобы ответ
/// прочитать, и «уже приходило» вместо присвоенного номера для него ничем не лучше молчания.
/// </summary>
public sealed class IngressReplies
{
    /// <summary>
    /// Сколько дней помнить ответы. Память о повторах нужна ровно на то время, пока отправитель
    /// может повторить обращение; год хранения не добавил бы к этому ничего, кроме файла.
    /// </summary>
    public const int KeepDays = 30;

    private readonly string _path;
    private readonly object _lock = new();

    public IngressReplies(string path)
    {
        _path = path;
        Ensure();
    }

    /// <summary>Путь к файлу — для экрана «где это лежит».</summary>
    public string Path => _path;

    private void Ensure()
    {
        using var connection = Open();
        using var command    = connection.CreateCommand();

        // Ключ составной: один и тот же идентификатор сообщения у разных токенов означает
        // разных отправителей, и считать их повторами друг друга нельзя.
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ingress_replies (
                TokenId   INTEGER NOT NULL,
                MessageId TEXT    NOT NULL,
                At        TEXT    NOT NULL,
                Reply     TEXT    NOT NULL,
                PRIMARY KEY (TokenId, MessageId)
            );
            CREATE INDEX IF NOT EXISTS ingress_replies_at ON ingress_replies (At);
            """;

        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    /// <summary>
    /// Ответ на уже принятое сообщение; <c>null</c> — такого не приходило. Сообщение без
    /// идентификатора повтором не бывает: защищаться нечем, и отправителю об этом сказано
    /// отказом еще на проверке формата.
    /// </summary>
    public InboxReceipt? Past(int tokenId, string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return null;

        try
        {
            lock (_lock)
            {
                using var connection = Open();
                using var command    = connection.CreateCommand();

                command.CommandText =
                    "SELECT Reply FROM ingress_replies WHERE TokenId = $token AND MessageId = $id;";
                command.Parameters.AddWithValue("$token", tokenId);
                command.Parameters.AddWithValue("$id",    messageId);

                return InboxReceipt.TryParse(command.ExecuteScalar() as string);
            }
        }
        catch (SqliteException failure)
        {
            // Память не прочиталась — обращение пройдет как новое. Это осознанный выбор в пользу
            // работы: отказать из-за недоступной памяти значило бы остановить обмен целиком, а
            // повтор в худшем случае обновит те же записи теми же значениями.
            Serilog.Log.Warning("Прием по сети: память о повторах не прочиталась — {Reason:l}",
                                failure.Message);
            return null;
        }
    }

    /// <summary>
    /// Запомнить ответ. <c>false</c> — не записалось: вызывающий обязан знать об этом, потому что
    /// повтор тогда обработается второй раз.
    /// </summary>
    public bool Remember(int tokenId, string messageId, InboxReceipt reply, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return true;   // защищать нечего

        try
        {
            lock (_lock)
            {
                using var connection = Open();
                using var command    = connection.CreateCommand();

                command.CommandText = """
                    INSERT OR REPLACE INTO ingress_replies (TokenId, MessageId, At, Reply)
                    VALUES ($token, $id, $at, $reply);
                    """;

                command.Parameters.AddWithValue("$token", tokenId);
                command.Parameters.AddWithValue("$id",    messageId);
                command.Parameters.AddWithValue("$at",
                    now.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$reply", reply.ToJson());

                command.ExecuteNonQuery();
                return true;
            }
        }
        catch (SqliteException failure)
        {
            Serilog.Log.Warning("Прием по сети: ответ не запомнился — {Reason:l}", failure.Message);
            return false;
        }
    }

    /// <summary>Убрать то, что помнить уже незачем. Сколько строк убрано.</summary>
    public int Forget(DateTimeOffset now)
    {
        try
        {
            lock (_lock)
            {
                using var connection = Open();
                using var command    = connection.CreateCommand();

                command.CommandText = "DELETE FROM ingress_replies WHERE At < $edge;";
                command.Parameters.AddWithValue("$edge",
                    now.AddDays(-KeepDays).ToUniversalTime()
                       .ToString("o", CultureInfo.InvariantCulture));

                return command.ExecuteNonQuery();
            }
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    /// <summary>Сколько ответов помнится — экрану «Доступ извне».</summary>
    public long Count()
    {
        try
        {
            lock (_lock)
            {
                using var connection = Open();
                using var command    = connection.CreateCommand();

                command.CommandText = "SELECT COUNT(*) FROM ingress_replies;";
                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    // ── Общий доступ ──────────────────────────────────────────────────────────

    /// <summary>Память приложения; <c>null</c> — файл не открылся.</summary>
    public static IngressReplies? Current { get; private set; }

    public static void Attach(IngressReplies? replies) => Current = replies;
}
