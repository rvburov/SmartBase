namespace SmartBase.Integrations;

/// <summary>
/// Заготовка карточки запроса: что заполнить и зачем.
/// </summary>
/// <param name="Title">Название в списке выбора.</param>
/// <param name="Hint">Одна фраза о том, когда это берут. Без нее список — четыре загадки.</param>
/// <param name="Request">Заполненная карточка; соединение и имя человек назовет сам.</param>
public sealed record RequestPreset(string Title, string Hint, IntegrationRequest Request);

/// <summary>
/// Готовые заготовки запросов (docs/ROADMAP-API.md, раздел 5: «простой режим»).
///
/// <b>Пресет не создает «другой» запрос.</b> Это мастер, заполняющий тот же самый объект
/// «Запрос»: после него открывается обычная карточка со всеми полями, и человек видит, что
/// именно ему заполнили. Обратного пути (эксперт → пресет) нет и не нужно.
///
/// Заготовок намеренно мало и все они РОДОВЫЕ — «список по ссылке», «отправить уведомление».
/// Карточек под конкретные сторонние системы здесь нет и не будет: названия меняются, а подпись в
/// интерфейсе читается как обещание поддержки, которого никто не давал. Для настройки под
/// конкретную систему есть путь сильнее любого пресета — вставка строки cURL из ее документации.
/// </summary>
public static class RequestPresets
{
    /// <summary>Заголовок «принимаю JSON» — его ждет почти каждый чужой API.</summary>
    private static readonly IntegrationHeader AcceptsJson = new("Accept", "application/json");

    /// <summary>Заголовок «посылаю JSON» — у всего, что несет тело.</summary>
    private static readonly IntegrationHeader SendsJson = new("Content-Type", "application/json");

    public static IReadOnlyList<RequestPreset> All { get; } =
    [
        new("Забрать список",
            "Сторонняя программа отдает записи одним ответом. Самый частый случай: справочник, "
            + "остатки, список заказов за сутки.",
            IntegrationRequest.New() with
            {
                Name    = "Забрать список",
                Method  = RequestMethod.Get,
                Path    = "/",
                Headers = [AcceptsJson],
            }),

        new("Забрать список постранично",
            "То же, но записей больше, чем отдают за раз. Без разбивки выгрузка молча "
            + "обрывается на первой сотне — самая частая и самая незаметная беда обмена.",
            IntegrationRequest.New() with
            {
                Name        = "Забрать список постранично",
                Method      = RequestMethod.Get,
                Path        = "/",
                Headers     = [AcceptsJson],
                Paging      = RequestPaging.Page,
                PagingField = "page",
                ItemsPath   = "data",
            }),

        new("Забрать изменения с прошлого раза",
            "Инкрементальная выгрузка: вместо всего списка каждый час — только то, что "
            + "изменилось. На медленной сети это решает все.",
            IntegrationRequest.New() with
            {
                Name    = "Забрать изменения",
                Method  = RequestMethod.Get,
                Path    = "/?since={{последний_запуск}}",
                Headers = [AcceptsJson],
            }),

        new("Отправить уведомление",
            "Короткое сообщение в стороннюю программу: «заказ принят», «документ зарегистрирован». "
            + "Тело набирается руками, с подстановками.",
            IntegrationRequest.New() with
            {
                Name    = "Отправить уведомление",
                Method  = RequestMethod.Post,
                Path    = "/",
                Headers = [SendsJson],
                Body    = "{ \"text\": \"Готово на {{сегодня}}\" }",
            }),

        new("Отправить записи коллекции",
            "Тело собирает исходящая привязка — список полей, которые уходят наружу; "
            + "задается он кнопкой «Что уносит» на карточке. Наберите его руками — и уйдет "
            + "напечатанное, а не записи.",
            IntegrationRequest.New() with
            {
                Name    = "Отправить записи",
                Method  = RequestMethod.Post,
                Path    = "/",
                Headers = [SendsJson],
            }),
    ];
}
