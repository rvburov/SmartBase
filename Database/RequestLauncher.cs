using SmartBase.Integrations;

namespace SmartBase.Database;

/// <summary>
/// Один запуск запроса целиком: замок на исходящем, сама отправка, строка рабочего журнала и
/// отметка о том, что направлением воспользовались.
///
/// Собрано в одном месте не ради краткости. Эти четыре шага связаны правилами, которые нигде не
/// записаны как код, а живут в порядке вызовов: замок спрашивается ДО первого сокета, строка
/// журнала пишется и для НЕУДАЧНОГО запуска, в накопительный расход идет только то, что дошло до
/// сети, а отметка о простое ставится по тому же признаку. Повтори эту цепочку у второй кнопки —
/// и однажды она повторится не целиком, причем не сразу заметно: обмен продолжит работать, просто
/// перестанет считаться или писаться.
///
/// Зовется с UI-потока и из службы сценариев: реестр живет на общем контексте app.db.
/// </summary>
public static class RequestLauncher
{
    /// <summary>
    /// Отправить и отчитаться. Возвращает итог запуска — показывать его человеку или разбирать
    /// дальше решает вызывающий: у кнопки и у ночного сценария это разные вещи.
    /// </summary>
    /// <param name="registry">Реестр интеграций.</param>
    /// <param name="connection">Соединение запроса.</param>
    /// <param name="request">Что запускаем.</param>
    /// <param name="payload">Тело из коллекции (<see cref="OutgoingSource.Build"/>);
    /// <c>null</c> — запрос ничего из коллекции не уносит.</param>
    /// <param name="dbId">База, которую задевает обмен; 0 — ни одной.</param>
    /// <param name="startedAt">Момент начала — им же помечается строка журнала.</param>
    /// <param name="now">Момент для замка и отметки о простое.</param>
    /// <param name="scenario">Имя сценария; пусто — кнопку нажал человек.</param>
    /// <param name="step">Отметка шага («шаг 2 из 5 · запрос»); пусто — запуск не из сценария.</param>
    /// <param name="ct">Отмена.</param>
    public static async Task<RunOutcome> SendAsync(
        IntegrationsRegistry  registry,
        IntegrationConnection connection,
        IntegrationRequest    request,
        OutgoingPayload?      payload,
        int                   dbId,
        DateTimeOffset        startedAt,
        DateTimeOffset        now,
        string                scenario = "",
        string                step     = "",
        CancellationToken     ct       = default)
    {
        // Значения единственной уходящей записи — для подстановки {{поле.Имя}} В ПУТИ запроса.
        // Записей не одна — подстановки нет, и запуск остановится с понятным отказом: путь
        // собирается один раз, и подставить в него запись из трех значило бы отправить две
        // оставшиеся по чужому адресу.
        var context = VariableContext.Now(registry.LastRun(request.Id), connection.Address,
                                          payload?.Single);

        // Замок на исходящем: подтверждено ли направление и лег ли след. Читающий запрос сюда не
        // попадает — ему подтверждать нечего.
        var verdict = OutwardGuard.Check(registry, connection, request, context,
                                         scenario, dbId, now, payload);

        var outcome = await RequestRunner.RunAsync(
            connection, request, registry.Secret(connection.Id), context, verdict.Trouble,
            payload, ct);

        // Строка пишется и для НЕУДАЧНОГО запуска: именно их ищут, разбирая «почему записи не
        // появились». В расход при этом идет только то, что дошло до сети: запуск, остановленный
        // замком или политикой, обменом не был — засчитай его, и отказ по квоте продлевал бы сам
        // себя.
        RunJournal.Add(RunEntry.New(
            RunChannel.Request, request.Name, outcome.SafeAddress, outcome.Ok, outcome.Explanation,
            outcome.Rows, outcome.Bytes, outcome.Elapsed, dbId, startedAt,
            counts: outcome.Sent, direction: request.Id,
            sentBody: outcome.SentBody, gotBody: outcome.GotBody,
            scenario: scenario, step: step));

        // Вторая строка журнала безопасности — ИТОГ: код ответа, объем, время либо причина, по
        // которой до сети не дошло. Первая строка легла до сокета, и кода ответа в ней быть не
        // могло: его тогда еще не существовало. Без этой второй проверяющий не отличал бы
        // дошедшую отправку от застрявшей — «собирались отправить» выглядит одинаково и когда
        // ушло, и когда сервер ответил 500.
        //
        // Неудача записи здесь НИЧЕГО не отменяет, в отличие от первой строки: отправка уже
        // случилась, и притворяться, что ее можно отозвать, было бы ложью.
        if (request.Outward)
            SecurityJournal.Add(SecurityEntry.New(
                SecurityEvent.Result,
                allowed:     outcome.Ok,
                who:         Utils.CurrentUser.DisplayName,
                scenario:    scenario,
                address:     outcome.SafeAddress.Length > 0 ? outcome.SafeAddress : request.Path,
                fields:      "",
                rows:        outcome.Rows,
                bytes:       outcome.Bytes,
                bodyHash:    "",
                explanation: Result(outcome),
                dbId:        dbId,
                at:          now));

        // Направлением воспользовались — отсчет простоя начинается заново. По тому же признаку,
        // что и расход квоты: данные ДОШЛИ до сети.
        if (outcome.Sent) OutwardGuard.Used(registry, request, now);

        // Копия ушедшего — в папку аудита организации, если она ее назвала (10.1). Снимается
        // ПОСЛЕ отправки и только с того, что дошло до сети: копия неотправленного означала бы
        // отправку, которой не было. Читающие запросы зеркала не касаются — они ничего не
        // уносят, и складывать в папку аудита их ответы значит хоронить в ней настоящие копии.
        if (outcome.Sent && request.Outward && EgressMirror.Configured)
            EgressMirror.Note(request, outcome.SafeAddress, outcome.SentBody,
                              payload?.Rows ?? 0, payload?.Fields ?? "",
                              outcome.Ok, Utils.CurrentUser.DisplayName, scenario, now);

        return outcome;
    }

    /// <summary>
    /// Итог отправки одной строкой — той, что уедет проверяющему.
    ///
    /// Три случая и три ответа. Не дошло до сети — это не «ошибка обмена», а «данные не покинули
    /// машину», и для проверяющего разница принципиальная. Дошло и ответили — код ответа первым
    /// делом: по нему отличают «приняли» от «отказали». Дошло, но обрезали по потолку — сказано
    /// прямо: иначе неполный ответ выглядел бы полным.
    /// </summary>
    private static string Result(RunOutcome outcome)
    {
        if (!outcome.Sent) return "до сети не дошло: " + outcome.Explanation;

        var parts = new List<string> { $"код ответа {outcome.StatusCode}" };

        if (outcome.Rows  > 0) parts.Add($"записей {outcome.Rows:N0}");
        if (outcome.Bytes > 0) parts.Add($"байт {outcome.Bytes:N0}");

        parts.Add($"за {outcome.Elapsed.TotalSeconds:N1} с");

        if (outcome.Capped) parts.Add("ответ обрезан по потолку");
        if (!outcome.Ok && outcome.Explanation.Length > 0) parts.Add(outcome.Explanation);

        return string.Join(", ", parts);
    }
}
