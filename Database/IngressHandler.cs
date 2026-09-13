using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Integrations;
using SmartBase.Utils;

namespace SmartBase.Database;

/// <summary>
/// Что делать с обращением. Здесь — ВСЯ логика приема по сети, и ни одного сокета: слушатель
/// читает запрос и отдает ответ, а решает все этот класс. Разделение не косметическое —
/// иначе проверить прием можно было бы только открыв порт, а прием как раз то место, где
/// проверять надо каждую развилку.
///
/// <b>Порядок проверок и есть содержание класса:</b>
///
/// <list type="number">
///   <item><b>Токен.</b> Нет, не разобрался, выключен, не подошел — один и тот же ответ «неверный
///   токен»: подсказывать, ЧТО именно не так, значит помогать подбирать.</item>
///   <item><b>База.</b> Токен выдан под одну базу; она закрыта или недоступна — отказ с причиной.
///   Метка «закрытый контур» закрывает базу и для входящих (10.12).</item>
///   <item><b>Права робота.</b> Токен — это пользователь в <c>db_users</c> целевой базы, и маска
///   у него обычная. Недопущенный робот ждет так же, как человек.</item>
///   <item><b>Формат сообщения.</b> Тот же, что у файла в папке.</item>
///   <item><b>Повтор.</b> Сообщение с тем же номером возвращает ТОТ ЖЕ ответ.</item>
///   <item><b>Укладка</b> — общим кодом обоих транспортов, от имени робота.</item>
/// </list>
///
/// <b>Наружу дверь отдает данные только по выдаче.</b> Токену по умолчанию выдается право
/// писать и ни одного права читать; чтение (<c>/v1/records</c>, <c>/v1/changes</c>) открывается
/// отдельным замком — выдачей, подтвержденной мастер-паролем организации, с масками робота,
/// метками, суточным потолком и строкой в журнале безопасности на КАЖДЫЙ ответ. Ответ на
/// чтение — данные, покидающие программу, и рубежи у него те же, что у отправки: правило
/// «не записалось — не отправляем» действует и здесь, дословно (docs/ROADMAP-API.md, шаг 10).
/// </summary>
public static class IngressHandler
{
    /// <summary>
    /// Ответить на обращение. Исключений не бросает: любая беда — это ответ с кодом, потому что
    /// оборванное соединение отправитель истолкует как «программа не работает» и будет прав
    /// ровно в одном случае из десяти.
    /// </summary>
    /// <param name="call">Обращение.</param>
    /// <param name="registry">Реестр обмена на СВОЕМ подключении к app.db: обращения
    /// обрабатываются в фоне, а общий контекст принадлежит интерфейсу.</param>
    /// <param name="databases">Открытые базы — целевая ищется среди них. Закрытая база означает
    /// «сейчас недоступна», а не «неверный токен».</param>
    /// <param name="replies">Память о повторах; <c>null</c> — памяти нет, и повтор пройдет как
    /// новое сообщение.</param>
    /// <param name="now">Момент обращения.</param>
    public static IngressAnswer Handle(IngressCall call,
                                       IntegrationsRegistry registry,
                                       IReadOnlyList<DatabaseManager> databases,
                                       IngressReplies? replies,
                                       DateTimeOffset now)
    {
        var path = (call.Path ?? "").TrimEnd('/');
        if (path.Length == 0) path = "/";

        // Путь проверяется ДО токена: «такого адреса нет» не выдает ничего о токенах, а
        // отвечать «неверный токен» на опечатку в пути — верный способ искать не там.
        if (path != IngressApi.PingPath
            && path != IngressApi.SchemaPath
            && path != IngressApi.InboxPath
            && path != IngressApi.RecordsPath
            && path != IngressApi.ChangesPath)
            return IngressAnswer.Problem(404,
                $"Такого адреса нет. Есть: {IngressApi.PingPath}, "
              + $"{IngressApi.SchemaPath}, {IngressApi.InboxPath}, "
              + $"{IngressApi.RecordsPath}, {IngressApi.ChangesPath}.");

        var method = (call.Method ?? "").ToUpperInvariant();
        var wanted = path == IngressApi.InboxPath ? "POST" : "GET";

        if (method != wanted)
            return IngressAnswer.Problem(405, $"Этот адрес отвечает только на {wanted}.");

        if (registry.Verify(call.Token) is not { } token)
            return IngressAnswer.Problem(401, "Неверный токен.");

        registry.MarkTokenUsed(token.Id, now);

        if (databases.FirstOrDefault(d => d.DbId == token.DbId) is not { } target)
            return IngressAnswer.Problem(503,
                "База данных этого токена сейчас не открыта на компьютере-приемнике.");

        var repo = target.Constructor;

        // Метка «закрытый контур» закрывает базу и для входящих. Политика разрешить это пока не
        // умеет — поля у нее для этого нет, — и ответ поэтому один: нельзя (10.12).
        if (repo.GetDatabaseLabel() == ConfidentialityLabel.Closed)
            return IngressAnswer.Problem(403,
                "База помечена закрытым контуром: обмен по сети с ней запрещен.");

        var access = PermissionService.RobotIsolated(repo.DbPath, token.RobotKey);

        return path switch
        {
            IngressApi.PingPath    => Ping(token, target, access),
            IngressApi.SchemaPath  => Schema(token, target, access),
            IngressApi.RecordsPath => Records(call, token, target, access, registry, now),
            IngressApi.ChangesPath => Changes(call, token, target, access, registry, now),
            _                          => Inbox(call, token, target, access, registry, replies, now),
        };
    }

    // ── Проверка связи ────────────────────────────────────────────────────────

    /// <summary>
    /// «Жив ли слушатель и тот ли это токен». Отвечает и недопущенному роботу — но честно,
    /// полем <c>ready</c> и причиной: отправитель, настраивающий обмен, должен узнать про
    /// недостающий допуск здесь, а не из отказа первой же боевой посылки.
    /// </summary>
    private static IngressAnswer Ping(AccessToken token, DatabaseManager target, RobotAccess access)
    {
        var trouble = access.Trouble(PermAction.CreateRecord);

        return IngressAnswer.Ok(new
        {
            ok       = true,
            ready    = trouble is null,
            database = target.Name,
            token    = token.Name,
            trouble,
        });
    }

    // ── Схема ─────────────────────────────────────────────────────────────────

    private static IngressAnswer Schema(AccessToken token, DatabaseManager target, RobotAccess access)
    {
        // Схему отдаем только тому, кому есть куда писать: список коллекций базы, в которую
        // робота не пустили, — это уже сведения о базе, а не о его собственном обмене.
        if (access.Trouble(PermAction.CreateRecord) is { } trouble)
            return IngressAnswer.Problem(403, Capitalize(trouble) + ".");

        _ = token;

        return IngressAnswer.Ok(
            ApiDescription.Json(target.Name, target.Constructor.ApiSchemaIsolated()));
    }

    // ── Чтение по выдаче ──────────────────────────────────────────────────────

    /// <summary>
    /// Отдать страницу записей (docs/ROADMAP-API.md, шаг 10). Порядок проверок — от дешевых к
    /// дорогим, и каждый отказ называет себя: маска робота, параметры, коллекция, выдача, ее
    /// срок, суточный потолок — и только потом чтение. Ответ уходит ПОСЛЕ строки в журнале
    /// безопасности: не записалось — не отдаем, дословно как у отправки.
    /// </summary>
    private static IngressAnswer Records(IngressCall call, AccessToken token,
                                         DatabaseManager target, RobotAccess access,
                                         IntegrationsRegistry registry, DateTimeOffset now)
    {
        var source = $"{call.Remote} {IngressApi.RecordsPath}".Trim();

        if (Prepare(call, token, target, access, registry, source, now) is not { } ready)
            return _refusal!;

        var (grant, collection, collectionId) = ready;

        if (ParseMoment(call.Param("since"), out var since) is { } badSince)
            return Refuse(400, badSince, token, source, now);

        if (ParseNumber(call.Param("after"), out var after) is { } badAfter)
            return Refuse(400, badAfter, token, source, now);

        ParseNumber(call.Param("limit"), out var limit);

        var started = DateTimeOffset.Now;

        ConstructorRepository.ApiRows read;
        try
        {
            read = target.Constructor.ApiReadIsolated(
                collectionId, grant.FieldKeys, since, (int)after,
                limit > 0 ? (int)limit : ConstructorRepository.ApiPage);
        }
        catch (PermissionDeniedException denied)
        {
            // Права того, кто ОТКРЫЛ базу на этой машине: слушает его процесс, и обмен не
            // может читать его руками то, что ему самому не показывают.
            return RefuseRead(403, "На компьютере-приемнике: " + denied.Message,
                              token, grant, collection, source, now);
        }

        if (read.Trouble is { } cannot)
            return RefuseRead(403, Capitalize(cannot) + ".", token, grant, collection, source, now);

        var rows = read.Rows.Select(r => new Dictionary<string, object>
        {
            ["id"]      = r.Id,
            ["updated"] = r.Updated,
            // Значения — отдельным полем, а не вперемешку со служебными: свойство, названное
            // «id», иначе перезаписало бы ключ записи.
            ["values"]  = Columns(read.Columns, r.Values),
        }).ToList();

        var body = IngressAnswer.Ok(new
        {
            ok         = true,
            collection,
            rows,
            next       = read.More && read.Rows.Count > 0 ? (int?)read.Rows[^1].Id : null,
        }).Body;

        return Deliver(body, read.Rows.Count, string.Join(", ", read.Columns),
                       $"чтение коллекции «{collection}»",
                       call, token, target, grant, collection, registry, source, started, now);
    }

    /// <summary>
    /// Отдать хвост журнала изменений коллекции — то, на что не отвечает чтение записей: «что
    /// УДАЛЯЛИ». В журнале имена, а не ключи записей, — он ведется для человека, и об этом
    /// прямо сказано в описании API.
    /// </summary>
    private static IngressAnswer Changes(IngressCall call, AccessToken token,
                                         DatabaseManager target, RobotAccess access,
                                         IntegrationsRegistry registry, DateTimeOffset now)
    {
        var source = $"{call.Remote} {IngressApi.ChangesPath}".Trim();

        if (Prepare(call, token, target, access, registry, source, now) is not { } ready)
            return _refusal!;

        var (grant, collection, _) = ready;

        if (ParseNumber(call.Param("after"), out var after) is { } badAfter)
            return Refuse(400, badAfter, token, source, now);

        ParseNumber(call.Param("limit"), out var limit);

        var started = DateTimeOffset.Now;

        ConstructorRepository.ApiChangesPage tail;
        try
        {
            tail = target.Constructor.ApiChangesIsolated(
                collection, after, limit > 0 ? (int)limit : ConstructorRepository.ApiPage);
        }
        catch (PermissionDeniedException denied)
        {
            return RefuseRead(403, "На компьютере-приемнике: " + denied.Message,
                              token, grant, collection, source, now);
        }

        if (tail.Trouble is { } stopped)
            return RefuseRead(403, Capitalize(stopped) + ".", token, grant, collection, source, now);

        var body = IngressAnswer.Ok(new
        {
            ok         = true,
            collection,
            changes    = tail.Changes.Select(c => new
            {
                n      = c.Id,
                at     = c.At,
                action = c.Action,
                target = c.Target,
                name   = c.Name,
            }).ToList(),
            next       = tail.More && tail.Changes.Count > 0 ? (long?)tail.Changes[^1].Id : null,
        }).Body;

        return Deliver(body, tail.Changes.Count, "",
                       $"журнал изменений коллекции «{collection}»",
                       call, token, target, grant, collection, registry, source, started, now);
    }

    // Отказ, собранный общей проверкой Prepare. Поле, а не пара out/кортеж, потому что отказов
    // у проверки шесть видов и каждый уже готовым ответом; поток обращений последовательный
    // (слушатель обрабатывает по одному), гонки за поле нет.
    [ThreadStatic] private static IngressAnswer? _refusal;

    /// <summary>
    /// Общие проверки обоих чтений: маска робота, параметр коллекции, сама коллекция, выдача,
    /// ее срок и суточный потолок. <c>null</c> — отказ, и он лежит в <see cref="_refusal"/>.
    /// </summary>
    private static (ReadGrant Grant, string Collection, int CollectionId)? Prepare(
        IngressCall call, AccessToken token, DatabaseManager target, RobotAccess access,
        IntegrationsRegistry registry, string source, DateTimeOffset now)
    {
        // Маска чтения — отдельная и по умолчанию ее НЕТ: роботу выдают право писать, а
        // читать разрешает администратор той же панелью, что человеку.
        if (access.Trouble(Core.Models.PermAction.ViewCollection) is { } trouble)
        {
            _refusal = RefuseRead(403, Capitalize(trouble) + ".", token, null, "", source, now);
            return null;
        }

        var collection = call.Param("collection");

        if (collection.Length == 0)
        {
            _refusal = Refuse(400, "Не назван параметр «collection»: какую коллекцию читать.",
                              token, source, now);
            return null;
        }

        if (target.Constructor.CollectionIdByNameIsolated(collection) is not { } collectionId)
        {
            _refusal = Refuse(404, $"Коллекции «{collection}» в этой базе нет.",
                              token, source, now);
            return null;
        }

        // Выдача — сам замок. Ее отсутствие и ее просроченность называются РАЗНЫМИ словами:
        // первое лечится у администратора, второе — новым подтверждением, и слать человека
        // не туда значит потерять ему день.
        if (registry.GrantFor(token.Id, collectionId) is not { } grant)
        {
            _refusal = RefuseRead(403,
                $"Чтение коллекции «{collection}» этому токену не разрешено: выдачи нет. "
                + "Выдачу подтверждает ответственный за интеграции на компьютере-приемнике.",
                token, null, collection, source, now);
            return null;
        }

        if (grant.Trouble(now) is { } stale)
        {
            _refusal = RefuseRead(403, "Выдача не действует: " + stale + ".",
                                  token, grant, collection, source, now);
            return null;
        }

        // Потолок — свой у каждой выдачи и только по журналу: не по чему считать расход —
        // значит, потолок выбран. Битый счетчик не должен снимать ограничение.
        if (RunJournal.Current is not { } journal)
        {
            _refusal = RefuseRead(503,
                "Журнал обмена недоступен: расход потолка не по чему считать, чтение остановлено.",
                token, grant, collection, source, now);
            return null;
        }

        var policy = EgressGate.Policy;

        if (policy.Any
            && journal.ReadBytesSince(now - TimeSpan.FromDays(1), grant.Id)
               >= policy.MaxBytesPerDay)
        {
            _refusal = RefuseRead(429,
                "Суточный потолок этой выдачи выбран — окно скользящее, попробуйте позже.",
                token, grant, collection, source, now);
            return null;
        }

        return (grant, collection, collectionId);
    }

    /// <summary>
    /// Отдать готовый ответ чтения — журналы, отметки, тело. Строка в журнале безопасности
    /// идет ПЕРВОЙ, и неудача записи останавливает выдачу: данные, о которых не осталось
    /// следа, не покидают программу.
    /// </summary>
    private static IngressAnswer Deliver(string body, int rows, string fields, string what,
                                         IngressCall call, AccessToken token,
                                         DatabaseManager target, ReadGrant grant,
                                         string collection, IntegrationsRegistry registry,
                                         string source, DateTimeOffset started,
                                         DateTimeOffset now)
    {
        var bytes = System.Text.Encoding.UTF8.GetByteCount(body);

        var written = SecurityJournal.Add(SecurityEntry.New(
            SecurityEvent.Read, allowed: true,
            who: token.Name, scenario: "",
            address: $"{call.Path}?collection={collection}",
            fields: fields, rows: rows, bytes: bytes,
            bodyHash: SecurityJournal.Sha256(body),
            explanation: what, dbId: token.DbId, at: now, confirmed: grant.Confirmed));

        if (!written)
            return Refuse(503,
                "Журнал безопасности недоступен: чтение остановлено — данные, о которых не "
                + "осталось записи, не отдаются.", token, source, now);

        RunJournal.Add(RunEntry.New(
            RunChannel.Ingress, token.Name, source, ok: true,
            $"{Capitalize(what)}: отдано записей {rows}.", rows, bytes,
            DateTimeOffset.Now - started, token.DbId, now,
            counts: false, direction: grant.Id));

        // Отметки — от них считается простой выдачи и строка «последнее обращение» у робота.
        registry.MarkGrantUsed(grant.Id, now);
        PermissionService.MarkRobotSeenIsolated(target.Constructor.DbPath, token.RobotKey, now);

        return IngressAnswer.Ok(body);
    }

    /// <summary>
    /// Отказ чтения: строка в оба журнала. В журнал безопасности — потому что отказанная
    /// попытка чтения такой же ответ проверяющему, как отказанная отправка: «программа
    /// попробовала, и ей не дали» и есть доказательство, что рамки работают.
    /// </summary>
    private static IngressAnswer RefuseRead(int status, string explanation, AccessToken token,
                                            ReadGrant? grant, string collection, string source,
                                            DateTimeOffset now)
    {
        SecurityJournal.Add(SecurityEntry.New(
            SecurityEvent.Read, allowed: false,
            who: token.Name, scenario: "",
            address: collection.Length > 0
                ? $"{IngressApi.RecordsPath}?collection={collection}"
                : IngressApi.RecordsPath,
            fields: "", rows: 0, bytes: 0, bodyHash: "",
            explanation: explanation, dbId: token.DbId, at: now,
            confirmed: grant?.Confirmed ?? ""));

        return Refuse(status, explanation, token, source, now);
    }

    /// <summary>Значения строки под именами колонок — в порядке выдачи.</summary>
    private static Dictionary<string, string> Columns(IReadOnlyList<string> names,
                                                      string[] values)
    {
        var row = new Dictionary<string, string>(names.Count);

        for (var i = 0; i < names.Count; i++)
            row[names[i]] = i < values.Length ? values[i] : "";

        return row;
    }

    /// <summary>Момент из параметра; вернувшаяся строка — отказ словами.</summary>
    private static string? ParseMoment(string text, out string normalized)
    {
        normalized = "";
        if (text.Length == 0) return null;

        if (!DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var moment))
            return "Параметр «since» не разобрался: нужен момент вида 2026-09-12T10:00:00Z.";

        normalized = moment.ToUniversalTime()
                           .ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        return null;
    }

    /// <summary>Число из параметра; вернувшаяся строка — отказ словами.</summary>
    private static string? ParseNumber(string text, out long value)
    {
        value = 0;
        if (text.Length == 0) return null;

        return long.TryParse(text, System.Globalization.NumberStyles.Integer,
                             System.Globalization.CultureInfo.InvariantCulture, out value)
               && value >= 0
            ? null
            : "Числовой параметр не разобрался: нужно целое число не меньше нуля.";
    }

    // ── Сообщение ─────────────────────────────────────────────────────────────

    private static IngressAnswer Inbox(IngressCall call, AccessToken token, DatabaseManager target,
                                       RobotAccess access, IntegrationsRegistry registry,
                                       IngressReplies? replies, DateTimeOffset now)
    {
        var source = $"{call.Remote} {IngressApi.InboxPath}".Trim();

        // Разбор — ДО маски: какое право спрашивать, зависит от режима сообщения. Формат
        // ничего о базе не выдает, так что недопущенный робот здесь не узнает лишнего.
        var message = InboxMessage.TryParse(call.Body, out var error);

        if (message is null)
            return Refuse(400, "Сообщение не разобралось: " + error + ".", token, source, now);

        var problems = message.Problems();
        if (problems.Count > 0)
            return Refuse(400, Capitalize(string.Join("; ", problems)) + ".", token, source, now);

        // Каждый режим спрашивает СВОЕ право — то же, которое проверила бы панель у человека.
        // По умолчанию роботу выдана только запись; удаление и подготовку коллекций открывает
        // администратор базы обычной панелью (docs/ROADMAP-API.md, шаг 11).
        PermAction[] rights = message.Mode switch
        {
            InboxMode.Delete           => [PermAction.DeleteRecord],
            InboxMode.EnsureCollection => [PermAction.CreateCollection, PermAction.CreateField],
            _                          => [PermAction.CreateRecord],
        };

        foreach (var right in rights)
            if (access.Trouble(right) is { } trouble)
                return Refuse(403, Capitalize(trouble) + ".", token, source, now);

        // Повтор возвращает ТОТ ЖЕ ответ, что и первая доставка: отправитель, не дождавшийся
        // ответа, повторяет запрос именно ради него, и «уже приходило» для него не лучше молчания.
        if (replies?.Past(token.Id, message.MessageId) is { } past)
        {
            RunJournal.Add(RunEntry.New(
                RunChannel.Ingress, token.Name, source, ok: true,
                "повтор: сообщение уже приходило", past.Created + past.Updated, 0, TimeSpan.Zero,
                token.DbId, now, counts: false, direction: 0));

            return IngressAnswer.Ok(past.ToJson());
        }

        var started = DateTimeOffset.Now;

        // Записи пишутся от имени РОБОТА: в истории изменений должно стоять его имя, иначе
        // чужие записи неотличимы от своих и разбирать потом нечего.
        InboxReceipt receipt;
        using (CurrentUser.ActAs(token.RobotKey, token.Name))
        {
            try
            {
                receipt = InboxIntake.Apply(message, target.Constructor, token.DbId, source, now);
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

        // Ответ запоминается ДО того, как уедет отправителю: не записалась память — повтор
        // обработается второй раз, и знать об этом надо здесь, а не при разборе дублей.
        if (receipt.Accepted && replies is not null
            && !replies.Remember(token.Id, message.MessageId, receipt, now))
            Serilog.Log.Warning("Прием по сети: ответ на сообщение «{Id:l}» не запомнился — "
                              + "повторная доставка будет обработана заново", message.MessageId);

        PermissionService.MarkRobotSeenIsolated(target.Constructor.DbPath, token.RobotKey, now);
        registry.MarkTokenUsed(token.Id, now);

        RunJournal.Add(RunEntry.New(
            RunChannel.Ingress, token.Name, source, receipt.Accepted, receipt.Explanation,
            receipt.Created + receipt.Updated, call.Body?.Length ?? 0,
            DateTimeOffset.Now - started, token.DbId, now, counts: false, direction: 0));

        // Отказ по содержанию сообщения — это 422: запрос понят и дошел, но сделать по нему
        // нечего. Отвечать на него 400 значило бы сказать отправителю «ты неправильно
        // сформировал запрос», и он бы правил не то.
        return new IngressAnswer(receipt.Accepted ? 200 : 422, receipt.ToJson());
    }

    /// <summary>
    /// Отказ со строкой журнала. Отклоненное обращение записывается наравне с принятым: разбор
    /// «почему записи не появились» начинается именно с них, а молчаливый отказ выглядит так же,
    /// как выключенный слушатель.
    /// </summary>
    private static IngressAnswer Refuse(int status, string explanation, AccessToken token,
                                        string source, DateTimeOffset now)
    {
        RunJournal.Add(RunEntry.New(
            RunChannel.Ingress, token.Name, source, ok: false, explanation,
            0, 0, TimeSpan.Zero, token.DbId, now, counts: false, direction: 0));

        return IngressAnswer.Problem(status, explanation);
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0]) + text[1..];
}
