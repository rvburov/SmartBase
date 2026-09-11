using System.Text;
using System.Text.Json;
using SmartBase.Integrations;
using SmartBase.Utils;

namespace SmartBase.Database;

/// <summary>
/// Решение по одной исходящей отправке.
/// </summary>
/// <param name="Trouble">Почему нельзя — готовой фразой; <c>null</c> — можно.</param>
/// <param name="Confirmed">Кто подтвердил направление; пусто — подтверждения не было или оно
/// не требуется.</param>
public sealed record OutwardVerdict(string? Trouble, string Confirmed = "")
{
    /// <summary>
    /// Согласовывать нечего: запрос данных наружу не уносит.
    ///
    /// Причина названа по имени не для красоты: у записи есть еще и конструктор копирования, и
    /// <c>new(null)</c> уходит в него — молча, с обещанием уронить программу при первом же
    /// обращении.
    /// </summary>
    public static readonly OutwardVerdict Free = new(Trouble: null);

    /// <summary>Отправлять можно.</summary>
    public bool Allowed => string.IsNullOrEmpty(Trouble);
}

/// <summary>
/// Замок на исходящей отправке (docs/ROADMAP-API.md, 10.8 и 10.10).
///
/// Подтверждение направления и журнал безопасности появились раньше — здесь они наконец
/// соединяются с самой отправкой. Один вызов отвечает на оба вопроса сразу, и это не ради
/// краткости: спроси их по отдельности — и однажды в новом месте спросят только первый.
///
/// <list type="number">
///   <item><b>Подтверждено ли направление.</b> Ответ берется у реестра — у того же метода,
///   которым карточка запроса рисует состояние. Второй его реализации нет нигде.</item>
///   <item><b>Лег ли след.</b> Строка журнала безопасности пишется ДО первого сокета, и
///   «не записалось — не отправляем» живет здесь: <see cref="SecurityJournal.Add"/> вернул
///   <c>false</c> — отправка отменяется. Правило неудобное намеренно: отправка, о которой не
///   осталось следа, — ровно то, чего требование службы безопасности не допускает. Журнал не
///   открылся вовсе — исходящего обмена на этой машине просто нет (см. <c>App</c>).</item>
/// </list>
///
/// <b>Читающие запросы сюда не приходят.</b> У них нечего подтверждать, и строки они не
/// оставляют: обмен, опрашивающий чужую программу каждые пять минут, за сутки положил бы в
/// журнал проверяющего три сотни строк «сходили и вернулись», среди которых настоящее событие
/// уже не разглядеть.
///
/// <b>Строка пишется одна и до отправки.</b> Она отвечает на вопрос проверяющего «что и куда
/// БЫЛО РАЗРЕШЕНО уйти и с чьего подтверждения»; ответила ли чужая программа — вопрос рабочий, и
/// на него отвечает журнал запусков. Писать вторую строку по итогу значило бы удвоить журнал
/// ради сведений, которые уже есть рядом.
///
/// Зовется с UI-потока: реестр живет на общем контексте app.db.
/// </summary>
public static class OutwardGuard
{
    /// <summary>
    /// Сколько имен полей попадает в строку журнала. Тело бывает и на тысячу колонок, а строка
    /// журнала — то, что читают глазами; остальные обозначаются числом.
    /// </summary>
    public const int MaxFields = 40;

    /// <summary>
    /// Почему запросу нельзя отправлять — готовой фразой; <c>null</c> — можно. Журнала не
    /// касается: это вопрос сухого прогона и карточки, а следа заслуживает только настоящая
    /// попытка.
    /// </summary>
    public static string? Refusal(IntegrationsRegistry registry, IntegrationRequest request,
                                  DateTimeOffset now) =>
        registry.OutwardTrouble(request, now) is { } trouble
            ? "исходящее направление: " + trouble
            : null;

    /// <summary>
    /// Проверить направление и оставить след. Возвращает решение: <see cref="OutwardVerdict.Trouble"/>
    /// отдают в <c>RequestRunner.RunAsync</c> как есть.
    /// </summary>
    /// <param name="scenario">Имя сценария; пусто — человек нажал кнопку сам.</param>
    /// <param name="dbId">Ключ базы, которую задевает обмен; 0 — ни одной.</param>
    public static OutwardVerdict Check(IntegrationsRegistry  registry,
                                       IntegrationConnection connection,
                                       IntegrationRequest    request,
                                       VariableContext       context,
                                       string                scenario,
                                       int                   dbId,
                                       DateTimeOffset        now)
    {
        if (!request.Outward) return OutwardVerdict.Free;

        var trouble   = Refusal(registry, request, now);
        var confirmed = registry.Direction(request.Id)?.Confirmed ?? "";

        // Что именно ушло бы — собирается тем же кодом, что и сама отправка, и тем же, что
        // показывает сухой прогон. Собирается ДАЖЕ ДЛЯ ОТКАЗА: проверяющему, разбирающему
        // заблокированную попытку, первым делом нужно знать, куда и с чем в нее шли.
        var preview = RequestRunner.Preview(
            connection, request, registry.Secret(connection.Id), context);

        var body = Encoding.UTF8.GetBytes(preview.Body);
        var (rows, fields) = Shape(preview.Body);

        var written = SecurityJournal.Add(SecurityEntry.New(
            SecurityEvent.Send,
            allowed:     trouble is null && preview.Ok,
            who:         CurrentUser.DisplayName,
            scenario:    scenario,
            address:     preview.SafeAddress.Length > 0 ? preview.SafeAddress : request.Path,
            fields:      fields,
            rows:        rows,
            bytes:       body.LongLength,
            bodyHash:    SecurityJournal.Sha256(body),
            explanation: Explanation(trouble, preview, registry.Direction(request.Id)),
            dbId:        dbId,
            at:          now,
            confirmed:   confirmed));

        if (trouble is { Length: > 0 }) return new OutwardVerdict(trouble, confirmed);

        return written
            ? new OutwardVerdict(null, confirmed)
            : new OutwardVerdict(
                "след об отправке не лег в журнал безопасности, и отправка отменена: обмен, "
                + "о котором не осталось записи, не допускается. Сообщите ответственному за "
                + "интеграции", confirmed);
    }

    /// <summary>
    /// Отметить, что направлением воспользовались, — от этой отметки идет отсчет простоя.
    /// Ставится по тому же признаку, что и расход квоты: данные ДОШЛИ до сети. Запуск,
    /// остановленный замком или политикой, дверью не пользовался, и продлевать ею жизнь забытому
    /// обмену незачем.
    /// </summary>
    public static void Used(IntegrationsRegistry registry, IntegrationRequest request,
                            DateTimeOffset now)
    {
        if (request.Outward) registry.MarkDirectionUsed(request.Id, now);
    }

    // ── Что пишем в строку ────────────────────────────────────────────────────

    /// <summary>
    /// Пояснение к строке журнала. Три случая и три разных ответа: не разрешили, разрешили но не
    /// состоится, разрешили и ушло бы. Слить их в одно «отказано» значило бы заставить
    /// проверяющего гадать, чего именно не хватило.
    /// </summary>
    private static string Explanation(string? trouble, RunPreview preview,
                                      OutgoingDirection? direction)
    {
        if (trouble is { Length: > 0 }) return trouble;

        if (!preview.Ok) return "направление подтверждено, но запуск не состоится: "
                              + preview.Explanation;

        return direction is null
            ? "разрешено"
            : "разрешено: " + direction.Describe();
    }

    /// <summary>
    /// Сколько записей уносит тело и какие в нем поля. Имена — да, значения — никогда: журнал
    /// выгружают проверяющему целиком, и значение поля в нем стало бы второй утечкой следом за
    /// первой.
    ///
    /// Тело не JSON (форма, обычный текст, пусто) — полей не называем вовсе. Угадывать их
    /// разбором текста значило бы однажды назвать полем кусок значения.
    /// </summary>
    private static (int Rows, string Fields) Shape(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (0, "");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling     = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException)
        {
            return (0, "");
        }

        using (document)
        {
            var root = document.RootElement;

            return root.ValueKind switch
            {
                JsonValueKind.Object => (1, Names(root)),
                JsonValueKind.Array  => (root.GetArrayLength(),
                                         Names(root.EnumerateArray().FirstOrDefault())),
                _                    => (0, ""),
            };
        }
    }

    /// <summary>Имена полей объекта через запятую; хвост длинного списка — числом.</summary>
    private static string Names(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object) return "";

        var names = node.EnumerateObject().Select(p => p.Name).ToList();

        return names.Count <= MaxFields
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(MaxFields)) + $" и еще {names.Count - MaxFields}";
    }
}
