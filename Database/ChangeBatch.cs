namespace SmartBase.Database;

/// <summary>
/// Копилка своих коммитов между двумя обновлениями экрана. Коммиты приходят пачками — одна
/// задача это запись плюс несколько значений, правка в панели записи сохраняется на каждую
/// букву, массовая операция коммитит порциями, — а обновлять разделы нужно один раз на
/// пачку. Здесь события складываются по базе: какие типы объектов затронуты и момент
/// последнего коммита; главное окно опустошает копилку по таймеру и обновляет то, что читало
/// данные раньше этого момента.
///
/// Потокобезопасна: <see cref="Add"/> зовется с потока записи (UI или фонового),
/// <see cref="Drain"/> — с UI-потока.
/// </summary>
public sealed class ChangeBatch
{
    /// <summary>Итог по одной базе: путь, затронутые типы, момент последнего коммита.</summary>
    /// <param name="Structural">Среди изменений есть группы/коллекции/поля — деревья сайдбаров и
    /// колонки таблицы перестраиваются; иначе достаточно обновить строки и события.</param>
    public sealed record Entry(string DbPath, IReadOnlySet<string> Types, long Stamp, bool Structural);

    private sealed class Bucket
    {
        public readonly HashSet<string> Types = [];
        public long Stamp;
    }

    private readonly Dictionary<string, Bucket> _byDb = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Есть ли что выдать (без опустошения).</summary>
    public bool IsEmpty
    {
        get { lock (_gate) return _byDb.Count == 0; }
    }

    /// <summary>
    /// Сложить коммит. Отдает <c>true</c>, если до него копилка была пуста, — вызывающему пора
    /// заводить таймер обновления; последующие коммиты лишь пополняют пачку.
    /// </summary>
    public bool Add(string dbPath, IEnumerable<ChangeBus.Change> changes, long stamp)
    {
        lock (_gate)
        {
            var wasEmpty = _byDb.Count == 0;
            if (!_byDb.TryGetValue(dbPath, out var bucket))
                _byDb[dbPath] = bucket = new Bucket();

            foreach (var c in changes) bucket.Types.Add(c.TargetType);
            if (stamp > bucket.Stamp) bucket.Stamp = stamp;
            return wasEmpty;
        }
    }

    /// <summary>Забрать накопленное и опустошить копилку.</summary>
    public List<Entry> Drain()
    {
        lock (_gate)
        {
            var list = new List<Entry>(_byDb.Count);
            foreach (var (path, b) in _byDb)
                list.Add(new Entry(path, b.Types, b.Stamp, b.Types.Any(ChangeBus.IsStructural)));
            _byDb.Clear();
            return list;
        }
    }
}
