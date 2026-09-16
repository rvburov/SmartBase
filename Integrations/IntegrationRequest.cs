namespace SmartBase.Integrations;

/// <summary>Метод запроса. Пятерки хватает на обмен данными; экзотика вроде HEAD не нужна.</summary>
public enum RequestMethod
{
    Get = 0,
    Post,
    Put,
    Patch,
    Delete,
}

/// <summary>
/// Как сторонняя программа отдает длинные списки. Без этого выгрузка молча обрывается на первой
/// сотне записей — самая частая и самая незаметная беда обмена.
/// </summary>
public enum RequestPaging
{
    /// <summary>Все приходит одним ответом.</summary>
    None = 0,

    /// <summary>Номер страницы: <c>?page=1</c>, <c>?page=2</c>…</summary>
    Page,

    /// <summary>Сдвиг от начала: <c>?offset=0</c>, <c>?offset=100</c>…</summary>
    Offset,

    /// <summary>Метка следующей порции из прошлого ответа: <c>?cursor=…</c>.</summary>
    Cursor,
}

/// <summary>
/// Запрос — «что дергаем»: метод, путь, заголовки, тело, разбивка на страницы и путь к массиву
/// в ответе. Живет поверх <see cref="IntegrationConnection"/>: адрес и секрет берутся оттуда,
/// иначе смена токена превращалась бы в обход десятка карточек.
///
/// Путь ОТНОСИТЕЛЬНЫЙ, и это правило безопасности, а не удобства. Позволь здесь полный адрес —
/// и карточка показывала бы безобидное соединение «Учетная система», а запрос уходил бы куда
/// угодно. Хост берется только из соединения, а его проверяет <see cref="EgressGate"/>.
///
/// Модель без ввода-вывода: отправка, подстановка переменных и разбор ответа — отдельные
/// слои. Здесь только форма запроса и ее проверка.
/// </summary>
/// <param name="Id">Ключ строки в <c>app.db</c>; 0 — запрос еще не сохранен.</param>
/// <param name="Name">Имя для человека: «Заказы за сутки».</param>
/// <param name="ConnectionId">Соединение, через которое уходит запрос.</param>
/// <param name="Method">Метод.</param>
/// <param name="Path">Путь с параметрами относительно адреса соединения:
/// <c>/orders?since={{последний_запуск}}</c>.</param>
/// <param name="Headers">Заголовки запроса; заголовки соединения добавляются к ним при отправке.</param>
/// <param name="Body">Тело; для <see cref="RequestMethod.Get"/> пустое.</param>
/// <param name="Paging">Как сторонняя программа отдает длинные списки.</param>
/// <param name="PagingField">Имя параметра страницы, сдвига или поля с меткой следующей порции.</param>
/// <param name="ItemsPath">Путь к массиву записей в ответе: <c>data.items</c>. Пусто — ответ сам
/// является массивом либо одной записью.</param>
public sealed record IntegrationRequest(
    int                              Id,
    string                           Name,
    int                              ConnectionId,
    RequestMethod                    Method,
    string                           Path,
    IReadOnlyList<IntegrationHeader> Headers,
    string                           Body,
    RequestPaging                    Paging,
    string                           PagingField,
    string                           ItemsPath)
{
    /// <summary>Пустая карточка для экрана «Добавить».</summary>
    public static IntegrationRequest New() =>
        new(0, "", 0, RequestMethod.Get, "/", [], "", RequestPaging.None, "", "");

    /// <summary>Нужно ли этому способу разбивки имя параметра или поля.</summary>
    public bool NeedsPagingField => Paging != RequestPaging.None;

    /// <summary>
    /// Уносит ли запрос данные наружу — то есть нужно ли ему подтвержденное направление
    /// (<see cref="OutgoingDirection"/>).
    ///
    /// Граница проходит по методу: <c>GET</c> спрашивает, остальные меняют что-то на той стороне
    /// и несут тело. Это та же граница, по которой ее проводит и весь остальной мир, и требовать
    /// мастер-пароль на каждое чтение значило бы запереть обмен, который и так ограничен белым
    /// списком и квотами.
    ///
    /// <b>Чего эта граница не ловит.</b> Данные можно вписать руками и в путь <c>GET</c>:
    /// <c>/ping?что=секрет</c>. От этого защищает не подтверждение, а белый список (уйдет только
    /// на разрешенный адрес) и журнал безопасности (уйдет со следом). Делать вид, что метод
    /// закрывает и этот случай, нельзя.
    /// </summary>
    public bool Outward => Method != RequestMethod.Get;

    /// <summary>
    /// Отпечаток запроса — то, что подтверждают. Считается по всему, что влияет на уходящее:
    /// метод, путь, заголовки и тело. Имя карточки и настройки разбора ответа сюда НЕ входят:
    /// переименование запроса не меняет того, что уходит, а сбрасывать подтверждение из-за него
    /// значило бы приучить человека подтверждать не глядя.
    ///
    /// Значения заголовков входят в отпечаток, но наружу отсюда не попадают: это односторонний
    /// хэш, а не запись.
    /// </summary>
    /// <param name="outgoing">Отпечаток состава исходящей привязки
    /// (<c>OutgoingBinding.Mark</c>); пусто — запрос ничего из коллекции не уносит. Приходит
    /// параметром, потому что привязка лежит отдельно от карточки, а подтверждают их вместе:
    /// согласовано «этот запрос с ЭТИМИ полями», и дописанное поле обязано ронять подтверждение
    /// так же, как правка тела.</param>
    public string Fingerprint(string outgoing = "")
    {
        var parts = new List<string>
        {
            ((int)Method).ToString(System.Globalization.CultureInfo.InvariantCulture),
            (Path ?? "").Trim(),
            (Body ?? "").Trim(),
        };

        // Состав дописывается, только когда он есть. Дописывай пустую строку всегда — и
        // отпечатки всех уже подтвержденных запросов сменились бы в день обновления программы,
        // отключив обмен там, где ничего не меняли.
        if (outgoing.Length > 0) parts.Add(outgoing);

        // Заголовки в отпечаток идут по порядку карточки: перестановка строк меняет то, что
        // уходит в сеть, ровно в тех случаях, когда сторонняя программа читает первый подходящий.
        foreach (var header in Headers)
            parts.Add((header.Name ?? "") + "=" + (header.Value ?? ""));

        // Разделитель — знак 0x1F, записанный кодом, а не самим знаком: невидимый знак в
        // исходнике не переживет ни правки, ни смены кодировки, а вместе с ним разойдутся
        // все отпечатки — и подтверждения слетят у всех разом.
        var text = string.Join((char)0x1F, parts);

        return Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>
    /// Что не так с карточкой; пустой список — можно сохранять. Проверяется форма, а не
    /// доступность: ответит ли сервер и разрешит ли политика адрес — вопросы отдельные.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = ShapeProblems().ToList();

        if (ConnectionId <= 0)
            problems.Add("Не выбрано соединение");

        return problems;
    }

    /// <summary>
    /// Что не так с самим запросом, без привязки к соединению. Нужно там, где соединение и так
    /// известно: при проверке связи карточку еще не сохранили, ключа у нее нет, а путь и метод
    /// проверить надо.
    /// </summary>
    public IReadOnlyList<string> ShapeProblems()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
            problems.Add("Название не заполнено");

        var path = (Path ?? "").Trim();

        if (path.Length == 0)
            problems.Add("Путь не заполнен: для корня адреса достаточно «/»");
        else if (HasScheme(path))
            problems.Add("Путь должен быть относительным: адрес задается соединением");
        else if (path.StartsWith("//", StringComparison.Ordinal))
            problems.Add("Путь должен быть относительным: «//сервер» уводит запрос на чужой хост");
        else if (path.Contains("..", StringComparison.Ordinal))
            problems.Add("Путь не должен содержать «..»: так запрос уходит выше адреса соединения");
        else if (!path.StartsWith('/'))
            problems.Add("Путь должен начинаться с «/»");

        if (Method == RequestMethod.Get && !string.IsNullOrWhiteSpace(Body))
            problems.Add("У метода GET тела не бывает: перенесите данные в путь или смените метод");

        if (NeedsPagingField && string.IsNullOrWhiteSpace(PagingField))
            problems.Add(Paging == RequestPaging.Cursor
                ? "Не указано поле ответа с меткой следующей порции"
                : "Не указано имя параметра разбивки на страницы");

        foreach (var header in Headers)
            if (header.Trouble() is { Length: > 0 } trouble)
                problems.Add(char.ToUpperInvariant(trouble[0]) + trouble[1..]);

        return problems;
    }

    /// <summary>
    /// Начинается ли путь со схемы — «https:», «file:», «javascript:». Именно разбор начала
    /// строки, а не <c>Uri.TryCreate(path, UriKind.Absolute, …)</c>: на macOS и Linux тот
    /// считает абсолютным любой путь с «/» в начале (читает «/orders» как «file:///orders»),
    /// и проверка отвергала бы каждый нормальный путь — а заодно обнуляла бы
    /// <see cref="BuildAddress"/>, то есть обмен там не работал бы вовсе.
    ///
    /// Схема по RFC 3986: буква, дальше буквы, цифры, «+», «-», «.» и двоеточие. Двоеточие
    /// внутри пути («/orders?t=12:30») схемой не считается — до него должна идти буква.
    /// </summary>
    private static bool HasScheme(string path)
    {
        var colon = path.IndexOf(':');

        if (colon <= 0 || !char.IsAsciiLetter(path[0])) return false;

        for (var i = 1; i < colon; i++)
            if (!char.IsAsciiLetterOrDigit(path[i]) && path[i] is not ('+' or '-' or '.'))
                return false;

        return true;
    }

    /// <summary>
    /// Полный адрес запроса: адрес соединения плюс путь. Склейка строками, а не
    /// <see cref="Uri"/>: тот по правилам считает «/orders» заменой всего пути, и
    /// «https://сервер/обмен» + «/orders» дало бы «https://сервер/orders» — не то, чего ждет
    /// человек, настроивший соединение на подкаталог.
    ///
    /// <c>null</c> — карточка негодная; причину скажет <see cref="ShapeProblems"/>.
    /// </summary>
    public Uri? BuildAddress(IntegrationConnection connection)
    {
        if (ShapeProblems().Count > 0 || connection.BaseAddress is null) return null;

        var head = connection.Address.Trim().TrimEnd('/');
        var tail = (Path ?? "").Trim();

        return Uri.TryCreate(tail == "/" ? head : head + tail, UriKind.Absolute, out var address)
            ? address
            : null;
    }
}
