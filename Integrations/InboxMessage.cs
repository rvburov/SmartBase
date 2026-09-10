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
/// </summary>
/// <param name="Collection">Имя коллекции, в которую ложатся записи. Имя, а не номер: номера
/// коллекций у каждой базы свои, а отправитель про базы ничего не знает.</param>
/// <param name="Mode">Добавлять или искать по ключу и обновлять.</param>
/// <param name="Key">Колонка сопоставления; у <see cref="InboxMode.Add"/> пустая.</param>
/// <param name="MessageId">Идентификатор сообщения — защита от повтора. Отправитель при обрыве
/// пришлет файл дважды, и это норма, а не сбой.</param>
/// <param name="RowsJson">Массив записей сырым текстом — тем же, что читает
/// <c>JsonTextSource</c>. Разбирать записи здесь незачем: дальше их читает ровно тот же источник
/// импорта, что и ответ чужой программы, и делать это дважды значило бы завести два разных
/// понимания одних и тех же данных.</param>
/// <param name="Rows">Сколько записей в массиве — для квитанции и для отказа «файл пустой».</param>
public sealed record InboxMessage(
    string    Collection,
    InboxMode Mode,
    string    Key,
    string    MessageId,
    string    RowsJson,
    int       Rows)
{
    /// <summary>
    /// Что не так с сообщением; пустой список — можно принимать. Существует ли такая коллекция и
    /// есть ли в ней колонка ключа — вопрос укладки, там же и объясняется: приемник не обязан
    /// знать состав базы, чтобы отбраковать заведомо негодный файл.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Collection))
            problems.Add("Не указана коллекция (поле «collection»)");

        if (Rows <= 0)
            problems.Add("В сообщении нет записей (поле «rows»)");

        if (Mode == InboxMode.Update && string.IsNullOrWhiteSpace(Key))
            problems.Add("Режим «update» требует колонку сопоставления (поле «key»)");

        // Идентификатор обязателен именно у «добавить»: там от повтора не защищает ничего.
        // В режиме «обновить по ключу» повтор безвреден — вторая доставка того же файла
        // перезапишет те же записи теми же значениями.
        if (Mode == InboxMode.Add && string.IsNullOrWhiteSpace(MessageId))
            problems.Add("Режим «add» требует идентификатор сообщения (поле «id»): "
                       + "без него повторная доставка файла заведет записи второй раз");

        if (MessageId.Length > 0 && !ValidId(MessageId))
            problems.Add("Идентификатор сообщения годится только из букв, цифр, «-», «_» и «.» "
                       + "и не длиннее 100 знаков: он становится именем файла квитанции");

        return problems;
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

            if (!root.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                error = "в файле нет массива записей «rows»";
                return null;
            }

            return new InboxMessage(
                Text(root, "collection"),
                ModeOf(Text(root, "mode")),
                Text(root, "key"),
                Text(root, "id"),
                rows.GetRawText(),
                rows.GetArrayLength());
        }
    }

    /// <summary>
    /// Незнакомый режим читается как «добавить» — самый безобидный: он ничего не перезаписывает.
    /// Строку мог написать более новый выпуск программы-отправителя, и превращать это в отказ
    /// принять файл незачем; идентификатор при этом обязателен, так что и дубля не будет.
    /// </summary>
    private static InboxMode ModeOf(string? mode) => (mode ?? "").Trim().ToLowerInvariant() switch
    {
        "update" => InboxMode.Update,
        _        => InboxMode.Add,
    };

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? ""
            : "";
}
