using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartBase.Integrations;

/// <summary>Соединение в переносимом виде — БЕЗ секрета.</summary>
/// <param name="Name">Имя карточки; по нему соединение сводится на той стороне.</param>
public sealed record BundleConnection(
    [property: JsonPropertyName("name")]     string Name,
    [property: JsonPropertyName("address")]  string Address,
    [property: JsonPropertyName("auth")]     string Auth,
    [property: JsonPropertyName("authName")] string AuthName,
    [property: JsonPropertyName("headers")]  IReadOnlyList<BundleHeader> Headers,
    [property: JsonPropertyName("timeout")]  int    TimeoutSeconds,
    [property: JsonPropertyName("retries")]  int    Retries);

/// <summary>Постоянный заголовок соединения или запроса.</summary>
public sealed record BundleHeader(
    [property: JsonPropertyName("name")]  string Name,
    [property: JsonPropertyName("value")] string Value);

/// <summary>Запрос в переносимом виде. Соединение названо ИМЕНЕМ: ключи у каждой машины свои.</summary>
public sealed record BundleRequest(
    [property: JsonPropertyName("name")]        string Name,
    [property: JsonPropertyName("connection")]  string Connection,
    [property: JsonPropertyName("method")]      string Method,
    [property: JsonPropertyName("path")]        string Path,
    [property: JsonPropertyName("headers")]     IReadOnlyList<BundleHeader> Headers,
    [property: JsonPropertyName("body")]        string Body,
    [property: JsonPropertyName("paging")]      string Paging,
    [property: JsonPropertyName("pagingField")] string PagingField,
    [property: JsonPropertyName("itemsPath")]   string ItemsPath);

/// <summary>Поток событий в переносимом виде. Соединение названо ИМЕНЕМ, как у запроса.</summary>
public sealed record BundleStream(
    [property: JsonPropertyName("name")]       string Name,
    [property: JsonPropertyName("connection")] string Connection,
    [property: JsonPropertyName("path")]       string Path,
    [property: JsonPropertyName("protocol")]   string Protocol,
    [property: JsonPropertyName("topics")]     IReadOnlyList<string> Topics,
    [property: JsonPropertyName("itemsPath")]  string ItemsPath,
    [property: JsonPropertyName("keyField")]   string KeyField);

/// <summary>
/// Триггер в переносимом виде. База и коллекция, поток и запрос ответа названы ИМЕНАМИ, потому
/// что номера у каждой машины свои; не нашлись на той стороне — сценарий приедет с триггером
/// «вручную», и об этом будет сказано в отчете.
/// </summary>
public sealed record BundleTrigger(
    [property: JsonPropertyName("kind")]       string Kind,
    [property: JsonPropertyName("minutes")]    int    Minutes,
    [property: JsonPropertyName("time")]       string Time,
    [property: JsonPropertyName("database")]   string Database,
    [property: JsonPropertyName("collection")] string Collection,
    [property: JsonPropertyName("stream")]     string Stream = "",
    [property: JsonPropertyName("reply")]      string Reply  = "",
    [property: JsonPropertyName("failText")]   string FailText = "");

/// <summary>Шаг в переносимом виде: запрос, база и коллекция — именами.</summary>
public sealed record BundleStep(
    [property: JsonPropertyName("kind")]       string Kind,
    [property: JsonPropertyName("request")]    string Request,
    [property: JsonPropertyName("text")]       string Text,
    [property: JsonPropertyName("extension")]  string Extension,
    [property: JsonPropertyName("action")]     string Action,
    [property: JsonPropertyName("database")]   string Database,
    [property: JsonPropertyName("collection")] string Collection,
    [property: JsonPropertyName("fields")]     IReadOnlyDictionary<string, string>? Fields,
    [property: JsonPropertyName("folder")]     string Folder,
    [property: JsonPropertyName("conditions")] IReadOnlyList<ScenarioCondition>? Conditions,
    [property: JsonPropertyName("format")]     string Format = "",
    [property: JsonPropertyName("formulas")]   IReadOnlyList<ScenarioFormula>? Formulas = null);

/// <summary>Сценарий в переносимом виде. Тумблер не переносится — см. описание набора.</summary>
public sealed record BundleScenario(
    [property: JsonPropertyName("name")]    string Name,
    [property: JsonPropertyName("trigger")] BundleTrigger Trigger,
    [property: JsonPropertyName("steps")]   IReadOnlyList<BundleStep> Steps);

/// <summary>
/// Набор настроек обмена одним файлом — то, чем люди обмениваются между машинами
/// (docs/ROADMAP-API.md, раздел 3: «обмен настройками между людьми — экспортом сценария в JSON
/// без секретов»).
///
/// <b>Три правила формата, и каждое следует из того, где эти настройки живут.</b>
///
/// <list type="number">
///   <item><b>Секретов нет.</b> Токены и пароли лежат в защищенном хранилище системы и не
///   попадают даже в файл базы; файл, который пересылают письмом, — последнее место, где им
///   быть. Соединение приезжает без секрета, и пока его не впишут, обмен по нему не пойдет.</item>
///   <item><b>Все связи — по именам.</b> Ключи строк у каждой машины свои: запрос №3 здесь и
///   там — разные запросы. Переносить номера значило бы привязать сценарий к чужой машине, а
///   имена человек видит на экране и может сверить глазами.</item>
///   <item><b>Тумблер не переносится.</b> Приехавший сценарий всегда выключен: файл открывают,
///   чтобы посмотреть, а не чтобы немедленно начать обмен со сторонней системой. Включают его руками,
///   уже разобравшись.</item>
/// </list>
///
/// Модель без ввода-вывода: файл читает и пишет экран, связывает имена с ключами
/// <c>ScenarioTransfer</c>, а здесь только формат и его разбор.
/// </summary>
public sealed record ScenarioBundle(
    [property: JsonPropertyName("version")]     int    Version,
    [property: JsonPropertyName("exported")]    string Exported,
    [property: JsonPropertyName("machine")]     string Machine,
    [property: JsonPropertyName("connections")] IReadOnlyList<BundleConnection> Connections,
    [property: JsonPropertyName("requests")]    IReadOnlyList<BundleRequest>    Requests,
    [property: JsonPropertyName("scenarios")]   IReadOnlyList<BundleScenario>   Scenarios,
    [property: JsonPropertyName("streams")]     IReadOnlyList<BundleStream>?    Streams = null)
{
    /// <summary>
    /// Версия формата, которую понимает эта сборка. Файл с большим номером — от более новой
    /// программы: разбирать его наугад нельзя, смысл полей мог смениться. Правило то же, что у
    /// политики организации.
    /// </summary>
    public const int SupportedVersion = 2;

    /// <summary>
    /// Версия файла без потоков событий. Такой файл пишется ею и дальше: его поймет и выпуск
    /// программы, который о потоках еще не знает.
    /// </summary>
    public const int PlainVersion = 1;

    /// <summary>
    /// Версия, которой пишется набор. Вторая ставится, только когда в наборе есть потоки:
    /// прежний выпуск программы прочитал бы такой файл, молча превратив сценарий потока в
    /// ручной, — а отказ «версия новее поддерживаемой» говорит правду.
    /// </summary>
    public static int VersionFor(IReadOnlyList<BundleStream> streams) =>
        streams.Count > 0 ? SupportedVersion : PlainVersion;

    /// <summary>Потоки набора; в файле прежней версии их нет.</summary>
    [JsonIgnore]
    public IReadOnlyList<BundleStream> StreamList => Streams ?? [];

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        // Кириллица остается кириллицей: файл читают глазами и правят руками — имена коллекций
        // и текст сообщений должны быть видны.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Текст файла.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Format);

    /// <summary>Сколько всего в наборе — для заголовка окна и отчета.</summary>
    public string Summary() =>
        $"сценариев {Scenarios.Count}, запросов {Requests.Count}, соединений {Connections.Count}"
        + (StreamList.Count > 0 ? $", потоков событий {StreamList.Count}" : "");

    /// <summary>
    /// Разобрать файл. <c>null</c> — не принят; причина в <paramref name="error"/> и
    /// показывается как есть. Правило то же, что у политики: <b>что не разобралось, то не
    /// принимается</b> — половина набора хуже его отсутствия, потому что отсутствие видно, а
    /// потерянный шаг нет.
    /// </summary>
    public static ScenarioBundle? TryParse(string? text, out string error)
    {
        error = "";

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "файл пуст";
            return null;
        }

        ScenarioBundle? bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<ScenarioBundle>(text, new JsonSerializerOptions
            {
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException failure)
        {
            error = "файл не разобрался: " + failure.Message;
            return null;
        }

        if (bundle is null)
        {
            error = "в файле ожидался набор настроек обмена";
            return null;
        }

        if (bundle.Version > SupportedVersion)
        {
            error = $"версия формата {bundle.Version} новее поддерживаемой ({SupportedVersion})";
            return null;
        }

        if (bundle.Scenarios is null || bundle.Scenarios.Count == 0)
        {
            error = "в файле нет ни одного сценария";
            return null;
        }

        return bundle with
        {
            Connections = bundle.Connections ?? [],
            Requests    = bundle.Requests    ?? [],
        };
    }

    /// <summary>Имя файла по умолчанию — то, что предлагают в окне сохранения.</summary>
    public static string FileName(DateTimeOffset now) =>
        $"Обмен — настройки {now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.json";
}
