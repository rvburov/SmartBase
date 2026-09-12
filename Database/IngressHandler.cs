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
/// <b>Наружу дверь не отдает ничего.</b> Обработчика, возвращающего записи, здесь нет: токену по
/// умолчанию выдается право писать и ни одного права читать, и «вычитать базу через свой же порт»
/// не должно быть выключено настройкой — его не должно существовать. Отдается только схема
/// собственных коллекций: имена и типы, без единого значения.
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
            && path != IngressApi.InboxPath)
            return IngressAnswer.Problem(404,
                $"Такого адреса нет. Есть: {IngressApi.PingPath}, "
              + $"{IngressApi.SchemaPath}, {IngressApi.InboxPath}.");

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
            IngressApi.PingPath   => Ping(token, target, access),
            IngressApi.SchemaPath => Schema(token, target, access),
            _                         => Inbox(call, token, target, access, registry, replies, now),
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

    // ── Сообщение ─────────────────────────────────────────────────────────────

    private static IngressAnswer Inbox(IngressCall call, AccessToken token, DatabaseManager target,
                                       RobotAccess access, IntegrationsRegistry registry,
                                       IngressReplies? replies, DateTimeOffset now)
    {
        var source = $"{call.Remote} {IngressApi.InboxPath}".Trim();

        if (access.Trouble(PermAction.CreateRecord) is { } trouble)
            return Refuse(403, Capitalize(trouble) + ".", token, source, now);

        var message = InboxMessage.TryParse(call.Body, out var error);

        if (message is null)
            return Refuse(400, "Сообщение не разобралось: " + error + ".", token, source, now);

        var problems = message.Problems();
        if (problems.Count > 0)
            return Refuse(400, Capitalize(string.Join("; ", problems)) + ".", token, source, now);

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
