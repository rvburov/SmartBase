using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SmartBase.Core.Importers;
using SmartBase.Core.Importers.TableSources;

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

    /// <summary>Создать коллекцию по схеме и добрать недостающие поля: сторонняя программа готовит
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
/// Входящее сообщение — файл, который сторонняя программа кладет в папку обмена.
///
/// Формат намеренно плоский: коллекция, режим, ключ сопоставления, идентификатор сообщения и
/// массив записей. Ничего, что нельзя собрать одной строкой в учетной системе, скрипте или планировщике.
///
/// <b>Ключи полей — латиницей, а имена папок — по-русски</b>, и это не разнобой. Файл читает
/// машина, и его собирает сторонней программист: латинские ключи не ломаются ни в одной кодировке и
/// не требуют от отправителя ничего особенного. Папки же читает человек, который зашел на общий
/// диск посмотреть, что там лежит.
///
/// Захват файлов и раскладку по папкам ведет <see cref="InboxFolder"/>, запись в коллекцию —
/// укладка привязки. Здесь только формат и его проверка — и ОДНО исключение из «без
/// ввода-вывода»: <see cref="TryParseFile"/> открывает файл сам. Иначе не выходит: чтобы прочитать
/// из большого файла только конверт, не втащив в память записи, надо идти по файлу токенами, а
/// значит держать поток. Отдать эту работу файловому слою значило бы переселить туда знание
/// формата — и завести второе его понимание.
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
/// импорта, что и ответ сторонней программы, и делать это дважды значило бы завести два разных
/// понимания одних и тех же данных. Пусто у сообщения из файла — там записи остаются в файле
/// (<paramref name="RowsFile"/>).</param>
/// <param name="Rows">Сколько записей в массиве — для квитанции и для отказа «файл пустой».</param>
/// <param name="Fields">Состав полей (только <see cref="InboxMode.EnsureCollection"/>).</param>
/// <param name="ModeWord">Слово режима, как его написал отправитель, — для отказа
/// «режим не знаком»: отправителю нужно увидеть в квитанции СВОЕ слово.</param>
/// <param name="RowsFile">Путь к файлу, в котором лежат записи; пусто — они в
/// <paramref name="RowsJson"/>. Заполняется только разбором файла
/// (<see cref="TryParseFile"/>): записи оттуда читаются ПОТОКОМ и в память целиком не попадают
/// никогда, поэтому предела на размер файла у этого канала нет.</param>
public sealed record InboxMessage(
    string    Collection,
    InboxMode Mode,
    string    Key,
    string    MessageId,
    string    RowsJson,
    int       Rows,
    IReadOnlyList<InboxField>? Fields   = null,
    string                     ModeWord = "",
    string                     RowsFile = "")
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
    /// Разобрать текст сообщения. <paramref name="error"/> заполняется, когда разобрать не удалось
    /// вовсе; негодное по составу сообщение возвращается целиком — про него скажет
    /// <see cref="Problems"/>, и отправитель получит квитанцию с причиной, а не молчание.
    ///
    /// Этим путем приходит тело HTTP-обращения: оно уже в памяти и уже обрезано потолком двери.
    /// </summary>
    public static InboxMessage? TryParse(string? text, out string error)
    {
        error = "";

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "файл пустой";
            return null;
        }

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return Read(stream, rowsFile: "", out error);
    }

    /// <summary>
    /// Разобрать сообщение из ФАЙЛА, не читая файл в память.
    ///
    /// <b>Чем отличается от <see cref="TryParse"/> и зачем существует.</b> Разбор один и тот же —
    /// оба пути идут через <see cref="Read"/>, и второго понимания формата не заводится. Разница в
    /// одном: массив записей отсюда не забирается в память, а остается в файле, и вместо него
    /// сообщение несет путь (<see cref="RowsFile"/>). Записи потом читает потоковый источник
    /// импорта — тот же, которым программа читает любой большой файл.
    ///
    /// Ради этого все и сделано: пока конверт разбирался из строки, прочитанной целиком, файл в
    /// общей папке приходилось ограничивать по размеру, потому что один такой файл стоил
    /// нескольких своих размеров памяти. Теперь память не зависит от размера файла вовсе.
    /// </summary>
    /// <param name="path">Путь к файлу; он же попадет в <see cref="RowsFile"/>.</param>
    /// <param name="error">Почему не разобралось; пусто — разобралось.</param>
    public static InboxMessage? TryParseFile(string path, out string error)
    {
        error = "";

        try
        {
            using var stream = OpenFileAfterShortSharingConflict(path);

            if (stream.Length == 0)
            {
                error = "файл пустой";
                return null;
            }

            return Read(stream, path, out error);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            error = "файл не прочитался: " + failure.Message;
            return null;
        }
    }

    // Ready соседней копии кратко открывает исходный файл с FileShare.None. На Unix
    // переименование не снимает блокировку inode: захват уже наш, но сосед еще закрывает
    // свой проверочный дескриптор. Ждем только открытия, не повторяем разбор JSON и
    // не ослабляем FileShare. Постоянный отказ вернется прежним ответом через полсекунды.
    private static FileStream OpenFileAfterShortSharingConflict(string path)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
            catch (IOException failure) when (IsSharingConflict(failure)
                && Stopwatch.GetElapsedTime(started).TotalMilliseconds < 500)
            {
                Thread.Sleep(10);
            }
        }
    }

    // Windows возвращает HRESULT_FROM_WIN32(ERROR_SHARING_VIOLATION). Unix .NET
    // оставляет в HResult исходный errno EWOULDBLOCK: 11 на Linux, 35 на macOS.
    // Другие IO-ошибки (нет файла, отказ доступа, ошибка диска) здесь не повторяются.
    private static bool IsSharingConflict(IOException failure) =>
        OperatingSystem.IsWindows() && failure.HResult == unchecked((int)0x80070020)
        || OperatingSystem.IsLinux() && failure.HResult == 11
        || OperatingSystem.IsMacOS() && failure.HResult == 35;

    /// <summary>
    /// Общий разбор конверта — по одному токену, без чтения потока целиком.
    ///
    /// Поля конверта отправитель вправе написать в любом порядке, в том числе ПОСЛЕ массива
    /// записей, — поэтому массив приходится проходить, а не бросать чтение на нем. Проходится он
    /// пропуском (<see cref="JsonCursor.Skip"/>), то есть без памяти, и заодно считается: число
    /// записей нужно и квитанции, и отказу «в сообщении нет записей», а отдельным проходом по
    /// файлу за него платить не пришлось.
    /// </summary>
    /// <param name="stream">Поток сообщения.</param>
    /// <param name="rowsFile">Путь к файлу — тогда записи остаются в нем; пусто — записи
    /// забираются текстом в <see cref="RowsJson"/>.</param>
    private static InboxMessage? Read(Stream stream, string rowsFile, out string error)
    {
        error = "";

        try
        {
            var cursor = new JsonCursor(stream);

            if (cursor.Peek() != JsonTokenType.StartObject)
            {
                error = "в файле ожидался объект с полями «collection», «mode» и «rows»";
                return null;
            }

            cursor.Consume();

            var collection = "";
            var word       = "";
            var key        = "";
            var id         = "";
            var rowsJson   = "[]";
            var hasRows    = false;
            long rows      = 0;
            IReadOnlyList<InboxField>? fields = null;

            // Повтор имени в конверте — не наш случай, но поведение на нем менять молча нельзя:
            // разбор через JsonDocument брал ПЕРВОЕ вхождение, и здесь берется тоже первое.
            var seen = new HashSet<string>(StringComparer.Ordinal);

            while (true)
            {
                var token = cursor.Peek();

                if (token is JsonTokenType.EndObject or JsonTokenType.None) break;
                if (token != JsonTokenType.PropertyName) throw JsonCursor.Broken();

                var name = cursor.TakeName();

                if (!seen.Add(name)) { cursor.Skip(); continue; }

                switch (name)
                {
                    case "collection": collection = Scalar(cursor); break;
                    case "mode":       word       = Scalar(cursor); break;
                    case "key":        key        = Scalar(cursor); break;
                    case "id":         id         = Scalar(cursor); break;

                    case "fields":
                        if (cursor.Peek() != JsonTokenType.StartArray) { cursor.Skip(); break; }
                        using (var list = cursor.Take()) fields = ParseFields(list.RootElement);
                        break;

                    case "rows":
                        if (cursor.Peek() != JsonTokenType.StartArray) { cursor.Skip(); break; }
                        hasRows = true;

                        if (rowsFile.Length > 0)
                        {
                            rows = cursor.Skip();
                        }
                        else
                        {
                            using var array = cursor.Take();
                            rowsJson = array.RootElement.GetRawText();
                            rows     = array.RootElement.GetArrayLength();
                        }
                        break;

                    default: cursor.Skip(); break;   // чужое поле — мимо, и не в память
                }
            }

            var mode = ModeOf(word);

            // Массив записей обязателен у всех режимов, кроме подготовки коллекции: там вместо
            // строк идет состав полей. У незнакомого режима массив не спрашивается тоже —
            // сообщение вернется целиком, и отказ назовет режим, а не отсутствие «rows».
            if (!hasRows && mode is not (InboxMode.EnsureCollection or InboxMode.Unknown))
            {
                error = "в файле нет массива записей «rows»";
                return null;
            }

            return new InboxMessage(
                collection,
                mode,
                key,
                id,
                hasRows ? rowsJson : "[]",
                // Два миллиарда записей в одном сообщении — не обмен, а сбой у отправителя;
                // счетчик упирается в потолок, а разбираться с таким файлом все равно человеку.
                (int)Math.Min(int.MaxValue, rows),
                fields,
                word,
                hasRows ? rowsFile : "");
        }
        catch (ImportException failure)
        {
            // Отправителю в квитанции нужен РОД беды — «файл не является JSON»: чинить он пойдет
            // свою выгрузку. Про развал структуры курсор говорит нашими внутренними словами, и
            // вместе они читались бы заиканием, поэтому этот случай называется здесь заново.
            error = failure.Message == JsonCursor.BrokenText
                ? "файл не является JSON: структура нарушена или файл оборван"
                : "файл не является JSON: " + failure.Message;

            return null;
        }
    }

    /// <summary>Скалярное поле конверта текстом; не строка — пусто, как и было при разборе целиком.</summary>
    private static string Scalar(JsonCursor cursor)
    {
        using var value = cursor.Take();

        return value.RootElement.ValueKind == JsonValueKind.String
            ? value.RootElement.GetString()?.Trim() ?? ""
            : "";
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
    private static IReadOnlyList<InboxField> ParseFields(JsonElement fields)
    {
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
