using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartBase.Integrations;

/// <summary>
/// Квитанция — ответ отправителю, который иначе не узнает о судьбе своего файла ничего.
///
/// У папки-приемника нет обратной связи по своей природе: файл положили и ушли. Квитанция —
/// единственное место, где написано, сколько записей принято, сколько обновлено и что не
/// прошло. Без нее обмен «работает» ровно до первой опечатки в имени коллекции, и никто об этом
/// не узнает неделями.
///
/// <b>Квитанция же служит памятью о принятых сообщениях.</b> Перед разбором приемник смотрит,
/// нет ли уже квитанции с таким идентификатором; есть — файл считается повтором и второй раз не
/// обрабатывается. Память намеренно живет в папке обмена, а не в <c>app.db</c>: папку видят все
/// машины, а <c>app.db</c> — только своя. Отправитель при обрыве пришлет файл дважды, и вторая
/// доставка вполне может попасть на соседний компьютер; память на одной машине такой повтор
/// пропустила бы.
///
/// Поля латиницей — по той же причине, что и у <see cref="InboxMessage"/>: квитанцию читает
/// машина отправителя.
/// </summary>
/// <param name="File">Имя исходного файла — по нему отправитель узнает свой.</param>
/// <param name="Id">Идентификатор сообщения; пусто — отправитель его не указал.</param>
/// <param name="Accepted">Файл принят и записи уложены.</param>
/// <param name="Collection">Коллекция, в которую легли записи.</param>
/// <param name="Received">Сколько записей было в файле.</param>
/// <param name="Created">Сколько записей заведено.</param>
/// <param name="Updated">Сколько обновлено.</param>
/// <param name="Skipped">Сколько пропущено по правилам привязки.</param>
/// <param name="Ambiguous">Сколько пропущено из-за нескольких записей с тем же ключом.</param>
/// <param name="Empty">Сколько пропущено из-за пустого ключа.</param>
/// <param name="Explanation">Причина отказа либо пояснение к принятому файлу.</param>
/// <param name="At">Когда обработано (ISO-8601).</param>
public sealed record InboxReceipt(
    [property: JsonPropertyName("file")]        string File,
    [property: JsonPropertyName("id")]          string Id,
    [property: JsonPropertyName("accepted")]    bool   Accepted,
    [property: JsonPropertyName("collection")]  string Collection,
    [property: JsonPropertyName("received")]    int    Received,
    [property: JsonPropertyName("created")]     int    Created,
    [property: JsonPropertyName("updated")]     int    Updated,
    [property: JsonPropertyName("skipped")]     int    Skipped,
    [property: JsonPropertyName("ambiguous")]   int    Ambiguous,
    [property: JsonPropertyName("empty")]       int    Empty,
    [property: JsonPropertyName("explanation")] string Explanation,
    [property: JsonPropertyName("at")]          string At)
{
    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        // Кириллица в квитанции остается кириллицей: имя коллекции и причина отказа читаются
        // человеком, а «Заказы» не читается никем.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Отказ: файл не принят, причина названа.</summary>
    public static InboxReceipt Rejected(string file, string id, string collection, int rows,
                                        string explanation, DateTimeOffset at) =>
        new(file, id, false, collection, rows, 0, 0, 0, 0, 0, explanation, Stamp(at));

    /// <summary>Принято: числа берутся из итога укладки.</summary>
    public static InboxReceipt Accept(string file, string id, string collection, int rows,
                                      BindingResult result, DateTimeOffset at,
                                      string explanation = "") =>
        new(file, id, true, collection, rows,
            result.Created, result.Updated, result.Skipped, result.Ambiguous, result.Empty,
            explanation, Stamp(at));

    /// <summary>
    /// Ответ СЦЕНАРИЯ на взятый из папки файл. Своей укладки у сценария нет: числа и слова
    /// приносят его шаги, и главное из них — ответ действия («зарегистрировано АБВГ.301121.001»).
    /// Ради этого ответа отправитель квитанцию и читает.
    ///
    /// Коллекция не называется. Шагов бывает несколько, и каждый пишет в свою: выбрать одну из
    /// них для ответа значило бы соврать в поле, которое читает машина отправителя.
    /// </summary>
    /// <param name="file">Имя файла отправителя.</param>
    /// <param name="id">Идентификатор сообщения; пусто — защиты от повтора нет.</param>
    /// <param name="accepted">Сценарий дошел до конца.</param>
    /// <param name="rows">Сколько записей завели его шаги.</param>
    /// <param name="explanation">Что случилось — словами, для человека и для отправителя.</param>
    /// <param name="at">Момент ответа.</param>
    public static InboxReceipt OfScenario(string file, string id, bool accepted, int rows,
                                          string explanation, DateTimeOffset at) =>
        new(file, id, accepted, "", 1, rows, 0, 0, 0, 0, explanation, Stamp(at));

    /// <summary>Текст квитанции для записи в файл.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Format);

    /// <summary>
    /// Строка одной фразой — для журнала и для экрана. То же, что увидит отправитель, только
    /// без разметки: расхождение между тем, что видят двое, само по себе источник споров.
    /// </summary>
    public string Summary()
    {
        if (!Accepted) return $"{File}: отказ — {Explanation}";

        var parts = new List<string>();
        if (Created   > 0) parts.Add($"создано {Created}");
        if (Updated   > 0) parts.Add($"обновлено {Updated}");
        if (Skipped   > 0) parts.Add($"пропущено {Skipped}");
        if (Ambiguous > 0) parts.Add($"неоднозначных {Ambiguous}");
        if (Empty     > 0) parts.Add($"без ключа {Empty}");

        var text = $"{File}: принято {Received} — "
                 + (parts.Count == 0 ? "изменений нет" : string.Join(", ", parts));

        return Explanation.Length > 0 ? text + "; " + Explanation : text;
    }

    /// <summary>
    /// Прочитать квитанцию; <c>null</c> — файл не читается. Битая квитанция намеренно НЕ считается
    /// отсутствующей: она все равно означает, что сообщение с таким идентификатором уже приходило,
    /// и обработать его второй раз хуже, чем не понять подробности прошлого раза.
    /// </summary>
    public static InboxReceipt? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            return JsonSerializer.Deserialize<InboxReceipt>(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Stamp(DateTimeOffset at) => at.ToString("o", CultureInfo.InvariantCulture);
}
