using System.Text;

namespace SmartBase.Integrations;

/// <summary>
/// Подготовленный вызов: что именно уйдет наружу. Здесь нет ни одного сетевого типа — только
/// решения, и потому все они проверяются тестами без сети.
///
/// Разделение намеренное. Сетевой слой должен быть тонким и скучным: взял готовый план,
/// соединился с проверенным адресом, вернул ответ. Все, что можно перепутать — адрес, заголовок
/// авторизации, тип содержимого, можно ли повторять, — решается тут.
/// </summary>
/// <param name="Address">Адрес, по которому пойдет запрос. Если ключ авторизации живет в
/// параметрах, он уже здесь — и наружу этот вид отдавать нельзя.</param>
/// <param name="SafeAddress">Тот же адрес с вырезанным секретом: для журнала, экрана и
/// диагностики.</param>
/// <param name="Method">Метод.</param>
/// <param name="Headers">Заголовки, включая заголовок авторизации.</param>
/// <param name="Body">Тело.</param>
/// <param name="ContentType">Тип содержимого тела.</param>
/// <param name="TimeoutSeconds">Сколько ждать ответа.</param>
/// <param name="Retries">Сколько раз повторять при обрыве связи.</param>
/// <param name="Idempotent">Можно ли повторять запрос, не боясь сделать работу дважды.</param>
/// <param name="SecretHeader">Имя заголовка, значение которого — секрет; пусто, если секрета в
/// заголовках нет.</param>
public sealed record EgressPlan(
    Uri                              Address,
    string                           SafeAddress,
    RequestMethod                    Method,
    IReadOnlyList<IntegrationHeader> Headers,
    string                           Body,
    string                           ContentType,
    int                              TimeoutSeconds,
    int                              Retries,
    bool                             Idempotent,
    string                           SecretHeader)
{
    /// <summary>Чем заменяется секрет везде, где вызов показывается человеку.</summary>
    public const string Mask = "***";

    /// <summary>
    /// Заголовки для показа и журнала: значение заголовка авторизации заменено. Журнал, в
    /// котором виден токен, — это место утечки, а не журнал.
    /// </summary>
    public IReadOnlyList<IntegrationHeader> SafeHeaders =>
        [.. Headers.Select(h => h.Name.Equals(SecretHeader, StringComparison.OrdinalIgnoreCase)
            ? h with { Value = Mask }
            : h)];

    /// <summary>
    /// Собрать вызов из соединения, запроса и секрета. <c>null</c> — карточки негодные, причина
    /// в <paramref name="error"/>.
    ///
    /// Секрет приходит сюда параметром и нигде не сохраняется: он живет ровно столько, сколько
    /// длится вызов.
    /// </summary>
    public static EgressPlan? Build(
        IntegrationConnection connection,
        IntegrationRequest    request,
        string                secret,
        out string            error)
    {
        error = "";

        // У запроса спрашивается форма без привязки к соединению: оно передано сюда явно, и
        // требовать вдобавок сохраненный ключ значило бы запретить проверку связи по карточке,
        // которую еще не сохранили.
        var problems = connection.Problems().Concat(request.ShapeProblems()).ToList();
        if (problems.Count > 0)
        {
            error = string.Join("; ", problems);
            return null;
        }

        var address = request.BuildAddress(connection);
        if (address is null)
        {
            error = "адрес не собрался";
            return null;
        }

        // Заголовки соединения идут первыми, заголовки запроса могут их перекрыть: общее
        // правило снизу, частный случай сверху.
        var headers = new List<IntegrationHeader>();
        foreach (var header in connection.Headers.Concat(request.Headers))
        {
            headers.RemoveAll(h => h.Name.Equals(header.Name, StringComparison.OrdinalIgnoreCase));
            headers.Add(header);
        }

        // Именно AbsoluteUri, а не ToString(): тот показывает адрес «покрасивее», разэкранируя
        // часть знаков, и склеенный из него новый адрес мог бы значить уже не то же самое.
        var safeAddress  = address.AbsoluteUri;
        var secretHeader = "";

        switch (connection.Auth)
        {
            case ConnectionAuth.Bearer:
                headers.Add(new IntegrationHeader("Authorization", "Bearer " + secret));
                secretHeader = "Authorization";
                break;

            case ConnectionAuth.Basic:
                var pair = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(connection.AuthName + ":" + secret));
                headers.Add(new IntegrationHeader("Authorization", "Basic " + pair));
                secretHeader = "Authorization";
                break;

            case ConnectionAuth.Header:
                headers.Add(new IntegrationHeader(connection.AuthName, secret));
                secretHeader = connection.AuthName;
                break;

            case ConnectionAuth.Query:
                var name = Uri.EscapeDataString(connection.AuthName);
                var mark = address.Query.Length > 0 ? "&" : "?";
                address     = new Uri(safeAddress + mark + name + "=" + Uri.EscapeDataString(secret));
                safeAddress = safeAddress + mark + name + "=" + Mask;
                break;
        }

        // Последняя проверка перед сборкой: заголовок не из латиницы отправить НЕЛЬЗЯ —
        // HttpClient отвечает на такой исключением уже в момент записи в сокет. Форму карточек
        // проверяет их собственный разбор, но сюда приходит еще и СЕКРЕТ, которого в карточке
        // нет: набранный кириллицей токен иначе ронял бы обмен ночью и без объяснений.
        //
        // Имя заголовка в сообщении есть, значение — никогда: оно и есть секрет.
        foreach (var header in headers)
        {
            if (Ascii(header.Name) && Ascii(header.Value)) continue;

            error = header.Name.Equals(secretHeader, StringComparison.OrdinalIgnoreCase)
                        && !Ascii(header.Value)
                ? $"секрет соединения набран не латиницей, а заголовок «{header.Name}» бывает "
                + "только латинским — такой запрос не уйдет вовсе"
                : $"заголовок «{header.Name}» не из латиницы: заголовки HTTP бывают только "
                + "латинскими";

            return null;
        }

        var body = request.Method == RequestMethod.Get ? "" : request.Body ?? "";

        return new EgressPlan(
            address,
            safeAddress,
            request.Method,
            headers,
            body,
            ContentTypeFor(headers, body),
            connection.TimeoutSeconds,
            connection.Retries,
            Idempotency(request.Method),
            secretHeader);
    }

    /// <summary>Только видимые знаки ASCII — то, что вообще бывает в заголовке HTTP.</summary>
    private static bool Ascii(string? text) => (text ?? "").All(c => c is >= ' ' and <= '~');

    /// <summary>
    /// План для кнопки «Проверить связь»: GET по адресу соединения без пути. Отдельный метод
    /// затем, что проверяют обычно недописанную карточку — сохраненного запроса еще нет.
    /// </summary>
    public static EgressPlan? Probe(IntegrationConnection connection, string secret, out string error) =>
        Build(connection,
              IntegrationRequest.New() with { Name = "Проверка связи", Path = "/" },
              secret,
              out error);

    /// <summary>
    /// Тип содержимого: заданный человеком главнее догадки. Догадка повторяет поведение cURL —
    /// строки для обмена чаще всего приходят именно оттуда, и запрос должен вести себя так же,
    /// как вел себя в документации сторонней программы.
    /// </summary>
    private static string ContentTypeFor(List<IntegrationHeader> headers, string body)
    {
        var declared = headers.FirstOrDefault(
            h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));

        if (declared is not null) return declared.Value;

        var head = body.TrimStart();
        return head.StartsWith('{') || head.StartsWith('[')
            ? "application/json"
            : "application/x-www-form-urlencoded";
    }

    /// <summary>
    /// Можно ли повторять. POST и PATCH не повторяются никогда: обрыв связи не значит, что
    /// запрос не дошел, — он мог дойти и выполниться, а ответ потеряться. Повтор в этом случае
    /// зарегистрирует документ дважды, и виноватой будет выглядеть программа.
    /// </summary>
    private static bool Idempotency(RequestMethod method) =>
        method is RequestMethod.Get or RequestMethod.Put or RequestMethod.Delete;
}

/// <summary>
/// Ответ HTTP-транспорта либо описание ошибки подключения.
/// </summary>
/// <param name="Sent">Запрос ушел и ответ получен. <c>false</c> — не ушел вовсе.</param>
/// <param name="Explanation">Объяснение человеческим языком: и отказа, и сетевого сбоя.</param>
/// <param name="StatusCode">Код ответа; 0 — ответа не было.</param>
/// <param name="Body">Тело ответа, обрезанное по потолку.</param>
/// <param name="Bytes">Сколько байт пришло.</param>
/// <param name="Truncated">Ответ оказался длиннее потолка и обрезан.</param>
/// <param name="Elapsed">Сколько заняло.</param>
/// <param name="Headers">Заголовки ответа.</param>
/// <param name="SafeAddress">Куда ходили — без секрета.</param>
/// <param name="Attempts">Сколько попыток понадобилось.</param>
/// <param name="Content">Готовый двоичный ответ загрузки. У текстового запроса, ошибки либо
/// обрезанного ответа пусто: неполный файл нельзя передавать на обработку.</param>
public sealed record EgressResponse(
    bool                             Sent,
    string                           Explanation,
    int                              StatusCode,
    string                           Body,
    long                             Bytes,
    bool                             Truncated,
    TimeSpan                         Elapsed,
    IReadOnlyList<IntegrationHeader> Headers,
    string                           SafeAddress,
    int                              Attempts,
    byte[]?                          Content = null)
{
    /// <summary>Ответ получен и он успешный.</summary>
    public bool Ok => Sent && StatusCode is >= 200 and < 300;

    /// <summary>Сторонняя программа ответила перенаправлением, а за ним программа не идет.</summary>
    public bool Redirected => Sent && StatusCode is >= 300 and < 400;

    /// <summary>Запрос ушел, но связи не случилось: обрыв, таймаут, отказ сертификата.</summary>
    public static EgressResponse Failed(string explanation, string address = "", int attempts = 0,
                                         TimeSpan elapsed = default) =>
        new(false, explanation, 0, "", 0, false, elapsed, [], address, attempts);
}
