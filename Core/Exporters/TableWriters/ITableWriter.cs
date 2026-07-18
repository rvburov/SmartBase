namespace SmartBase.Core.Exporters.TableWriters;

/// <summary>
/// Писатель одного файла экспорта. Excel поддерживает несколько таблиц (лист на таблицу),
/// CSV/JSON — ровно одну. Переполнение листа Excel писатель обрабатывает сам: продолжение
/// на листах «Имя 2», «Имя 3»… с повтором заголовков. Память O(1): SAX/поток, без DOM и
/// общих строк. Файловые исключения летят наружу сырыми — их переводит оркестрация через
/// <see cref="ExportErrors.Map"/>; недописанный файл удаляет тоже оркестрация (после Dispose).
/// Порядок вызовов: (BeginTable → WriteRow* → EndTable)+ → Finish → Dispose.
/// </summary>
public interface ITableWriter : IDisposable
{
    /// <summary>Начать таблицу (лист Excel). writeHeader=false — без строки заголовков.</summary>
    void BeginTable(string name, IReadOnlyList<ExportColumn> columns, bool writeHeader);

    /// <summary>Строка данных: сырые значения БД в порядке колонок BeginTable.</summary>
    void WriteRow(IReadOnlyList<string> values);

    void EndTable();

    /// <summary>Финализировать файл (workbook.xml, закрытие массива JSON). После — только Dispose.</summary>
    void Finish();
}
