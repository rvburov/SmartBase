using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SmartBase.Database.Repositories;
using SmartBase.Integrations;

namespace SmartBase.Database;

/// <summary>
/// Описание собственного API: что у нас есть, куда стучаться и как выглядит сообщение
/// (docs/ROADMAP-API.md, раздел 7 — «создал коллекцию с полями, и на вкладке готовые адреса,
/// примеры запроса и ответа»).
///
/// Два вида одного и того же, и оба нужны по-разному. <see cref="Text"/> читает ЧЕЛОВЕК: его
/// показывают на экране, кладут в буфер и пересылают тому, кто будет писать отправителя.
/// <see cref="Json"/> читает МАШИНА — его отдает <c>GET /v1/schema</c>, чтобы сторонняя программа
/// могла свериться со схемой, не открывая переписку.
///
/// Собираются оба из одной модели: разойдись они, и пересланное описание перестало бы совпадать
/// с тем, что отдает сама программа, — а спорить в этот момент будет некому.
/// </summary>
public static class ApiDescription
{
    // Адреса и параметр выбора базы берутся у двери: описание обязано обещать ровно
    // то, что слушатель принимает.

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        // Кириллица в описании остается кириллицей: имена коллекций читает человек, а
        // «Заказы» не читает никто.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Описание для человека. Адрес передается готовым: собирать его здесь значило бы завести
    /// второе мнение о том, где программа слушает.
    /// </summary>
    /// <param name="address">Адрес слушателя; пусто — прием выключен, и это сказано прямо.</param>
    /// <param name="database">Имя базы.</param>
    /// <param name="collections">Схема коллекций.</param>
    public static string Text(string address, string database,
                              IReadOnlyList<ApiCollection> collections, int? databaseId = null)
    {
        var text = new StringBuilder();
        var selector = $"{IngressApi.DatabaseParameter}=" + (databaseId is > 0
            ? databaseId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "<ключ базы>");

        text.AppendLine($"Прием данных в базу «{database}»");
        text.AppendLine();

        if (address.Length == 0)
        {
            text.AppendLine("Прием по сети сейчас выключен: адреса нет, и обратиться некуда.");
            text.AppendLine("Включается он в разделе «Настройки → Интеграции → Доступ извне».");
            text.AppendLine();
        }
        else
        {
            text.AppendLine($"Адрес:      {address}");
            text.AppendLine($"Проверка:   GET  {address}{IngressApi.PingPath}?{selector}");
            text.AppendLine($"Схема:      GET  {address}{IngressApi.SchemaPath}?{selector}");
            text.AppendLine($"Сообщение:  POST {address}{IngressApi.InboxPath}?{selector}");
            text.AppendLine($"Чтение:     GET  {address}{IngressApi.RecordsPath}?{selector}&collection=<имя>");
            text.AppendLine($"Изменения:  GET  {address}{IngressApi.ChangesPath}?{selector}&collection=<имя>");
            text.AppendLine();
        }

        text.AppendLine("API работает без входа и токенов. Параметр «database» выбирает открытую базу "
                      + "по ключу из реестра приложения. Если открыта ровно одна база, параметр можно не указывать.");
        text.AppendLine();

        text.AppendLine("Тело сообщения — такое же, как у файла в папке обмена:");
        text.AppendLine();
        text.AppendLine("""
            {
              "collection": "<имя коллекции>",
              "mode": "update",
              "key": "<колонка, по которой ищется запись>",
              "id": "<номер сообщения: защита от повторной доставки>",
              "rows": [ { "<колонка>": "<значение>" } ]
            }
            """);
        text.AppendLine();
        text.AppendLine("«mode»: «add» — добавить, «update» — найти по ключу и обновить "
                      + "(ненайденную запись заведет),");
        text.AppendLine("«delete» — найти по ключу и удалить (читается только колонка ключа),");
        text.AppendLine("«ensure-collection» — создать коллекцию и добрать недостающие поля; вместо «rows» — "
                      + "«fields»: [{ \"name\": \"Номер\", \"type\": \"text\" }], типы: "
                      + string.Join(", ", InboxField.KnownTypes) + ". Существующие поля не "
                      + "трогаются, несовпадение типов — отказ.");
        text.AppendLine("«id» обязателен у «add»: без него повторная доставка заведет записи "
                      + "второй раз. Идентификатор должен быть уникальным в пределах базы. Повтор с тем же «id» возвращает ТОТ ЖЕ ответ.");
        text.AppendLine();

        // Потолок тела называется отправителю ЗДЕСЬ, а не выясняется им по отказу: у сети он
        // есть и обязан быть — тело надо принять целиком, прежде чем станет понятно, что это.
        // У файловых каналов (папка обмена, --inbox) потолка нет вовсе, и это сказано рядом:
        // иначе первое же число прочиталось бы как общее правило.
        text.AppendLine($"Потолок тела одного обращения — {BodyLimit()}. Он относится только к "
                      + "сети: файл в папке обмена и файл, переданный ключом «--inbox», читаются "
                      + "потоком, и их размер не ограничен.");
        text.AppendLine();
        text.AppendLine("В ответ приходит квитанция: сколько создано, обновлено, удалено и что "
                      + "не перенеслось.");
        text.AppendLine();

        text.AppendLine("Коллекции и их колонки");
        text.AppendLine();

        if (collections.Count == 0)
        {
            text.AppendLine("    В базе нет ни одной коллекции.");
            return text.ToString();
        }

        foreach (var collection in collections)
        {
            text.AppendLine($"  «{collection.Name}»" + (collection.Id is int collectionKey ? $" · collectionId={collectionKey}" : ""));
            text.AppendLine($"      {collection.Title} — название записи");

            foreach (var field in collection.Fields)
                text.AppendLine($"      {field.Name} — {field.Type}" + (field.Id is int fieldKey ? $" · ключ {fieldKey}" : ""));

            text.AppendLine();
        }

        text.AppendLine("Колонки сообщения сводятся со свойствами ПО ИМЕНАМ; то, чему места не нашлось, "
                      + "перечислено в квитанции. Для приема имя коллекции должно быть уникальным в базе: "
                      + "при совпадениях сообщение отклоняется, записи не меняются.");
        text.AppendLine("Параметр «collectionId» поддерживается только при чтении. "
                      + $"POST {IngressApi.InboxPath} с этим параметром возвращает 400: коллекцию задают в JSON.");
        text.AppendLine();

        text.AppendLine("Чтение");
        text.AppendLine();
        text.AppendLine("Для чтения коллекцию выбирают именем «collection» либо ключом «collectionId» "
                      + "из описания схемы. Числовое имя остается именем. При одноименных коллекциях нужен ключ.");
        text.AppendLine("Историю одноименных коллекций разделить нельзя: старые строки хранят только имя. "
                      + "Запрос такой истории возвращает 409 и не смешивает события разных коллекций.");
        text.AppendLine("Чтение возвращает название записи и все свойства коллекции. Параметром «fields» "
                      + "можно выбрать состав: «t» — название, далее ключи или имена свойств через запятую. "
                      + "Свойство с именем «t» или числовым именем выбирайте по его ключу.");
        text.AppendLine("В «columns» указаны key, name и fieldKey выбранных колонок. При уникальных именах «values» "
                      + "содержит значения под именами. Если имена повторяются, все ключи «values» — «t» "
                      + "и численные ключи свойств; «columns» однозначно описывает каждое значение.");
        text.AppendLine();
        text.AppendLine($"    GET {IngressApi.RecordsPath}?{selector}&collection=<имя>&since=<момент>&after=<ключ>&limit=<число>");
        text.AppendLine("      «since» — только записи, правленные с момента (ISO-8601); «after» — "
                      + "ключ записи из поля «next» прошлого ответа; страница до "
                      + "500 записей. В ответе: id, updated и values с выбранными полями.");
        text.AppendLine();
        text.AppendLine($"    GET {IngressApi.ChangesPath}?{selector}&collection=<имя>&after=<номер>&limit=<число>");
        text.AppendLine("      Хвост журнала изменений коллекции — отвечает и про УДАЛЕНИЯ, о "
                      + "которых чтение записей молчит. В журнале имена записей, а не ключи: он "
                      + "ведется для человека. «after» — номер из поля «next» прошлого ответа.");

        return text.ToString();
    }

    /// <summary>Описание для машины — ответ <c>GET /v1/schema</c>.</summary>
    public static string Json(string database, IReadOnlyList<ApiCollection> collections, int? databaseId = null) =>
        JsonSerializer.Serialize(new
        {
            database,
            databaseId,
            databaseParameter = IngressApi.DatabaseParameter,
            authentication = "none",
            inbox   = IngressApi.InboxPath,
            ping    = IngressApi.PingPath,
            schema  = IngressApi.SchemaPath,
            records = IngressApi.RecordsPath + "?database=<ключ базы>&collection=<имя>&since=<момент>&after=<ключ>",
            changes = IngressApi.ChangesPath + "?database=<ключ базы>&collection=<имя>&after=<номер>",
            message = new
            {
                collection = "<имя коллекции>",
                mode       = "add | update | delete | ensure-collection",
                key        = "<колонка сопоставления; нужна у update и delete>",
                id         = "<номер сообщения>",
                rows       = new[] { new Dictionary<string, string> { ["<колонка>"] = "<значение>" } },
                fields     = new[] { new { name = "<имя поля>",
                                           type = string.Join(" | ", InboxField.KnownTypes) } },
            },
            collections = collections.Select(c => new
            {
                id = c.Id,
                name   = c.Name,
                title  = c.Title,
                fields = c.Fields.Select(f => new { id = f.Id, name = f.Name, type = f.Type }),
            }),
        }, Format);

    /// <summary>
    /// Имя файла, под которым описание предлагается сохранить. Отдельной строкой, потому что
    /// его называют и экран, и будущая выгрузка, а расхождение в имени файла — лишний повод
    /// спросить «то ли это».
    /// </summary>
    public static string FileName(string database) =>
        $"Прием данных — {database}.txt";

    /// <summary>
    /// Потолок тела обращения словами — для экрана и описания. Берется у двери, а не пишется
    /// числом рядом: два числа однажды разойдутся.
    /// </summary>
    public static string BodyLimit() =>
        $"{IngressGate.MaxBodyBytes / 1024 / 1024} МБ";
}
