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
/// <see cref="Json"/> читает МАШИНА — его отдает <c>GET /v1/schema</c>, чтобы чужая программа
/// могла свериться со схемой, не открывая переписку.
///
/// Собираются оба из одной модели: разойдись они, и пересланное описание перестало бы совпадать
/// с тем, что отдает сама программа, — а спорить в этот момент будет некому.
/// </summary>
public static class ApiDescription
{
    // Адреса и заголовок токена берутся у двери (IngressApi): описание обязано обещать ровно
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
    /// <param name="database">Имя базы, в которую пишет токен.</param>
    /// <param name="collections">Схема коллекций.</param>
    public static string Text(string address, string database,
                              IReadOnlyList<ApiCollection> collections)
    {
        var text = new StringBuilder();

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
            text.AppendLine($"Проверка:   GET  {address}{IngressApi.PingPath}");
            text.AppendLine($"Схема:      GET  {address}{IngressApi.SchemaPath}");
            text.AppendLine($"Сообщение:  POST {address}{IngressApi.InboxPath}");
            text.AppendLine($"Чтение:     GET  {address}{IngressApi.RecordsPath}?collection=<имя>");
            text.AppendLine($"Изменения:  GET  {address}{IngressApi.ChangesPath}?collection=<имя>");
            text.AppendLine();
        }

        text.AppendLine($"Токен едет заголовком «{IngressApi.TokenHeader}» либо «Authorization: Bearer …».");
        text.AppendLine("Токен выдается один раз и больше нигде не показывается.");
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
                      + "(ненайденную запись заведет).");
        text.AppendLine("«id» обязателен у «add»: без него повторная доставка заведет записи "
                      + "второй раз. Повтор с тем же «id» возвращает ТОТ ЖЕ ответ.");
        text.AppendLine();
        text.AppendLine("В ответ приходит квитанция: сколько создано, сколько обновлено и что "
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
            text.AppendLine($"  «{collection.Name}»");
            text.AppendLine($"      {collection.Title} — название записи");

            foreach (var field in collection.Fields)
                text.AppendLine($"      {field.Name} — {field.Type}");

            text.AppendLine();
        }

        text.AppendLine("Колонки сводятся со свойствами ПО ИМЕНАМ; то, чему места не нашлось, "
                      + "перечислено в квитанции.");
        text.AppendLine();

        text.AppendLine("Чтение");
        text.AppendLine();
        text.AppendLine("Чтение закрыто ВЫДАЧЕЙ: ее подтверждает ответственный за интеграции на "
                      + "компьютере-приемнике — на одну коллекцию, поименованные поля и срок. Без "
                      + "выдачи любой GET отвечает отказом с причиной.");
        text.AppendLine();
        text.AppendLine($"    GET {IngressApi.RecordsPath}?collection=<имя>&since=<момент>&after=<ключ>&limit=<число>");
        text.AppendLine("      «since» — только записи, правленные с момента (ISO-8601); «after» — "
                      + "ключ записи из поля «next» прошлого ответа; страница до "
                      + "500 записей. В ответе: id, updated и values с полями выдачи.");
        text.AppendLine();
        text.AppendLine($"    GET {IngressApi.ChangesPath}?collection=<имя>&after=<номер>&limit=<число>");
        text.AppendLine("      Хвост журнала изменений коллекции — отвечает и про УДАЛЕНИЯ, о "
                      + "которых чтение записей молчит. В журнале имена записей, а не ключи: он "
                      + "ведется для человека. «after» — номер из поля «next» прошлого ответа.");

        return text.ToString();
    }

    /// <summary>Описание для машины — ответ <c>GET /v1/schema</c>.</summary>
    public static string Json(string database, IReadOnlyList<ApiCollection> collections) =>
        JsonSerializer.Serialize(new
        {
            database,
            inbox   = IngressApi.InboxPath,
            ping    = IngressApi.PingPath,
            schema  = IngressApi.SchemaPath,
            records = IngressApi.RecordsPath + "?collection=<имя>&since=<момент>&after=<ключ>",
            changes = IngressApi.ChangesPath + "?collection=<имя>&after=<номер>",
            message = new
            {
                collection = "<имя коллекции>",
                mode       = "add | update",
                key        = "<колонка сопоставления; нужна у update>",
                id         = "<номер сообщения>",
                rows       = new[] { new Dictionary<string, string> { ["<колонка>"] = "<значение>" } },
            },
            collections = collections.Select(c => new
            {
                name   = c.Name,
                title  = c.Title,
                fields = c.Fields.Select(f => new { name = f.Name, type = f.Type }),
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
