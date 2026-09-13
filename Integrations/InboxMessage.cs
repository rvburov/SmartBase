using System.Text.Json;

namespace SmartBase.Integrations;

/// <summary>
/// Что делать со строками сообщения.
/// </summary>
public enum InboxMode
{
    /// <summary>Добавить как новые записи. Повтор файла обязан отсекаться по идентификатору.</summary>
    Add = 0,

    /// <summary>Найти запись по ключу и обновить; ненайденную завести.</summary>
    Update,

    /// <summary>Найти запись по ключу и удалить. Только при праве робота «Удаление записи»
    /// (docs/ROADMAP-API.md, шаг 11); повтор безвреден — удаленное второй раз не удаляется.</summary>
    Delete,

    /// <summary>Создать коллекцию по схеме и добрать недостающие поля: чужая программа готовит
    /// себе место одним сообщением при первом запуске. Существующие поля не трогаются.</summary>
    EnsureCollection,

    /// <summary>
    /// Режим, которого этот выпуск программы не знает. Раньше незнакомое читалось как
    /// «добавить» — тогда режимов было два и оба клали данные. С появлением «удалить» это
    /// правило стало опасным: прочесть чужое намерение НЕ ТЕМ действием хуже, чем отказать
    /// словами, — отправитель из квитанции сразу узнает, что приемник старее его формата.
    /// </summary>
    Unknown,
}

/// <summary>
/// Поле из сообщения «ensure-collection»: имя — как оно будет видно человеку, тип — из
/// короткого словаря <see cref="KnownTypes"/>.
/// </summary>
/// <param name="Name">Имя свойства.</param>
/// <param name="Type">Тип свойства — внутренним кодом («text», «number», …).</param>
public sealed record InboxField(string Name, string Type)
{
    /// <summary>
    /// Что разрешено заводить извне. Словарь нарочно короткий: списки и статусы требуют
    /// состава пунктов, связи — второй коллекции, и заводить их пустыми значило бы оставить
    /// человеку ловушку вместо поля. Остальные типы заводит человек в окне коллекции.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownTypes =
        ["text", "number", "date", "checkbox", "url"];
}

/// <summary>
/// Входящее сообщение — файл, который чужая программа кладет в папку обмена.
///
/// Формат намеренно плоский: коллекция, режим, ключ сопоставления, идентификатор сообщения и
/// массив записей. Ничего, что нельзя собрать одной строкой в 1С, скрипте или планировщике.
///
/// <b>Ключи полей — латиницей, а имена папок — по-русски</b>, и это не разнобой. Файл читает
/// машина, и его собирает чужой программист: латинские ключи не ломаются ни в одной кодировке и
/// не требуют от отправителя ничего особенного. Папки же читает человек, который зашел на общий
/// диск посмотреть, что там лежит.
///
/// Модель без ввода-вывода: файлы, захват и раскладку по папкам ведет <see cref="InboxFolder"/>,
/// запись в коллекцию — укладка привязки. Здесь только формат и его проверка.
///
/// <code>
/// {
///   "collection": "Заказы",
///   "mode": "update",
///   "key": "Номер",
///   "id": "2026-09-10-0042",
///   "rows": [ { "Номер": "А-1", "Название": "Молоток" } ]
/// }
/// </code>
///
/// Режим «delete» шлет те же строки, но читается из них только колонка ключа. Режим
/// «ensure-collection» вместо строк шлет состав полей:
///
/// <code>
/// {
///   "collection": "Заказы",
///   "mode": "ensure-collection",
///   "fields": [ { "name": "Номер", "type": "text" }, { "name": "Сумма", "type": "number" } ]
/// }
/// </code>
/// </summary>
/// <param name="Collection">Имя коллекции, в которую ложатся записи. Имя, а не номер: номера
/// коллекций у каждой базы свои, а отправитель про базы ничего не знает.</param>
/// <param name="Mode">Что делать: добавлять, обновлять, удалять или готовить коллекцию.</param>
/// <param name="Key">Колонка сопоставления; у <see cref="InboxMode.Add"/> пустая.</param>
/// <param name="MessageId">Идентификатор сообщения — защита от повтора. Отправитель при обрыве
/// пришлет файл дважды, и это норма, а не сбой.</param>
/// <param name="RowsJson">Массив записей сырым текстом — тем же, что читает
/// <c>JsonTextSource</c>. Разбирать записи здесь незачем: дальше их читает ровно тот же источник
/// импорта, что и ответ чужой программы, и делать это дважды значило бы завести два разных
/// понимания одних и тех же данных.</param>
/// <param name="Rows">Сколько записей в массиве — для квитанции и для отказа «файл пустой».</param>
/// <param name="Fields">Состав полей (только <see cref="InboxMode.EnsureCollection"/>).</param>
/// <param name="ModeWord">Слово режима, как его написал отправитель, — для отказа
/// «режим не знаком»: отправителю нужно увидеть в квитанции СВОЕ слово.</param>
public sealed record InboxMessage(
    string    Collection,
    InboxMode Mode,
    string    Key,
    string    MessageId,
    string    RowsJson,
    int       Rows,
    IReadOnlyList<InboxField>? Fields   = null,
    string                     ModeWord = "")
{
    /// <summary>Пустой набор — чтобы проверка на <c>null</c> не расползлась по всем местам.</summary>
    public IReadOnlyList<InboxField> Schema => Fields ?? [];

    /// <summary>
    /// Что не так с сообщением; пустой список — можно принимать. Существует ли такая коллекция и
    /// есть ли в ней колонка ключа — вопрос укладки, там же и объясняется: приемник не обязан
    /// знать состав базы, чтобы отбраковать заведомо негодный файл.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (Mode == InboxMode.Unknown)
        {
            // Дальше проверять нечего: остальные требования зависят от режима, которого нет.
            problems.Add($"Режим «{ModeWord}» этому выпуску программы не знаком; "
                       + "программа умеет: add, update, delete, ensure-collection");
            return problems;
        }

        if (string.IsNullOrWhiteSpace(Collection))
            problems.Add("Не указана коллекция (поле «collection»)");

        if (Mode == InboxMode.EnsureCollection)
        {
            SchemaProblems(problems);
        }
        else if (Rows <= 0)
        {
            problems.Add("В сообщении нет записей (поле «rows»)");
        }

        if (Mode is InboxMode.Update or InboxMode.Delete && string.IsNullOrWhiteSpace(Key))
            problems.Add($"Режим «{Word(Mode)}» требует колонку сопоставления (поле «key»)");

        // Идентификатор обязателен именно у «добавить»: там от повтора не защищает ничего.
        // В остальных режимах повтор безвреден: вторая доставка перезапишет те же значения,
        // удалит уже удаленное или найдет коллекцию уже готовой.
        if (Mode == InboxMode.Add && string.IsNullOrWhiteSpace(MessageId))
            problems.Add("Режим «add» требует идентификатор сообщения (поле «id»): "
                       + "без него повторная доставка файла заведет записи второй раз");

        if (MessageId.Length > 0 && !ValidId(MessageId))
            problems.Add("Идентификатор сообщения годится только из букв, цифр, «-», «_» и «.» "
                       + "и не длиннее 100 знаков: он становится именем файла квитанции");

        return problems;
    }

    /// <summary>
    /// Проверка состава полей «ensure-collection». Строки при нем не принимаются НАРОЧНО:
    /// «подготовь место и сразу положи» — это два разных права и два разных отказа, и склеивать
    /// их в одно сообщение значило бы отвечать одной квитанцией на два вопроса.
    /// </summary>
    private void SchemaProblems(List<string> problems)
    {
        if (Rows > 0)
            problems.Add("Режим «ensure-collection» записи не принимает — "
                       + "пришлите их отдельным сообщением «add» или «update»");

        if (Schema.Count == 0)
        {
            problems.Add("Режим «ensure-collection» требует состав полей (поле «fields»)");
            return;
        }

        foreach (var field in Schema)
        {
            if (string.IsNullOrWhiteSpace(field.Name))
                problems.Add("У одного из полей не указано имя (поле «name»)");

            if (!InboxField.KnownTypes.Contains(field.Type, StringComparer.OrdinalIgnoreCase))
                problems.Add($"Тип «{field.Type}» у поля «{field.Name}» не поддерживается; "
                           + "можно: " + string.Join(", ", InboxField.KnownTypes)
                           + " — остальные типы заводит человек в программе");
        }
    }

    /// <summary>
    /// Идентификатор становится ИМЕНЕМ ФАЙЛА квитанции, а квитанция и есть память о принятых
    /// сообщениях. Поэтому разрешено только то, что безопасно в имени файла на любой системе:
    /// «../» и разделители пути увели бы запись из папки обмена куда угодно.
    /// </summary>
    public static bool ValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && id!.Length <= 100
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
        && id.Trim('.').Length > 0;   // «..» и «.» — не имена, а путь

    // ── Разбор ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Разобрать текст файла. <paramref name="error"/> заполняется, когда разобрать не удалось
    /// вовсе; негодное по составу сообщение возвращается целиком — про него скажет
    /// <see cref="Problems"/>, и отправитель получит квитанцию с причиной, а не молчание.
    /// </summary>
    public static InboxMessage? TryParse(string? text, out string error)
    {
        error = "";

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "файл пустой";
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling     = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException failure)
        {
            error = "файл не является JSON: " + failure.Message;
            return null;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "в файле ожидался объект с полями «collection», «mode» и «rows»";
                return null;
            }

            var word = Text(root, "mode");
            var mode = ModeOf(word);

            // Массив записей обязателен у всех режимов, кроме подготовки коллекции: там вместо
            // строк идет состав полей. У незнакомого режима массив не спрашивается тоже —
            // сообщение вернется целиком, и отказ назовет режим, а не отсутствие «rows».
            var hasRows = root.TryGetProperty("rows", out var rows)
                          && rows.ValueKind == JsonValueKind.Array;

            if (!hasRows && mode is not (InboxMode.EnsureCollection or InboxMode.Unknown))
            {
                error = "в файле нет массива записей «rows»";
                return null;
            }

            return new InboxMessage(
                Text(root, "collection"),
                mode,
                Text(root, "key"),
                Text(root, "id"),
                hasRows ? rows.GetRawText() : "[]",
                hasRows ? rows.GetArrayLength() : 0,
                ParseFields(root),
                word);
        }
    }

    /// <summary>
    /// Незнакомый режим — <see cref="InboxMode.Unknown"/>, и сообщение получит отказ словами.
    /// Пустая строка при этом остается «добавить»: режим в файле не указывают с первого дня
    /// формата, и ломать всех молчаливых отправителей из-за новых режимов нельзя.
    /// </summary>
    private static InboxMode ModeOf(string? mode) => (mode ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "add"         => InboxMode.Add,
        "update"            => InboxMode.Update,
        "delete"            => InboxMode.Delete,
        "ensure-collection" => InboxMode.EnsureCollection,
        _                   => InboxMode.Unknown,
    };

    /// <summary>Слово режима для текста отказа.</summary>
    private static string Word(InboxMode mode) => mode switch
    {
        InboxMode.Update           => "update",
        InboxMode.Delete           => "delete",
        InboxMode.EnsureCollection => "ensure-collection",
        _                          => "add",
    };

    /// <summary>
    /// Состав полей из «fields». Кривые элементы не выбрасываются, а читаются с пустым именем
    /// или типом: про них скажет <see cref="Problems"/>, и отправитель узнает о своей ошибке из
    /// квитанции, а не по молча пропавшему полю.
    /// </summary>
    private static IReadOnlyList<InboxField>? ParseFields(JsonElement root)
    {
        if (!root.TryGetProperty("fields", out var fields)
            || fields.ValueKind != JsonValueKind.Array)
            return null;

        var parsed = new List<InboxField>();

        foreach (var field in fields.EnumerateArray())
            parsed.Add(field.ValueKind == JsonValueKind.Object
                ? new InboxField(Text(field, "name"), Text(field, "type"))
                : new InboxField("", ""));

        return parsed;
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? ""
            : "";
}
