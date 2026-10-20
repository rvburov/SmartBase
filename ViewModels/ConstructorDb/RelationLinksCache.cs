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
    // Предохранитель от роста на огромных таблицах: кеш страниц таблицы держит от 11 до 50
    // страниц по 200 строк (предел считается от числа колонок, см. VirtualRowList), здесь даем
    // запас под несколько колонок-связей. Переполнение → чистим целиком (ячейки просто упадут
    // на GetLinks — корректность не страдает).
    private const int MaxEntries = 20000;

    private readonly Dictionary<(int Field, int Record), List<(int Id, string Title)>> _map = new();
    private readonly Dictionary<(int Field, int Record), List<(SmartBase.Core.Models.RelationTarget Target, string Display)>> _targets = new();

    public bool TryGetTargets(int fieldId, int recordId,
        out List<(SmartBase.Core.Models.RelationTarget Target, string Display)> links) =>
        _targets.TryGetValue((fieldId, recordId), out links!);

    public void PrimeTargets(int fieldId, IReadOnlyList<int> recordIds,
        IReadOnlyDictionary<int, List<(SmartBase.Core.Models.RelationTarget Target, string Display)>> batch)
    {
        if (_targets.Count > MaxEntries) _targets.Clear();
        foreach (var rid in recordIds)
            _targets[(fieldId, rid)] = batch.TryGetValue(rid, out var links) ? links : [];
    }

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

    public void Invalidate(int fieldId, int recordId)
    {
        _map.Remove((fieldId, recordId));
        _targets.Remove((fieldId, recordId));
    }

    public void Clear() { _map.Clear(); _targets.Clear(); }
}
