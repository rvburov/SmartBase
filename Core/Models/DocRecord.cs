namespace SmartBase.Core.Models;

/// <summary>
/// Что известно про текст документа — им подписан серый чип карточки и считаются числа
/// в подвале блока поиска.
///
/// Состояние приходит из кеша <c>docs_index.db</c> и ставится контроллером раздела ПОСЛЕ того,
/// как страница ленты прочитана: сам запрос в базу про кеш ничего не знает, а спрашивать кеш
/// на каждую карточку отдельно значило бы сотни запросов на экран.
/// </summary>
public enum DocTextState
{
    /// <summary>Кеш еще не спрашивали — чипа нет.</summary>
    Unknown = 0,
    /// <summary>Документ не разобран: внутри него поиск пока не ищет, но разобрать можно.</summary>
    NotRead,
    /// <summary>Текст прочитан — поиск по содержимому работает.</summary>
    Read,
    /// <summary>Разобран, но текста нет: скан без текстового слоя. Перечитывание не поможет.</summary>
    NoText,
    /// <summary>Формат без извлечения текста — ищется только по названию.</summary>
    Unsupported,
    /// <summary>Прочитать не удалось: файл битый, занят или тяжелее порога.</summary>
    Failed,
}

/// <summary>
/// Место документа в порядке ленты — ключ keyset-курсора: позиция и id записи, затем позиция
/// и id файлового столбца (разные столбцы одной записи идут подряд). По нему следующая
/// страница читается «после этого документа» за время, не зависящее от глубины, а не
/// смещением, цена которого растет с каждой пропущенной строкой.
/// </summary>
public readonly record struct DocCursor(int RecordPosition, int RecordId, int FieldPosition, int FieldId);

/// <summary>
/// Документ раздела «Библиотека документов» — пара «запись × file-поле с ролью
/// <c>IsDocFile</c>». Одна запись дает столько документов, сколько в коллекции включенных
/// файловых столбцов: роль не эксклюзивна ровно так же, как календарная дата, поэтому
/// документ опознается парой (<see cref="Id"/>, <see cref="FileFieldId"/>), а не одним id.
/// </summary>
public class DocRecord
{
    public int    DbId         { get; set; }   // владелец-БД (мульти-маунт); 0 = по умолчанию
    public string DbName       { get; set; } = "";
    public int?   Id           { get; set; }
    /// <summary>File-поле (<c>IsDocFile</c>), из которого получен документ.</summary>
    public int    FileFieldId  { get; set; }
    /// <summary>Подпись файлового столбца — его видно в карточке, когда столбцов несколько.</summary>
    public string FileFieldName { get; set; } = "";
    public string Title        { get; set; } = "";
    /// <summary>Абсолютный путь к документу — как его хранит тип «Файл» (строка в значении).</summary>
    public string Path         { get; set; } = "";
    /// <summary>Значение поля с ролью «Тип документа»; пусто — тип не проставлен.</summary>
    public string DocType      { get; set; } = "";
    public int?   CollectionId { get; set; }
    public string CollectionName { get; set; } = "";
    public string Color        { get; set; } = "#007AFF";

    /// <summary>
    /// Фрагмент найденного внутри документа текста с маркерами подсветки — заполняется только
    /// поиском по содержимому (<c>DocTextIndex</c>), в обычной ленте пуст.
    /// </summary>
    public string Snippet      { get; set; } = "";

    /// <summary>Что известно про текст документа; ставит контроллер раздела из кеша.</summary>
    public DocTextState TextState { get; set; } = DocTextState.Unknown;

    /// <summary>
    /// Размер файла в байтах; отрицательное — файла нет на диске. Тот же вопрос к диску, которым
    /// определяется плашка «файл не найден», отдает и размер, поэтому он достается бесплатно.
    /// Правда только при <see cref="OnDiskKnown"/>.
    /// </summary>
    public long SizeBytes { get; set; } = -1;

    /// <summary>Время правки файла (тики UTC) — ключ готовой миниатюры в кеше.</summary>
    public long Mtime { get; set; }

    /// <summary>
    /// Файл уже спрошен у диска, и <see cref="SizeBytes"/> с <see cref="Mtime"/> — правда.
    /// Спрашивает фон, вместе со страницей ленты (<c>DocStamps.Probe</c>): карточка строится в
    /// UI-потоке, а файл лежит, бывает, на сервере, и ждать его ответа там нельзя. Карточка,
    /// собранная без этого, спросит диск сама — медленно, но честно.
    /// </summary>
    public bool OnDiskKnown { get; set; }

    /// <summary>
    /// Место документа в порядке ленты; есть только у документов, прочитанных страницей ленты
    /// (<c>GetDocPage</c>), — поиск его не заполняет.
    /// </summary>
    public DocCursor Cursor { get; set; }

    /// <summary>
    /// Число страниц из кеша; 0 — неизвестно. Считается при разборе документа (мы его в этот
    /// момент и так открыли), поэтому есть только у прочитанных.
    /// </summary>
    public int Pages { get; set; }

    /// <summary>Для показа — первая непустая строка названия; пустое название — «Без названия».</summary>
    public string DisplayTitle => TitleText.FirstLine(Title) is { Length: > 0 } line
                                      ? line : CalendarRecord.UntitledPlaceholder;

    /// <summary>Имя файла без пути — подпись под названием и запасной заголовок карточки.</summary>
    public string FileName => FileValue.DisplayName(Path);

    /// <summary>Расширение без точки в верхнем регистре («PDF») — бейдж вместо миниатюры.</summary>
    public string Format
    {
        get
        {
            var name = FileName;
            var dot  = name.LastIndexOf('.');
            return dot > 0 && dot < name.Length - 1
                ? name[(dot + 1)..].ToUpperInvariant()
                : "";
        }
    }
}
