namespace SmartBase.Core.Exporters;

/// <summary>Ошибка экспорта с дружелюбным текстом — показывается пользователю в баннере диалога.</summary>
public sealed class ExportException(string message) : Exception(message);

/// <summary>Формат файла экспорта. .xlsb/.xls не поддерживаются: свободных библиотек
/// записи двоичных книг для .NET нет (импорт их читает через ExcelDataReader).</summary>
public enum ExportFormat
{
    Xlsx,
    /// <summary>Тот же OpenXML-контейнер, что Xlsx, но macro-enabled (без макросов внутри).</summary>
    Xlsm,
    Csv,
    Json,
}

/// <summary>Коллекция, выбранная для экспорта: идентичность в мульти-маунте
/// плюс имена — для файлов/листов и текстов ошибок.</summary>
public sealed record ExportCollectionRef(int DbId, int CollectionId, string CollectionName, string DbName);

/// <summary>
/// Заявка на экспорт, собранная диалогом. FileName действует, когда файл один (одна коллекция
/// или SingleFile); иначе имена файлов — из имен коллекций. SheetName действует только при одной
/// коллекции. ExcludeColumns — сырая строка формы: имена свойств через запятую, несовпавшие
/// имена молча игнорируются (выбор может охватывать несколько коллекций с разными полями).
/// IncludeHidden=false — скрытые записи (record_display) и столбцы (column_display) не выгружаются.
/// SingleFile — только Excel: все коллекции в один файл, лист на коллекцию.
/// </summary>
public sealed record ExportSpec(
    string Folder,
    ExportFormat Format,
    IReadOnlyList<ExportCollectionRef> Collections,
    string? FileName,
    bool Manual,
    string? SheetName,
    string ExcludeColumns,
    bool NoHeaderRow,
    bool IncludeHidden,
    bool SingleFile);

/// <summary>
/// Колонка экспорта (строит репозиторий по column_order/дефолтному порядку таблицы):
/// Key — «__title__»/«__desc__»/id поля строкой, Header — отображаемое имя,
/// FieldId — id свойства (null у системных), FieldType — тип свойства для
/// типизации значений писателями («text» у системных), ConfigJson — конфиг поля
/// (нужен, например, для формата склейки «Мультисписка»; null у системных).
/// </summary>
public sealed record ExportColumn(string Key, string Header, int? FieldId, string FieldType, string? ConfigJson = null);
