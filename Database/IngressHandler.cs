using System.Globalization;
using SmartBase.Database.Repositories;
using SmartBase.Integrations;
using SmartBase.Utils;

namespace SmartBase.Database;

/// <summary>
/// Обработчик HTTP API без привязки к сокетам. База выбирается параметром database;
/// единственную открытую базу можно не указывать. Формат, пагинация и повторы общие
/// с файловым приемом, а результаты отражаются в рабочем журнале обмена.
/// </summary>
public static class IngressHandler
{
    private const string SourceName = "Обмен по API";

    public static IngressAnswer Handle(IngressCall call,
                                       IReadOnlyList<DatabaseManager> databases,
                                       IngressReplies? replies,
                                       DateTimeOffset now)
    {
        try
        {
            return HandleCore(call, databases, replies, now);
        }
        catch (FormatException badParameter)
        {
            return IngressAnswer.Problem(400, badParameter.Message);
        }
    }

    private static IngressAnswer HandleCore(IngressCall call,
                                           IReadOnlyList<DatabaseManager> databases,
                                           IngressReplies? replies,
                                           DateTimeOffset now)
    {
        var path = (call.Path ?? "").TrimEnd('/');
        if (path.Length == 0) path = "/";

        if (path != IngressApi.PingPath && path != IngressApi.SchemaPath
            && path != IngressApi.InboxPath && path != IngressApi.RecordsPath
            && path != IngressApi.ChangesPath)
            return IngressAnswer.Problem(404,
                $"Такого адреса нет. Есть: {IngressApi.PingPath}, {IngressApi.SchemaPath}, "
                + $"{IngressApi.InboxPath}, {IngressApi.RecordsPath}, {IngressApi.ChangesPath}.");

        var wanted = path == IngressApi.InboxPath ? "POST" : "GET";
        if (!string.Equals(call.Method, wanted, StringComparison.OrdinalIgnoreCase))
            return IngressAnswer.Problem(405, $"Этот адрес отвечает только на {wanted}.");

        var selected = call.Param(IngressApi.DatabaseParameter);
        DatabaseManager? target;

        if (selected.Length == 0)
        {
            if (databases.Count == 0)
                return IngressAnswer.Problem(503, "На компьютере-приемнике нет открытых баз данных.");
            if (databases.Count != 1)
                return IngressAnswer.Problem(400,
                    "Открыто несколько баз данных. Укажите параметр «database» с ключом нужной базы.");
            target = databases[0];
        }
        else
        {
            if (!int.TryParse(selected, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                return IngressAnswer.Problem(400,
                    "Параметр «database» должен содержать положительный целый ключ базы.");
            target = databases.FirstOrDefault(d => d.DbId == id);
            if (target is null)
                return IngressAnswer.Problem(404,
                    "База с указанным ключом сейчас не открыта на компьютере-приемнике.");
        }

        return path switch
        {
            IngressApi.PingPath => IngressAnswer.Ok(new
            {
                ok = true,
                ready = true,
                database = target.Name,
                databaseId = target.DbId,
            }),
            IngressApi.SchemaPath => IngressAnswer.Ok(
                ApiDescription.Json(target.Name, target.Constructor.ApiSchemaIsolated(), target.DbId)),
            IngressApi.RecordsPath => Records(call, target, now),
            IngressApi.ChangesPath => Changes(call, target, now),
            _ => Inbox(call, target, replies, now),
        };
    }

    private static IngressAnswer Records(IngressCall call, DatabaseManager target, DateTimeOffset now)
    {
        if (Collection(call, target, out var collection, out var collectionId) is { } missing)
            return missing;
        if (ParseMoment(call.Param("since"), out var since) is { } badSince)
            return Refuse(400, badSince, call, target, now);
        var badAfter = ParseNumber(call.Param("after"), out var after);
        if (badAfter is not null || after > int.MaxValue)
            return Refuse(400, badAfter ?? "Ключ записи в параметре «after» слишком большой.",
                call, target, now);
        if (PageSize(call, out var take) is { } badLimit)
            return Refuse(400, badLimit, call, target, now);

        var fieldText = call.Param("fields");
        IReadOnlyList<string>? fieldKeys = fieldText.Length == 0 ? null
            : fieldText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var started = DateTimeOffset.Now;
        var read = target.Constructor.ApiReadIsolated(collectionId, fieldKeys, since, (int)after, take);
        if (read.Trouble is { } trouble)
            return Refuse(400, Capitalize(trouble) + ".", call, target, now);

        // При повторяющихся именах все значения используют устойчивые ключи.
        // Смешивать имена и численные ключи нельзя: свойство само может называться «12» или «t».
        var byFieldKeys = read.Columns.Distinct(StringComparer.Ordinal).Count() != read.Columns.Count;
        var outputKeys = byFieldKeys ? read.FieldKeys : read.Columns;
        var answer = IngressAnswer.Ok(new
        {
            ok = true,
            collection,
            collectionId,
            columns = read.Columns.Select((name, i) => new
            {
                key = outputKeys[i], name, fieldKey = read.FieldKeys[i],
            }).ToList(),
            rows = read.Rows.Select(r => new
            {
                id = r.Id,
                updated = r.Updated,
                values = Columns(outputKeys, r.Values),
            }).ToList(),
            next = read.More && read.Rows.Count > 0 ? (int?)read.Rows[^1].Id : null,
        });
        Note(call, target, now, started, true,
            $"Чтение коллекции «{collection}»: отдано записей {read.Rows.Count}.", read.Rows.Count, answer.Body);
        return answer;
    }

    private static IngressAnswer Changes(IngressCall call, DatabaseManager target, DateTimeOffset now)
    {
        if (Collection(call, target, out var collection, out var collectionId) is { } missing)
            return missing;
        // Старые строки истории содержат имя коллекции, а не ее ключ.
        if (target.Constructor.ApiCollectionsIsolated(collection).Count > 1)
            return Refuse(409, "Историю одноименных коллекций нельзя разделить: старые записи истории "
                + "хранят только имя коллекции. Чтение смешанной истории не выполняется.", call, target, now);
        if (ParseNumber(call.Param("after"), out var after) is { } badAfter)
            return Refuse(400, badAfter, call, target, now);
        if (PageSize(call, out var take) is { } badLimit)
            return Refuse(400, badLimit, call, target, now);

        var started = DateTimeOffset.Now;
        var tail = target.Constructor.ApiChangesIsolated(collection, after, take);
        if (tail.Trouble is { } trouble)
            return Refuse(400, Capitalize(trouble) + ".", call, target, now);

        var answer = IngressAnswer.Ok(new
        {
            ok = true,
            collection,
            collectionId,
            changes = tail.Changes.Select(c => new
            {
                n = c.Id, at = c.At, action = c.Action, target = c.Target, name = c.Name,
            }).ToList(),
            next = tail.More && tail.Changes.Count > 0 ? (long?)tail.Changes[^1].Id : null,
        });
        Note(call, target, now, started, true,
            $"Журнал изменений коллекции «{collection}»: отдано строк {tail.Changes.Count}.",
            tail.Changes.Count, answer.Body);
        return answer;
    }

    private static IngressAnswer? Collection(IngressCall call, DatabaseManager target,
                                             out string name, out int id)
    {
        name = call.Param("collection");
        id = 0;
        var keyText = call.Param("collectionId");
        int? selectedId = null;
        if (keyText.Length > 0)
        {
            if (!int.TryParse(keyText, NumberStyles.None, CultureInfo.InvariantCulture, out var key) || key <= 0)
                return IngressAnswer.Problem(400,
                    "Параметр «collectionId» должен содержать положительный целый ключ коллекции.");
            selectedId = key;
        }
        if (name.Length == 0 && selectedId is null)
            return IngressAnswer.Problem(400, "Укажите «collection» с именем или «collectionId» с ключом коллекции.");
        var matches = target.Constructor.ApiCollectionsIsolated(name, selectedId);
        if (matches.Count == 0)
            return IngressAnswer.Problem(404, "Коллекции с указанным именем или ключом в этой базе нет.");
        if (matches.Count > 1)
            return IngressAnswer.Problem(400,
                "Имя коллекции неоднозначно. Укажите параметр «collectionId»; ключи доступны в описании схемы.");
        (id, name) = matches[0];
        return null;
    }

    private static IngressAnswer Inbox(IngressCall call, DatabaseManager target,
                                       IngressReplies? replies, DateTimeOffset now)
    {
        if (call.Param("collectionId").Length > 0)
            return Refuse(400, "Прием сообщений выбирает коллекцию по полю «collection» в JSON. "
                + "Параметр «collectionId» поддерживается только при чтении.", call, target, now);
        var source = $"{call.Remote} {IngressApi.InboxPath}".Trim();
        var message = InboxMessage.TryParse(call.Body, out var error);
        if (message is null)
            return Refuse(400, "Сообщение не разобралось: " + error + ".", call, target, now);
        var problems = message.Problems();
        if (problems.Count > 0)
            return Refuse(400, Capitalize(string.Join("; ", problems)) + ".", call, target, now);

        // Идентификатор сообщения теперь общий в пределах выбранной базы.
        if (replies?.Past(target.DbId, message.MessageId) is { } past)
        {
            Note(call, target, now, DateTimeOffset.Now, true, "Повтор: сообщение уже приходило.",
                past.Created + past.Updated + past.Deleted, "");
            return IngressAnswer.Ok(past.ToJson());
        }

        var started = DateTimeOffset.Now;
        InboxReceipt receipt;
        using (CurrentUser.ActAs($"api@{Environment.MachineName}".ToLowerInvariant(), SourceName))
            receipt = InboxIntake.Apply(message, target.Constructor, target.DbId, source, now);

        if (receipt.Accepted && replies is not null
            && !replies.Remember(target.DbId, message.MessageId, receipt, now))
            Serilog.Log.Warning("Прием по сети: ответ на сообщение «{Id:l}» не запомнился — "
                + "повторная доставка будет обработана заново", message.MessageId);

        var answer = new IngressAnswer(receipt.Accepted ? 200 : 422, receipt.ToJson());
        Note(call, target, now, started, receipt.Accepted, receipt.Explanation,
            receipt.Created + receipt.Updated + receipt.Deleted, call.Body ?? "");
        return answer;
    }

    private static IngressAnswer Refuse(int status, string explanation, IngressCall call,
                                        DatabaseManager target, DateTimeOffset now)
    {
        Note(call, target, now, DateTimeOffset.Now, false, explanation, 0, "");
        return IngressAnswer.Problem(status, explanation);
    }

    private static void Note(IngressCall call, DatabaseManager target, DateTimeOffset now,
                              DateTimeOffset started, bool ok, string explanation, int rows, string body) =>
        RunJournal.Add(RunEntry.New(RunChannel.Ingress, SourceName,
            $"{call.Remote} {call.Path}".Trim(), ok, explanation, rows,
            System.Text.Encoding.UTF8.GetByteCount(body), DateTimeOffset.Now - started, target.DbId, now));

    private static Dictionary<string, string> Columns(IReadOnlyList<string> names, string[] values)
    {
        var row = new Dictionary<string, string>(names.Count);
        for (var i = 0; i < names.Count; i++) row.Add(names[i], i < values.Length ? values[i] : "");
        return row;
    }

    private static string? ParseMoment(string text, out string normalized)
    {
        normalized = "";
        if (text.Length == 0) return null;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var moment))
            return "Параметр «since» не разобрался: нужен момент вида 2026-09-12T10:00:00Z.";
        normalized = moment.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        return null;
    }

    private static string? ParseNumber(string text, out long value)
    {
        value = 0;
        if (text.Length == 0) return null;
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 0
            ? null : "Числовой параметр не разобрался: нужно целое число не меньше нуля.";
    }

    private static string? PageSize(IngressCall call, out int take)
    {
        var error = ParseNumber(call.Param("limit"), out var limit);
        take = limit == 0 ? ConstructorRepository.ApiPage : (int)Math.Min(limit, ConstructorRepository.ApiPage);
        return error;
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0]) + text[1..];
}
