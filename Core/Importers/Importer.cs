namespace SmartBase.Core.Importers;

/// <summary>Ошибка импорта с дружелюбным текстом — показывается пользователю в баннере диалога.</summary>
public sealed class ImportException(string message) : Exception(message);

/// <summary>Одна запись: название и значения свойств (в порядке FieldNames схемы).</summary>
public sealed record ImportRow(string Title, IReadOnlyList<string> FieldValues);

/// <summary>
/// Итог первого прохода по источнику (см. <see cref="TableSources.ITableSource.Analyze"/>):
/// подпись колонки «Название», имена свойств, точное число записей (для процентов фазы записи)
/// и число записей с пустым названием — для нумерации «Без названия N» (правило: одна пустая →
/// «Без названия», несколько → «Без названия 1», «Без названия 2» …).
/// </summary>
public sealed record ImportSchema(
    string? TitleHeader,
    IReadOnlyList<string> FieldNames,
    long RowCount,
    long EmptyTitleCount);

/// <summary>Раскладка листов Excel по коллекциям (чекбоксы ручного режима).</summary>
public enum SheetMode
{
    /// <summary>Один лист: стандартно — первый; в ручном режиме можно указать имя.</summary>
    Single,

    /// <summary>Все листы книги — в одну коллекцию (столбцы сопоставляются по заголовкам).</summary>
    MergeAll,

    /// <summary>Каждый лист книги — своя коллекция («{имя} — {лист}»).</summary>
    SplitAll,
}

/// <summary>
/// Заявка на импорт, собранная диалогом. Для стандартного типа HeaderRow=1, DataRow=2,
/// TitleColumn="1", Sheets=Single, исключения пустые. Manual-параметры (лист/строки/столбец,
/// исключения листов/строк/столбцов) применяются только к Excel-файлам; CSV/JSON всегда
/// импортируются по стандартным правилам. В FolderMode Path — папка, BaseName не используется
/// (названия коллекций берутся из имен файлов/листов). Исключения — сырые строки формы
/// (парсятся планировщиком): листы — названия через запятую, строки — номера/диапазоны
/// («2, 5-7»), столбцы — номера/буквы/диапазоны («B, D-F, 8»).
/// </summary>
public sealed record ImportSpec(
    string Path,
    bool FolderMode,
    bool Manual,
    string? SheetName,
    int HeaderRow,
    int DataRow,
    string TitleColumn,
    SheetMode Sheets,
    string BaseName,
    string? ExcludeSheets = null,
    string? ExcludeRows = null,
    string? ExcludeColumns = null);
