using System.Text.Encodings.Web;
using System.Text.Json;

namespace SmartBase.Integrations;

/// <summary>
/// Договор входящего канала: адреса и параметр выбора базы. Лежит у двери, потому что его читают
/// обе стороны — слушатель, разбирающий запрос, и описание API, которое пересылают отправителю.
/// Раздвоение этих строк означало бы, что программа принимает по одному адресу, а обещает
/// другой.
/// </summary>
public static class IngressApi
{
    /// <summary>Куда присылают сообщения.</summary>
    public const string InboxPath = "/v1/inbox";

    /// <summary>Проверка связи и выбранной базы.</summary>
    public const string PingPath = "/v1/ping";

    /// <summary>Описание схемы коллекций.</summary>
    public const string SchemaPath = "/v1/schema";

    /// <summary>Чтение записей выбранной коллекции.</summary>
    public const string RecordsPath = "/v1/records";

    /// <summary>Журнал изменений коллекции; отвечает и про удаления.</summary>
    public const string ChangesPath = "/v1/changes";

    /// <summary>Параметр запроса с ключом базы из реестра приложения.</summary>
    public const string DatabaseParameter = "database";
}

/// <summary>
/// Обращение сторонней программы — уже прочитанное и отвязанное от сокета.
///
/// Лежит рядом с дверью, а не с обработчиком, намеренно: слушатель ничего не знает про базы,
/// а обработчик — ни про сокеты. Между ними только эта пара записей, и благодаря
/// ей прием проверяется тестами целиком, не открывая ни одного порта.
/// </summary>
/// <param name="Method">Метод: GET, POST.</param>
/// <param name="Path">Путь без адреса и без параметров запроса.</param>
/// <param name="Body">Тело запроса.</param>
/// <param name="Remote">Откуда пришли — для журнала. На петлевом адресе это всегда
/// <c>127.0.0.1</c>, и поле существует ради сетевого режима.</param>
/// <param name="Query">Строка параметров как пришла, с «?» или без; разбирает ее
/// <see cref="Param"/>. Нужна чтению: коллекция, момент и курсор едут параметрами.</param>
public sealed record IngressCall(string Method, string Path, string Body,
                                 string Remote = "", string Query = "")
{
    /// <summary>
    /// Значение параметра запроса; пусто — параметра нет. Разбор свой и нарочно простой:
    /// имена и курсоры — одна строка без повторов, а тянуть ради нее разбор форм незачем.
    /// </summary>
    public string Param(string name)
    {
        string? found = null;
        foreach (var pair in (Query ?? "").TrimStart('?').Split('&',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;

            if (!string.Equals(Uri.UnescapeDataString(pair[..eq]), name,
                               StringComparison.OrdinalIgnoreCase))
                continue;

            if (found is not null)
                throw new FormatException($"Параметр «{name}» указан больше одного раза.");

            // Плюс — пробел: так кодируют формы, и «Заказы за сутки» приходит с плюсами.
            found = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')).Trim();
        }

        return found ?? "";
    }
}

/// <summary>Ответ отправителю: код и тело. Тело всегда JSON — отвечать сторонней программе
/// страницей с разметкой незачем.</summary>
public sealed record IngressAnswer(int Status, string Body)
{
    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        Encoder       = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Отказ с причиной словами. Причина по-русски: ее читает человек, который
    /// налаживает обмен, а не программа, которая его ведет.</summary>
    public static IngressAnswer Problem(int status, string explanation) =>
        new(status, JsonSerializer.Serialize(new { ok = false, error = explanation }, Format));

    /// <summary>Ответ с готовым телом.</summary>
    public static IngressAnswer Ok(string body) => new(200, body);

    /// <summary>Ответ объектом.</summary>
    public static IngressAnswer Ok(object value) =>
        new(200, JsonSerializer.Serialize(value, Format));
}
