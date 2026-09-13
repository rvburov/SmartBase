namespace SmartBase.Integrations;

/// <summary>Способ авторизации у чужой программы. Строкой хранится ключ, а не подпись.</summary>
public enum ConnectionAuth
{
    /// <summary>Без авторизации — открытый адрес во внутренней сети.</summary>
    None = 0,

    /// <summary>Ключ в своем заголовке: <c>X-Api-Key: секрет</c>.</summary>
    Header,

    /// <summary>Токен: <c>Authorization: Bearer секрет</c>.</summary>
    Bearer,

    /// <summary>Имя и пароль: <c>Authorization: Basic base64(имя:секрет)</c>.</summary>
    Basic,

    /// <summary>Ключ в адресе: <c>?api_key=секрет</c>.</summary>
    Query,
}

/// <summary>
/// Соединение — «куда и под кем ходим». Настраивается один раз и переиспользуется всеми
/// запросами: адрес и секрет не должны повторяться в каждом из них, иначе смена токена
/// превращается в обход десятка карточек.
///
/// Секрет сюда НЕ входит. В памяти он живет отдельно и недолго, а в <c>app.db</c> лежит
/// защищенным (<c>SecretProtector</c>: DPAPI на Windows, Связка ключей на macOS) — см.
/// <c>Database/IntegrationsRegistry.cs</c>. Причина простая: соединения экспортируются и
/// показываются на экранах, и секрету там делать нечего.
///
/// Модель без ввода-вывода: проверка адреса по белому списку — забота
/// <see cref="EgressGate"/>, чтение и запись — реестра.
/// </summary>
/// <param name="Id">Ключ строки в <c>app.db</c>; 0 — соединение еще не сохранено.</param>
/// <param name="Name">Имя для человека: «Учетная система», «Склад».</param>
/// <param name="Address">Базовый адрес, к которому пути запросов дописываются справа.</param>
/// <param name="Auth">Способ авторизации.</param>
/// <param name="AuthName">Имя заголовка или параметра, а у <see cref="ConnectionAuth.Basic"/> —
/// имя пользователя. Для <see cref="ConnectionAuth.None"/> и <see cref="ConnectionAuth.Bearer"/>
/// не нужно.</param>
/// <param name="Headers">Постоянные заголовки соединения, кроме заголовка авторизации.</param>
/// <param name="TimeoutSeconds">Сколько ждать ответа.</param>
/// <param name="Retries">Сколько раз повторить при обрыве связи; повтор идет с бэкоффом.</param>
public sealed record IntegrationConnection(
    int                                     Id,
    string                                  Name,
    string                                  Address,
    ConnectionAuth                          Auth,
    string                                  AuthName,
    IReadOnlyList<IntegrationHeader>        Headers,
    int                                     TimeoutSeconds,
    int                                     Retries)
{
    /// <summary>Сколько ждать ответа, если не задано иначе.</summary>
    public const int DefaultTimeoutSeconds = 30;

    /// <summary>Границы таймаута: меньше секунды бессмысленно, больше пяти минут — висящий сценарий.</summary>
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 300;

    /// <summary>Повторов по умолчанию и потолок: без потолка обрыв связи дает бесконечный цикл.</summary>
    public const int DefaultRetries = 3;
    public const int MaxRetries     = 10;

    /// <summary>Пустая карточка для экрана «Добавить».</summary>
    public static IntegrationConnection New() =>
        new(0, "", "", ConnectionAuth.None, "", [], DefaultTimeoutSeconds, DefaultRetries);

    /// <summary>
    /// Копия карточки под новое соединение: без ключа и с новым именем. Сохранение такой
    /// карточки заводит НОВУЮ запись, а не переписывает ту, с которой копировали.
    ///
    /// <b>Секрет за копией не идет.</b> Технически его и нечем взять — он лежит в хранилище
    /// операционной системы под ключом карточки, а ключа у копии еще нет. Но причина не
    /// техническая: дублируют соединение, чтобы завести ВТОРОЙ доступ — к соседнему контуру,
    /// под другой учетной записью, — и молча унести туда тот же секрет значило бы размножить
    /// его по карточкам, не спросив.
    ///
    /// Адрес, способ авторизации, заголовки, таймаут и повторы копируются: ради них дублируют.
    /// </summary>
    public IntegrationConnection Copy() => this with { Id = 0, Name = Name + " — копия" };

    /// <summary>Нужен ли этому способу авторизации секрет.</summary>
    public bool NeedsSecret => Auth != ConnectionAuth.None;

    /// <summary>Нужно ли этому способу имя заголовка, параметра или пользователя.</summary>
    public bool NeedsAuthName => Auth is ConnectionAuth.Header or ConnectionAuth.Query or ConnectionAuth.Basic;

    /// <summary>
    /// Что не так с карточкой; пустой список — можно сохранять. Проверяется форма, а не
    /// доступность: разрешен ли адрес политикой, отвечает ли сервер — вопросы отдельные и
    /// отвечают на них <see cref="EgressGate"/> и кнопка «Проверить связь».
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
            problems.Add("Название не заполнено");

        if (string.IsNullOrWhiteSpace(Address))
            problems.Add("Адрес не заполнен");
        else if (!Uri.TryCreate(Address.Trim(), UriKind.Absolute, out var uri))
            problems.Add("Адрес не разобрался: ожидался вид https://сервер/путь");
        else if (uri.Scheme is not ("http" or "https"))
            problems.Add($"Схема «{uri.Scheme}» не поддерживается: разрешены только http и https");

        if (NeedsAuthName && string.IsNullOrWhiteSpace(AuthName))
            problems.Add(Auth == ConnectionAuth.Basic
                ? "Не указано имя пользователя"
                : "Не указано имя заголовка или параметра");

        if (TimeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds)
            problems.Add($"Таймаут вне границ: от {MinTimeoutSeconds} до {MaxTimeoutSeconds} секунд");

        if (Retries is < 0 or > MaxRetries)
            problems.Add($"Повторов вне границ: от 0 до {MaxRetries}");

        foreach (var header in Headers)
            if (header.Trouble() is { Length: > 0 } trouble)
                problems.Add(char.ToUpperInvariant(trouble[0]) + trouble[1..]);

        // Имя заголовка авторизации — такой же заголовок, и латиница ему нужна не меньше. Про
        // ПУСТОЕ имя выше уже сказано своими словами, и повторять это вторым сообщением незачем.
        if (Auth == ConnectionAuth.Header && !string.IsNullOrWhiteSpace(AuthName)
            && new IntegrationHeader(AuthName, "").Trouble() is { Length: > 0 } auth)
            problems.Add(char.ToUpperInvariant(auth[0]) + auth[1..]);

        return problems;
    }

    /// <summary>Адрес как <see cref="Uri"/>; <c>null</c> — не разобрался.</summary>
    public Uri? BaseAddress =>
        Uri.TryCreate((Address ?? "").Trim(), UriKind.Absolute, out var uri) ? uri : null;
}

/// <summary>Постоянный заголовок соединения или запроса.</summary>
/// <param name="Name">Имя заголовка.</param>
/// <param name="Value">Значение; секретов здесь быть не должно — для них есть авторизация.</param>
public sealed record IntegrationHeader(string Name, string Value)
{
    /// <summary>
    /// Что не так с заголовком; пустая строка — годится.
    ///
    /// Проверяется в первую очередь ЛАТИНИЦА. Заголовки HTTP по стандарту только из ASCII, и
    /// <c>HttpClient</c> отвечает на кириллицу исключением уже в момент отправки — то есть
    /// ночью, из фонового сценария, и в виде, который человеку ничего не объясняет. Поймать
    /// это при сохранении карточки стоит одной проверки, а не поймать — стоит суток
    /// разбирательств.
    /// </summary>
    public string Trouble()
    {
        var name = (Name ?? "").Trim();

        if (name.Length == 0) return "у заголовка не заполнено имя";

        if (!IsAscii(name))
            return $"имя заголовка «{name}» не из латиницы: заголовки HTTP бывают только "
                 + "латинскими, и такой запрос не уйдет вовсе";

        // Пробел и двоеточие в имени рвут сам протокол, а не только этот запрос.
        if (name.Any(c => c is ' ' or ':' or '	'))
            return $"в имени заголовка «{name}» есть пробел или двоеточие";

        if (!IsAscii(Value ?? ""))
            return $"значение заголовка «{name}» не из латиницы: заголовки HTTP бывают только "
                 + "латинскими";

        return "";
    }

    /// <summary>Только видимые знаки ASCII — то, что вообще бывает в заголовке.</summary>
    private static bool IsAscii(string text) => text.All(c => c is >= ' ' and <= '~');
}
