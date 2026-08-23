namespace SmartBase.Core.Importers.TableSources;

/// <summary>
/// Источник табличных данных для импорта (Excel/CSV/JSON). Двухпроходный: <see cref="Analyze"/>
/// читает источник целиком (валидация, схема, точные счетчики — строки в память НЕ складываются),
/// затем <see cref="ReadRows"/> отдает записи потоком, переоткрывая файл. Так схема (набор полей)
/// известна ДО создания коллекции, файл валидируется до первой записи в БД, а память остается
/// O(1) на любом объеме. Все ошибки — <see cref="ImportException"/> с дружелюбным текстом.
/// </summary>
public interface ITableSource
{
    /// <summary>Проход 1: схема и счетчики. <paramref name="progress"/> — доля 0..1 (по байтам файла).</summary>
    ImportSchema Analyze(IProgress<double>? progress, CancellationToken ct);

    /// <summary>Проход 2: записи в порядке источника. Title сырой (может быть пустым — нумерацию
    /// «Без названия» делает <see cref="SourceHelpers.WithTitles"/>); FieldValues — в порядке
    /// <see cref="ImportSchema.FieldNames"/>.</summary>
    IEnumerable<ImportRow> ReadRows(CancellationToken ct);
}
