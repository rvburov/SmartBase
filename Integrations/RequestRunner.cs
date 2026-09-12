using System.Text;
using System.Text.Json;

namespace SmartBase.Integrations;

/// <summary>
/// Итог запуска запроса: собранные записи и то, чем запуск закончился.
/// </summary>
/// <param name="Ok">Записи получены — их можно укладывать в коллекцию.</param>
/// <param name="Explanation">Что случилось, человеческим языком: и отказ политики, и сетевой
/// сбой, и «уперлись в потолок строк».</param>
/// <param name="Json">Записи одним массивом JSON — то, что читает <c>JsonTextSource</c>. При
/// неудаче пустой: половина выгрузки, уложенная в коллекцию, хуже, чем ничего, — заметить
/// недостачу потом невозможно.</param>
/// <param name="Rows">Сколько записей собрано; при неудаче — сколько успели до нее.</param>
/// <param name="Pages">Сколько порций пришлось запросить.</param>
/// <param name="Bytes">Сколько байт прошло через дверь суммарно: тела, ушедшие наружу, и тела
/// ответов, пришедших обратно. По этому числу считается суточный потолок объема, поэтому
/// отправленное в нем не учесть нельзя: потолок заведен против выноса базы «по капле»
/// (docs/ROADMAP-API.md, 10.6), а выносят данные как раз телом запроса. Считать один ответ
/// значило бы мерить противоположное — сколько данных в базу пришло.</param>
/// <param name="Elapsed">Сколько заняло.</param>
/// <param name="SafeAddress">Куда ходили — без секрета, для журнала.</param>
/// <param name="StatusCode">Код последнего ответа; 0 — ответа не было.</param>
/// <param name="Capped">Сбор остановлен потолком строк из политики, а не концом данных.</param>
/// <param name="Sent">Хоть одна порция ушла в сеть. Запуск, остановленный до сокета — отказом
/// политики, выбранной квотой, незнакомой подстановкой, — обменом НЕ был, и в накопительный
/// расход его записывать нельзя: отказ по квоте продлевал бы сам себя, а часовое окно не
/// опустело бы никогда. Отказ чужой программы — другое дело: обращение состоялось.</param>
public sealed record RunOutcome(
    bool     Ok,
    string   Explanation,
    string   Json,
    int      Rows,
    int      Pages,
    long     Bytes,
    TimeSpan Elapsed,
    string   SafeAddress,
    int      StatusCode,
    bool     Capped,
    bool     Sent = false,
    string   SentBody = "",
    string   GotBody  = "")
{
    /// <param name="bytes">Объем ВСЕГО запуска, а не последней порции: неудача на сороковой
    /// странице не отменяет тридцати девяти прошедших, и в суточный потолок они уже вошли.</param>
    internal static RunOutcome Failed(
        string explanation, EgressResponse answer, int pages, int rows, long bytes, bool sent,
        string sentBody = "", string gotBody = "") =>
        new(false, explanation, "[]", rows, pages, bytes, answer.Elapsed,
            answer.SafeAddress, answer.StatusCode, false, sent, sentBody, gotBody);
}

/// <summary>
/// Запуск запроса: подставить переменные, отправить, при необходимости пройти по страницам и
/// собрать записи в один массив.
///
/// Слоем ниже лежит <see cref="EgressHttp"/> — здесь нет ни одного сетевого типа, только
/// последовательность вызовов и разбор ответа. Разделение то же, что у плана и транспорта:
/// решения отдельно, сокет отдельно.
///
/// Три правила, ради которых этот слой и существует:
///
/// <list type="number">
///   <item><b>Незнакомая подстановка останавливает запуск.</b> <c>{{опечатка}}</c> уехала бы в
///   чужую программу буквально, а пустой ответ потом искали бы неделю.</item>
///   <item><b>Страницы берутся до конца данных, но не дальше потолка.</b> Без разбивки выгрузка
///   молча обрывается на первой сотне записей — самая частая и самая незаметная беда обмена;
///   без потолка чужая программа с бесконечной пагинацией крутит цикл вечно.</item>
///   <item><b>Потолок строк берется из политики организации</b>, а не из настроек человека.
///   Настройка, которую можно поднять, — это не потолок.</item>
/// </list>
/// </summary>
/// <summary>
/// Сухой прогон: что уйдет наружу, если запустить запрос прямо сейчас (docs/ROADMAP-API.md, 10.9).
///
/// Собирается ТЕМ ЖЕ кодом, которым собирается настоящая отправка, — иначе экран показывал бы
/// одно, а уходило другое, и доверять такому показу было бы нельзя вовсе. Ни одного сокета при
/// этом не открывается: даже имя узла не разрешается, потому что сам запрос к DNS уже говорит
/// наружу, что программа этим адресом интересуется.
/// </summary>
/// <param name="Ok">Запуск состоялся бы.</param>
/// <param name="Explanation">Что помешает; пусто — ничто.</param>
/// <param name="Method">Метод первой порции.</param>
/// <param name="SafeAddress">Полный адрес БЕЗ секрета.</param>
/// <param name="Headers">Заголовки с замаскированным секретом.</param>
/// <param name="Body">Тело с подставленными переменными — ровно то, что уйдет.</param>
/// <param name="Outward">Уносит ли этот запрос данные наружу.</param>
public sealed record RunPreview(
    bool                             Ok,
    string                           Explanation,
    RequestMethod                    Method,
    string                           SafeAddress,
    IReadOnlyList<IntegrationHeader> Headers,
    string                           Body,
    bool                             Outward)
{
    /// <summary>Отказ до сборки вызова: показывать нечего, причина одна.</summary>
    public static RunPreview Blocked(string explanation, bool outward) =>
        new(false, explanation, RequestMethod.Get, "", [], "", outward);
}

public static class RequestRunner
{
    /// <summary>
    /// Сколько порций подряд разрешено запросить. Страховка от чужой программы, которая на
    /// любой номер страницы отвечает одним и тем же непустым списком: такой обмен иначе не
    /// кончается никогда.
    /// </summary>
    public const int MaxPages = 200;

    /// <summary>
    /// Показать, что уйдет, ничего не отправляя. Проверяются те же рамки и в том же порядке, что
    /// и при запуске: потолок строк, накопительные квоты, подстановки, белый список адресов и
    /// подтверждение исходящего направления.
    ///
    /// Показывается ПЕРВАЯ порция. Страниц бывает двести, и они отличаются одним параметром;
    /// показать их все значило бы утопить в них то единственное, ради чего сухой прогон и
    /// смотрят, — тело запроса.
    /// </summary>
    /// <param name="refusal">Готовая причина, по которой запуск не состоится, — ответ
    /// <c>OutwardGuard.Refusal</c>; <c>null</c> — ничто не мешает. Спрашивается снаружи, потому
    /// что подтверждения живут в app.db, а этот слой к базе не ходит. Приходит она уже целой
    /// фразой: собери ее здесь — и та же фраза собиралась бы вторым способом в отправке.</param>
    /// <param name="payload">Тело, собранное из коллекции исходящей привязкой; <c>null</c> —
    /// запрос ничего из коллекции не уносит, и тело берется из карточки. Обязательный параметр
    /// по той же причине, что и <paramref name="refusal"/>: сухой прогон, строка журнала
    /// безопасности и сама отправка обязаны говорить об ОДНОМ теле, а значение по умолчанию
    /// означало бы, что где-то его можно не передать — и экран показал бы одно, а ушло
    /// другое.</param>
    public static RunPreview Preview(
        IntegrationConnection connection,
        IntegrationRequest    request,
        string                secret,
        VariableContext       context,
        string?               refusal,
        OutgoingPayload?      payload)
    {
        var outward = request.Outward;

        if (refusal is { Length: > 0 }) return RunPreview.Blocked(refusal, outward);

        if (EgressGate.Policy.MaxRowsPerRun <= 0)
            return RunPreview.Blocked(
                "политика организации не разрешает получать записи: потолок строк за запуск — 0",
                outward);

        var quota = EgressGate.CheckQuota(DateTimeOffset.Now, request.Id);
        if (!quota.Allowed)
            return RunPreview.Blocked("запрещено политикой: " + quota.Explanation, outward);

        var page = Page(request, context, payload, cursor: null, number: 1, offset: 0, out var unknown);

        if (unknown.Count > 0)
            return RunPreview.Blocked(
                "неизвестные подстановки: " + string.Join(", ", unknown.Select(u => "{{" + u + "}}")),
                outward);

        // Объем отправляемого известен и здесь — ровно тот же вопрос и в том же месте, что и в
        // запуске. Сухой прогон заведен ради ответа «что уйдет», и «не уйдет вовсе, суточный
        // потолок выбран» — такой же ответ, который лучше получить до нажатия «В коллекцию».
        var volume = EgressGate.CheckQuota(
            DateTimeOffset.Now, request.Id, Encoding.UTF8.GetByteCount(page.Body));

        if (!volume.Allowed)
            return RunPreview.Blocked("запрещено политикой: " + volume.Explanation, outward);

        var plan = EgressPlan.Build(connection, page, secret, out var error);

        if (plan is null) return RunPreview.Blocked(error, outward);

        // Белый список спрашивается ПО ИМЕНИ и только: разрешение имени — уже обращение наружу,
        // а сухой прогон не должен говорить чужой сети ничего.
        var decision = EgressGate.Check(plan.Address);

        return new RunPreview(decision.Allowed,
                              decision.Allowed ? "" : "запрещено политикой: " + decision.Explanation,
                              plan.Method, plan.SafeAddress, plan.SafeHeaders, plan.Body, outward);
    }

    /// <summary>
    /// Запустить запрос и собрать записи.
    /// </summary>
    /// <param name="refusal">Почему отправлять нельзя — готовой фразой; <c>null</c> — можно.
    /// Ответ дает <c>OutwardGuard.Check</c>: подтверждено ли направление и лег ли след в журнал
    /// безопасности.
    ///
    /// Параметр <b>обязательный, и это главное в нем</b>. Значение по умолчанию означало бы, что
    /// вопрос «этому обмену разрешено уносить данные?» можно не задать — просто забыв про него в
    /// новом месте вызова. Так замок и обходят: не взломом, а вызовом в обход. Теперь не
    /// ответить на вопрос нельзя — не собирается.</param>
    /// <param name="payload">Тело, собранное из коллекции исходящей привязкой; <c>null</c> —
    /// запрос ничего из коллекции не уносит. Обязательный по той же причине, что и
    /// <paramref name="refusal"/>: тело, о котором отчитался сухой прогон и которое записано в
    /// журнал безопасности, обязано быть тем же, что уходит в сокет.</param>
    public static async Task<RunOutcome> RunAsync(
        IntegrationConnection connection,
        IntegrationRequest    request,
        string                secret,
        VariableContext       context,
        string?               refusal,
        OutgoingPayload?      payload,
        CancellationToken     ct = default)
    {
        // До всего остального: отказ направления — это отказ ДО первого сокета, и в
        // накопительный расход он не идет (Sent: false). Иначе неподтвержденный запрос выбирал
        // бы чужую квоту, ни разу никуда не сходив.
        if (refusal is { Length: > 0 })
            return new RunOutcome(false, refusal, "[]", 0, 0, 0, TimeSpan.Zero, "", 0, false);

        var limit = EgressGate.Policy.MaxRowsPerRun;

        if (limit <= 0)
            return new RunOutcome(false,
                "политика организации не разрешает получать записи: потолок строк за запуск — 0",
                "[]", 0, 0, 0, TimeSpan.Zero, "", 0, false);

        // Накопительные потолки спрашиваются ДО первого сокета: потолок, о котором узнают
        // задним числом, ничего не ограничивает.
        var moment = DateTimeOffset.Now;
        var quota  = EgressGate.CheckQuota(moment, request.Id);

        if (!quota.Allowed)
            return new RunOutcome(false, "запрещено политикой: " + quota.Explanation,
                                  "[]", 0, 0, 0, TimeSpan.Zero, "", 0, false);

        // Сколько еще можно получить за сутки. Спросить один раз перед запуском мало: выгрузка
        // на двести порций перешагнула бы любой суточный потолок, ни разу его не спросив.
        var budget = EgressGate.BytesLeft(moment, request.Id);

        var items   = new List<string>();
        var bytes   = 0L;
        var elapsed = TimeSpan.Zero;
        var pages   = 0;
        var last    = "";
        var status  = 0;

        // Ушла ли в сеть хоть одна порция. От этого зависит, идет ли запуск в накопительный
        // расход: остановленный до сокета обменом не был.
        var sent    = false;

        // Тела первой порции — для журнала запусков. Секрет из них вырезан, длина урезана.
        var sentBody = "";
        var gotBody  = "";

        // Метка следующей порции у курсорной разбивки; у страничной — номер, у сдвиговой — сдвиг.
        string? cursor = null;
        var     number = 1;
        var     offset = 0;

        while (pages < MaxPages)
        {
            var page = Page(request, context, payload, cursor, number, offset, out var unknown);

            if (unknown.Count > 0)
                return new RunOutcome(false,
                    "неизвестные подстановки: " + string.Join(", ", unknown.Select(u => "{{" + u + "}}")),
                    "[]", items.Count, pages, bytes, elapsed, "", 0, false);

            // Сколько эта порция унесет наружу. Меряется тело: записи уходят только им — путь
            // собирается из переменных запуска, и значений полей в нем не бывает по устройству
            // подстановок, а заголовки заданы карточкой соединения и от объема выгрузки не
            // зависят. Байты, а не знаки: в UTF-8 кириллица вдвое тяжелее, и потолок,
            // посчитанный по длине строки, на русских данных оказался бы вдвое выше заданного.
            var outgoing = Encoding.UTF8.GetByteCount(page.Body);

            // Единственная проверка объема, которая успевает что-то предотвратить: размер
            // отправляемого известен ДО сокета. Ответ так проверить нельзя — его размер
            // узнается по факту, и потолок ответа работает задним числом (ниже).
            if (bytes + outgoing > budget)
                return new RunOutcome(false,
                    $"выбран суточный потолок объема этого направления: осталось было "
                    + $"{budget:N0} байт, а отправка потребовала {bytes + outgoing:N0}",
                    "[]", items.Count, pages, bytes, elapsed, last, status, false, sent,
                    sentBody, gotBody);

            var answer = await EgressHttp.SendAsync(connection, page, secret, ct);

            // Тела запоминаются от ПЕРВОЙ порции и до всех проверок ниже: именно неудачные
            // запуски разбирают по телам, а неудача чаще всего и случается на первой порции.
            // Показывать при этом последнюю из сорока — значит отвечать не на тот вопрос:
            // человек ищет, что ушло и что пришло в ответ, а не что было в конце выгрузки.
            if (pages == 0)
            {
                sentBody = Journalled(page.Body, secret);
                gotBody  = Journalled(answer.Body, secret);
            }

            pages++;
            bytes  += outgoing + answer.Bytes;
            elapsed = answer.Elapsed > elapsed ? answer.Elapsed : elapsed;
            last    = answer.SafeAddress;
            status  = answer.StatusCode;
            sent   |= answer.Sent;

            // Ответ уже пришел, и это неизбежно: узнать его размер заранее нельзя. Зато
            // следующая порция не уйдет — а собранное отбрасывается целиком, как и при любой
            // другой неудаче посреди выгрузки.
            if (bytes > budget)
                return RunOutcome.Failed(
                    $"выбран суточный потолок объема этого направления: осталось было "
                    + $"{budget:N0} байт, а выгрузка потребовала {bytes:N0}",
                    answer, pages, items.Count, bytes, sent, sentBody, gotBody);

            if (!answer.Sent)
                return RunOutcome.Failed(
                    answer.Reason == EgressDenyReason.None
                        ? answer.Explanation
                        : "запрещено политикой: " + answer.Explanation,
                    answer, pages, items.Count, bytes, sent, sentBody, gotBody);

            if (!answer.Ok)
                return RunOutcome.Failed(
                    $"чужая программа ответила {answer.StatusCode}"
                    + (answer.Explanation.Length > 0 ? "; " + answer.Explanation : ""),
                    answer, pages, items.Count, bytes, sent, sentBody, gotBody);

            if (answer.Truncated)
                return RunOutcome.Failed(
                    "ответ длиннее потолка и обрезан — записи из него взять нельзя, "
                    + "разбейте выгрузку на страницы",
                    answer, pages, items.Count, bytes, sent, sentBody, gotBody);

            if (!TryTake(answer.Body, request, items, limit, out cursor, out var taken, out var error))
                return RunOutcome.Failed(
                    error, answer, pages, items.Count, bytes, sent, sentBody, gotBody);

            var capped = items.Count >= limit;

            // Конец данных: порция пустая, разбивки нет вовсе либо чужая программа больше не
            // дала метку следующей порции.
            var done = taken == 0
                    || request.Paging == RequestPaging.None
                    || (request.Paging == RequestPaging.Cursor && string.IsNullOrEmpty(cursor));

            if (done || capped)
                return new RunOutcome(true,
                    capped && !done
                        ? $"собрано {items.Count} записей — потолок политики на один запуск"
                        : "",
                    "[" + string.Join(",", items) + "]",
                    items.Count, pages, bytes, elapsed, last, status, capped && !done, sent,
                    sentBody, gotBody);

            number++;
            offset += taken;
        }

        return new RunOutcome(true,
            $"остановлено после {MaxPages} порций: чужая программа не сообщает о конце списка",
            "[" + string.Join(",", items) + "]",
            items.Count, pages, bytes, elapsed, last, status, true, sent, sentBody, gotBody);
    }

    /// <summary>
    /// Потолок тела, попадающего в журнал запусков. Тело нужно, чтобы разобрать неудачу, а не
    /// чтобы хранить выгрузку: ответ бывает и в тридцать мегабайт, а строк в журнале двадцать
    /// тысяч. Показанного куска хватает на то, ради чего тело и смотрят, — увидеть форму ответа
    /// и текст ошибки чужой программы.
    /// </summary>
    public const int JournalBodyLimit = 8 * 1024;

    /// <summary>
    /// Тело в том виде, в каком его можно положить в журнал: без секрета и не длиннее потолка.
    ///
    /// Секрет вырезается ТЕМ ЖЕ знаком, что и в заголовках (<c>EgressPlan.Mask</c>): человек,
    /// сверяющий журнал с сухим прогоном, не должен гадать, одно это и то же или разное. В теле
    /// секрету взяться неоткуда — он живет в заголовке или в адресе, — но «неоткуда» здесь
    /// значит «пока никто не вписал его руками», и полагаться на это нельзя.
    /// </summary>
    private static string Journalled(string? body, string secret)
    {
        var text = body ?? "";

        if (secret.Length > 0) text = text.Replace(secret, EgressPlan.Mask, StringComparison.Ordinal);

        return text.Length <= JournalBodyLimit
            ? text
            : text[..JournalBodyLimit] + $"\n… показано {JournalBodyLimit:N0} знаков из {text.Length:N0}";
    }

    // ── Одна порция ───────────────────────────────────────────────────────────

    /// <summary>
    /// Запрос за одну порцию: подстановки применены, параметр разбивки дописан к пути. Первая
    /// порция идет БЕЗ параметра разбивки — так чужие программы и описывают свой первый вызов,
    /// а лишний <c>?page=1</c> некоторые из них считают ошибкой.
    /// </summary>
    private static IntegrationRequest Page(
        IntegrationRequest request,
        VariableContext    context,
        OutgoingPayload?   payload,
        string?            cursor,
        int                number,
        int                offset,
        out IReadOnlyList<string> unknown)
    {
        var path = RequestVariables.Apply(request.Path, context, out var pathUnknown);

        string                body;
        IReadOnlyList<string> bodyUnknown;

        if (payload is not null)
        {
            // Тело из коллекции подстановок НЕ проходит: это данные, а не заготовка. Значение
            // поля, в котором человек написал «{{сегодня}}», уехало бы сегодняшней датой вместо
            // самого текста, и заметили бы это на той стороне, а не здесь.
            body        = payload.Body;
            bodyUnknown = [];
        }
        else
        {
            body = RequestVariables.Apply(request.Body, context, out bodyUnknown);
        }

        unknown = [.. pathUnknown.Concat(bodyUnknown).Distinct()];

        var mark = request.Paging switch
        {
            RequestPaging.Page   when number > 1 => Parameter(request.PagingField, number.ToString()),
            RequestPaging.Offset when offset > 0 => Parameter(request.PagingField, offset.ToString()),
            RequestPaging.Cursor when !string.IsNullOrEmpty(cursor) =>
                Parameter(request.PagingField, cursor!),
            _ => "",
        };

        if (mark.Length > 0) path += (path.Contains('?') ? "&" : "?") + mark;

        return request with { Path = path, Body = body };
    }

    private static string Parameter(string name, string value) =>
        Uri.EscapeDataString(name.Trim()) + "=" + Uri.EscapeDataString(value);

    /// <summary>
    /// Забрать записи из ответа и метку следующей порции. Записи складываются сырым текстом:
    /// разбирать их значения будет источник импорта, и делать это дважды незачем.
    /// </summary>
    private static bool TryTake(
        string             body,
        IntegrationRequest request,
        List<string>       items,
        int                limit,
        out string?        cursor,
        out int            taken,
        out string         error)
    {
        cursor = null;
        taken  = 0;
        error  = "";

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling     = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException failure)
        {
            // Тело ответа в сообщение не попадает: в нем может оказаться что угодно, вплоть до
            // секрета, а сообщение уходит в журнал.
            error = "ответ не является JSON: " + failure.Message;
            return false;
        }

        using (document)
        {
            var node = document.RootElement;

            foreach (var step in (request.ItemsPath ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(step, out var next))
                {
                    error = $"в ответе нет пути «{request.ItemsPath}» — не найдена часть «{step}»";
                    return false;
                }

                node = next;
            }

            switch (node.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var item in node.EnumerateArray())
                    {
                        if (items.Count >= limit) break;
                        items.Add(item.GetRawText());
                        taken++;
                    }
                    break;

                // Одиночный объект — обычный ответ справочного вызова: это одна запись.
                case JsonValueKind.Object:
                    if (items.Count < limit) { items.Add(node.GetRawText()); taken++; }
                    break;

                default:
                    error = "по указанному пути в ответе лежит не список записей, а одиночное значение";
                    return false;
            }

            if (request.Paging == RequestPaging.Cursor)
                cursor = Cursor(document.RootElement, request.PagingField);
        }

        return true;
    }

    /// <summary>
    /// Метка следующей порции из корня ответа: путь по точкам, как и у массива записей. Метки
    /// нет или она пустая — список кончился.
    /// </summary>
    private static string? Cursor(JsonElement root, string field)
    {
        var node = root;

        foreach (var step in (field ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(step, out var next))
                return null;

            node = next;
        }

        var value = node.ValueKind switch
        {
            JsonValueKind.String => node.GetString(),
            JsonValueKind.Number => node.GetRawText(),
            _                    => null,
        };

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
