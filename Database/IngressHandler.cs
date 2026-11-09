using System.Globalization;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Integrations;
using SmartBase.Utils;

namespace SmartBase.Database;

/// <summary>
/// Обработчик HTTP API без привязки к сокетам: слушатель читает запрос и отдает ответ, а решает
/// все этот класс — проверить прием можно, не открывая порта.
///
/// <b>Порядок проверок:</b>
/// <list type="number">
///   <item><b>Адрес и метод.</b> «Такого адреса нет» не выдает ничего о токенах.</item>
///   <item><b>Токен.</b> Нет, не разобрался, выключен, не подошел — один и тот же ответ «неверный
///   токен»: подсказывать, ЧТО именно не так, значит помогать подбирать.</item>
///   <item><b>База.</b> Токен выдан под одну базу; она закрыта — отказ с причиной. Параметр
///   <c>database</c> необязателен, а указанный обязан совпасть с базой токена.</item>
///   <item><b>Права робота.</b> Токен — пользователь в <c>db_users</c> целевой базы с обычной
///   маской; недопущенный робот ждет так же, как человек. Запись — по правам создания, правки и
///   удаления, чтение (<c>/v1/records</c>, <c>/v1/changes</c>) — по праву просмотра коллекций,
///   которое администратор базы выдает роботу той же панелью, что и человеку.</item>
///   <item><b>Формат, повтор, укладка</b> — общие с файловым приемом, от имени робота.</item>
/// </list>
/// Результаты отражаются в рабочем журнале обмена.
/// </summary>
public static class IngressHandler
{
    /// <summary>
    /// Ответить на обращение. Исключений с разбором параметров не бросает: любая беда — ответ с
    /// кодом, потому что оборванное соединение отправитель истолкует как «программа не работает».
    /// </summary>
    /// <param name="registry">Реестр обмена на СВОЕМ подключении к app.db: обращения
    /// обрабатываются в фоне, а общий контекст принадлежит интерфейсу.</param>
    /// <param name="databases">Открытые базы — целевая ищется среди них по токену.</param>
    /// <param name="replies">Память о повторах; <c>null</c> — памяти нет, и повтор пройдет как
    /// новое сообщение.</param>
    public static IngressAnswer Handle(IngressCall call,
                                       IntegrationsRegistry registry,
                                       IReadOnlyList<DatabaseManager> databases,
                                       IngressReplies? replies,
                                       DateTimeOffset now)
    {
        try
        {
            return HandleCore(call, registry, databases, replies, now);
        }
        catch (FormatException badParameter)
        {
            return IngressAnswer.Problem(400, badParameter.Message);
        }
    }

    private static IngressAnswer HandleCore(IngressCall call,
                                           IntegrationsRegistry registry,
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

        if (registry.Verify(call.Token) is not { } token)
            return IngressAnswer.Problem(401, "Неверный токен.");

        registry.MarkTokenUsed(token.Id, now);

        if (databases.FirstOrDefault(d => d.DbId == token.DbId) is not { } target)
            return IngressAnswer.Problem(503,
                "База данных этого токена сейчас не открыта на компьютере-приемнике.");

        // База определяется токеном; параметр «database» оставлен для явности и обязан совпасть.
        var selected = call.Param(IngressApi.DatabaseParameter);
        if (selected.Length > 0)
        {
            if (!int.TryParse(selected, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                return IngressAnswer.Problem(400,
                    "Параметр «database» должен содержать положительный целый ключ базы.");
            if (id != token.DbId)
                return IngressAnswer.Problem(403, "Токен выдан для другой базы данных.");
        }

        var access = PermissionService.RobotIsolated(target.Constructor.DbPath, token.RobotKey);
        var who    = new Caller(token, access);

        return path switch
        {
            IngressApi.PingPath    => Ping(who, target),
            IngressApi.SchemaPath  => Schema(who, target),
            IngressApi.RecordsPath => Records(call, who, target, now),
            IngressApi.ChangesPath => Changes(call, who, target, now),
            _                      => Inbox(call, who, target, registry, replies, now),
        };
    }

    /// <summary>Кто обращается: токен и права его робота в целевой базе.</summary>
    private sealed record Caller(AccessToken Token, RobotAccess Access);

    /// <summary>
    /// «Жив ли слушатель и тот ли это токен». Отвечает и недопущенному роботу — но честно, полем
    /// <c>ready</c> и причиной: отправитель, настраивающий обмен, должен узнать про недостающий
    /// допуск здесь, а не из отказа первой же боевой посылки.
    /// </summary>
    private static IngressAnswer Ping(Caller who, DatabaseManager target)
    {
        var trouble = who.Access.Trouble(PermAction.CreateRecord);

        return IngressAnswer.Ok(new
        {
            ok         = true,
            ready      = trouble is null,
            database   = target.Name,
            databaseId = target.DbId,
            token      = who.Token.Name,
            trouble,
        });
    }

    /// <summary>Схема — тому, кому есть куда писать или что читать: список коллекций базы, в
    /// которую робота не пустили, — это уже сведения о базе, а не о его собственном обмене.</summary>
    private static IngressAnswer Schema(Caller who, DatabaseManager target)
    {
        if (who.Access.Trouble(PermAction.CreateRecord) is { } trouble
            && who.Access.Trouble(PermAction.ViewCollection) is not null)
            return IngressAnswer.Problem(403, Capitalize(trouble) + ".");

        return IngressAnswer.Ok(
            ApiDescription.Json(target.Name, target.Constructor.ApiSchemaIsolated(), target.DbId));
    }

    private static IngressAnswer Records(IngressCall call, Caller who, DatabaseManager target, DateTimeOffset now)
    {
        if (who.Access.Trouble(PermAction.ViewCollection) is { } denied)
            return Refuse(403, Capitalize(denied) + ".", call, who, target, now);
        if (Collection(call, target, out var collection, out var collectionId) is { } missing)
            return missing;
        if (ParseMoment(call.Param("since"), out var since) is { } badSince)
            return Refuse(400, badSince, call, who, target, now);
        var badAfter = ParseNumber(call.Param("after"), out var after);
        if (badAfter is not null || after > int.MaxValue)
            return Refuse(400, badAfter ?? "Ключ записи в параметре «after» слишком большой.",
                call, who, target, now);
        if (PageSize(call, out var take) is { } badLimit)
            return Refuse(400, badLimit, call, who, target, now);

        var fieldText = call.Param("fields");
        IReadOnlyList<string>? fieldKeys = fieldText.Length == 0 ? null
            : fieldText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var started = DateTimeOffset.Now;
        ConstructorRepository.ApiRows read;
        try
        {
            read = target.Constructor.ApiReadIsolated(collectionId, fieldKeys, since, (int)after, take);
        }
        catch (PermissionDeniedException mine)
        {
            return Refuse(403, "На компьютере-приемнике: " + mine.Message, call, who, target, now);
        }
        if (read.Trouble is { } trouble)
            return Refuse(400, Capitalize(trouble) + ".", call, who, target, now);

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
        Note(call, who, target, now, started, true,
            $"Чтение коллекции «{collection}»: отдано записей {read.Rows.Count}.", read.Rows.Count, answer.Body);
        PermissionService.MarkRobotSeenIsolated(target.Constructor.DbPath, who.Token.RobotKey, now);
        return answer;
    }

    private static IngressAnswer Changes(IngressCall call, Caller who, DatabaseManager target, DateTimeOffset now)
    {
        if (who.Access.Trouble(PermAction.ViewCollection) is { } denied)
            return Refuse(403, Capitalize(denied) + ".", call, who, target, now);
        if (Collection(call, target, out var collection, out var collectionId) is { } missing)
            return missing;
        // Старые строки истории содержат имя коллекции, а не ее ключ.
        if (target.Constructor.ApiCollectionsIsolated(collection).Count > 1)
            return Refuse(409, "Историю одноименных коллекций нельзя разделить: старые записи истории "
                + "хранят только имя коллекции. Чтение смешанной истории не выполняется.", call, who, target, now);
        if (ParseNumber(call.Param("after"), out var after) is { } badAfter)
            return Refuse(400, badAfter, call, who, target, now);
        if (PageSize(call, out var take) is { } badLimit)
            return Refuse(400, badLimit, call, who, target, now);

        var started = DateTimeOffset.Now;
        ConstructorRepository.ApiChangesPage tail;
        try
        {
            tail = target.Constructor.ApiChangesIsolated(collection, after, take);
        }
        catch (PermissionDeniedException mine)
        {
            return Refuse(403, "На компьютере-приемнике: " + mine.Message, call, who, target, now);
        }
        if (tail.Trouble is { } trouble)
            return Refuse(400, Capitalize(trouble) + ".", call, who, target, now);

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
        Note(call, who, target, now, started, true,
            $"Журнал изменений коллекции «{collection}»: отдано строк {tail.Changes.Count}.",
            tail.Changes.Count, answer.Body);
        PermissionService.MarkRobotSeenIsolated(target.Constructor.DbPath, who.Token.RobotKey, now);
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

    private static IngressAnswer Inbox(IngressCall call, Caller who, DatabaseManager target,
                                       IntegrationsRegistry registry, IngressReplies? replies,
                                       DateTimeOffset now)
    {
        if (call.Param("collectionId").Length > 0)
            return Refuse(400, "Прием сообщений выбирает коллекцию по полю «collection» в JSON. "
                + "Параметр «collectionId» поддерживается только при чтении.", call, who, target, now);
        var source = $"{call.Remote} {IngressApi.InboxPath}".Trim();
        var message = InboxMessage.TryParse(call.Body, out var error);
        if (message is null)
            return Refuse(400, "Сообщение не разобралось: " + error + ".", call, who, target, now);
        var problems = message.Problems();
        if (problems.Count > 0)
            return Refuse(400, Capitalize(string.Join("; ", problems)) + ".", call, who, target, now);

        // Каждый режим спрашивает СВОЕ право — то же, которое проверила бы панель у человека.
        // По умолчанию роботу выдана только запись; удаление и подготовку коллекций открывает
        // администратор базы обычной панелью.
        PermAction[] rights = message.Mode switch
        {
            InboxMode.Delete           => [PermAction.DeleteRecord],
            InboxMode.EnsureCollection => [PermAction.CreateCollection, PermAction.CreateField],
            _                          => [PermAction.CreateRecord],
        };
        foreach (var right in rights)
            if (who.Access.Trouble(right) is { } trouble)
                return Refuse(403, Capitalize(trouble) + ".", call, who, target, now);

        // Повтор узнается по паре «токен + идентификатор сообщения»: один и тот же номер у
        // разных отправителей — разные сообщения.
        if (replies?.Past(who.Token.Id, message.MessageId) is { } past)
        {
            Note(call, who, target, now, DateTimeOffset.Now, true, "Повтор: сообщение уже приходило.",
                past.Created + past.Updated + past.Deleted, "");
            return IngressAnswer.Ok(past.ToJson());
        }

        var started = DateTimeOffset.Now;

        // Записи пишутся от имени РОБОТА: в истории изменений должно стоять его имя, иначе
        // чужие записи неотличимы от своих и разбирать потом нечего.
        InboxReceipt receipt;
        using (CurrentUser.ActAs(who.Token.RobotKey, who.Token.Name))
        {
            try
            {
                receipt = InboxIntake.Apply(message, target.Constructor, target.DbId, source, now);
            }
            catch (PermissionDeniedException denied)
            {
                // Права того, кто ОТКРЫЛ базу на этой машине: слушает его процесс, и обмен
                // не может делать его руками того, что ему самому запрещено.
                receipt = InboxReceipt.Rejected(source, message.MessageId, message.Collection,
                                                message.Rows,
                                                "на компьютере-приемнике: " + denied.Message, now);
            }
        }

        if (receipt.Accepted && replies is not null
            && !replies.Remember(who.Token.Id, message.MessageId, receipt, now))
            Serilog.Log.Warning("Прием по сети: ответ на сообщение «{Id:l}» не запомнился — "
                + "повторная доставка будет обработана заново", message.MessageId);

        PermissionService.MarkRobotSeenIsolated(target.Constructor.DbPath, who.Token.RobotKey, now);
        registry.MarkTokenUsed(who.Token.Id, now);

        var answer = new IngressAnswer(receipt.Accepted ? 200 : 422, receipt.ToJson());
        Note(call, who, target, now, started, receipt.Accepted, receipt.Explanation,
            receipt.Created + receipt.Updated + receipt.Deleted, call.Body ?? "");
        return answer;
    }

    private static IngressAnswer Refuse(int status, string explanation, IngressCall call, Caller who,
                                        DatabaseManager target, DateTimeOffset now)
    {
        Note(call, who, target, now, DateTimeOffset.Now, false, explanation, 0, "");
        return IngressAnswer.Problem(status, explanation);
    }

    // Строка рабочего журнала обмена — от имени токена: по нему видно, какая программа приходила.
    private static void Note(IngressCall call, Caller who, DatabaseManager target, DateTimeOffset now,
                              DateTimeOffset started, bool ok, string explanation, int rows, string body) =>
        RunJournal.Add(RunEntry.New(RunChannel.Ingress, who.Token.Name,
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
