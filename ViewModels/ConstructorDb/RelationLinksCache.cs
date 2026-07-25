namespace SmartBase.ViewModels.ConstructorDb;

/// <summary>
/// Табличный кеш связей-relation на время жизни таблицы: батч-префетч ВИДИМОЙ СТРАНИЦЫ одним
/// <c>GetLinksBatch</c> вместо N точечных <c>GetLinks</c> по каждой строке (проблема N+1 — заметна
/// на больших таблицах и PG по сети). Ключ — (поле-связь, запись-источник). Хранит и ПУСТЫЕ наборы
/// (чтобы «связей нет» не перезапрашивалось).
///
/// Пишет <see cref="VirtualRowList"/> при наполнении страницы, читают ячейки (RelationSelectBox) при
/// установке RecordId. Правка ячейки инвалидирует свою запись (следующее чтение — свежее из БД);
/// повторный показ ячейки (AttachedToVisualTree после выхода из правки) читает мимо кеша. Только
/// UI-поток — без блокировок.
/// </summary>
public sealed class RelationLinksCache
{
    // Предохранитель от роста на огромных таблицах: кеш страниц держит ≤ MaxPages×PageSize строк,
    // здесь даём запас под несколько колонок-связей. Переполнение → чистим целиком (ячейки просто
    // упадут на GetLinks — корректность не страдает).
    private const int MaxEntries = 20000;

    private readonly Dictionary<(int Field, int Record), List<(int Id, string Title)>> _map = new();

    public bool TryGet(int fieldId, int recordId, out List<(int Id, string Title)> links)
        => _map.TryGetValue((fieldId, recordId), out links!);

    /// <summary>Сохранить результат батча по набору строк — включая отсутствующие (нет ребра → пустой список).</summary>
    public void Prime(int fieldId, IReadOnlyList<int> recordIds,
                      IReadOnlyDictionary<int, List<(int Id, string Title)>> batch)
    {
        if (_map.Count > MaxEntries) _map.Clear();
        foreach (var rid in recordIds)
            _map[(fieldId, rid)] = batch.TryGetValue(rid, out var l) ? l : [];
    }

    public void Invalidate(int fieldId, int recordId) => _map.Remove((fieldId, recordId));

    public void Clear() => _map.Clear();
}
