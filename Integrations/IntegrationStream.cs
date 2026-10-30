namespace SmartBase.Integrations;

/// <summary>Как сторонняя программа отдает события по открытому соединению.</summary>
public enum StreamProtocol
{
    /// <summary>
    /// Простой WebSocket: каждое текстовое сообщение — одно событие. Подписываться не на что:
    /// события идут сразу после соединения.
    /// </summary>
    Plain = 0,

    /// <summary>
    /// STOMP поверх WebSocket: после соединения программа представляется и подписывается на темы
    /// (<see cref="IntegrationStream.Topics"/>), событие приходит телом кадра <c>MESSAGE</c>.
    /// </summary>
    Stomp,
}

/// <summary>
/// Поток событий — «что нам присылают»: соединение, по которому сторонняя программа сама
/// сообщает о случившемся, не дожидаясь вопроса.
///
/// Запрос и поток — две стороны одного обмена. Запрос дергаем мы, и ответ приходит на вопрос;
/// поток открываем тоже мы, но дальше говорит та сторона — когда ей есть что сказать. Поэтому у
/// потока нет ни метода, ни тела, ни расписания: есть адрес, способ подписки и правило, как из
/// события получаются строки.
///
/// <b>О сторонней программе карточка не знает ничего.</b> Путь, темы, путь к записям и имя
/// колонки-ключа вводит человек — из документации той программы, как и путь запроса. Программа
/// знает только два общих способа подписки и общее правило разбора JSON.
///
/// Адрес и секрет берутся из соединения, путь относительный — по тем же причинам, что у запроса:
/// адрес задает соединение, и позволить здесь полный адрес значило бы показывать одно
/// соединение, а слушать другое.
///
/// Модель без ввода-вывода: слушает поток служба, а здесь только форма и ее проверка.
/// </summary>
/// <param name="Id">Ключ строки в <c>app.db</c>; 0 — поток еще не сохранен.</param>
/// <param name="Name">Имя для человека: «События учетной системы».</param>
/// <param name="ConnectionId">Соединение, через которое открывается поток.</param>
/// <param name="Path">Путь относительно адреса соединения: <c>/events</c>.</param>
/// <param name="Protocol">Способ подписки.</param>
/// <param name="Topics">Темы подписки (только <see cref="StreamProtocol.Stomp"/>).</param>
/// <param name="ItemsPath">Путь к записям внутри события, точками: <c>data</c>. Пусто — записью
/// считается само событие (или каждый элемент, если событие — список).</param>
/// <param name="KeyField">Колонка, по которой отсекаются повторы события; пусто — повторы не
/// отсекаются.</param>
public sealed record IntegrationStream(
    int                   Id,
    string                Name,
    int                   ConnectionId,
    string                Path,
    StreamProtocol        Protocol,
    IReadOnlyList<string> Topics,
    string                ItemsPath,
    string                KeyField)
{
    /// <summary>
    /// Сколько тем помещается в одну подписку. Предохранитель: три десятка тем в одной карточке —
    /// это уже не подписка, а попытка слушать все подряд, и разобрать потом, какое событие зачем
    /// пришло, не получится.
    /// </summary>
    public const int MaxTopics = 20;

    /// <summary>Пустая карточка для экрана «Добавить».</summary>
    public static IntegrationStream New() =>
        new(0, "", 0, "/", StreamProtocol.Plain, [], "", "");

    /// <summary>Темы без пустых строк и повторов — то, на что на самом деле подписываемся.</summary>
    public IReadOnlyList<string> Subscriptions =>
        [.. Topics.Select(t => (t ?? "").Trim())
                  .Where(t => t.Length > 0)
                  .Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Что не так с карточкой; пустой список — можно сохранять. Проверяется форма, а не
    /// доступность: ответит ли та сторона — вопрос отдельный.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
            problems.Add("Название не заполнено");

        if (ConnectionId <= 0)
            problems.Add("Не выбрано соединение");

        // Правила пути те же, что у запроса, и спрашиваются у него же: второе прочтение
        // «относительного пути» однажды разошлось бы с первым.
        problems.AddRange(
            (IntegrationRequest.New() with { Name = "поток", Path = Path }).ShapeProblems());

        if (Protocol == StreamProtocol.Stomp)
        {
            if (Subscriptions.Count == 0)
                problems.Add("Не названа ни одна тема подписки");
            else if (Subscriptions.Count > MaxTopics)
                problems.Add($"Тем больше {MaxTopics}: разделите подписку на несколько потоков");

            // Тема уходит заголовком кадра, а заголовок — одна строка: перевод строки внутри
            // превратил бы хвост темы в следующий заголовок.
            if (Subscriptions.Any(t => t.Contains('\n') || t.Contains('\r') || t.Contains('\0')))
                problems.Add("Тема подписки — одна строка без переводов строки");
        }

        return problems;
    }

    /// <summary>Адрес потока: адрес соединения плюс путь; <c>null</c> — карточка негодная.</summary>
    public Uri? BuildAddress(IntegrationConnection connection) =>
        (IntegrationRequest.New() with { Name = "поток", Path = Path }).BuildAddress(connection);

    /// <summary>Одной строкой для списка: путь и способ подписки.</summary>
    public string Describe() => Protocol switch
    {
        StreamProtocol.Stomp => $"{Path} · STOMP: {string.Join(", ", Subscriptions)}",
        _                    => $"{Path} · WebSocket",
    };
}
