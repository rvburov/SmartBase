using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;

namespace SmartBase.Database.Repositories;

/// <summary>
/// Данные из связанных записей по столбцу целиком — для отбора, сортировки и окна фильтра.
/// Раньше столбец считался страницами по 1000 записей, и каждая страница заново читала цели: на
/// миллионе записей — тысячи запросов и минуты. Теперь один проход по ребрам свойства-связи
/// (индекс (FieldId, FromRecordId, Position), без сортировки), значения различных целей —
/// пакетами по 1000 тем же читателем, что у страницы (<see cref="ReadComputedTargetProperties"/>),
/// по одному подключению на базу за весь расчет, и та же свертка (<see cref="RelationComputedFold"/>).
/// Поэтому значение записи в столбце совпадает со значением на странице и в карточке байт в байт.
///
/// Малый срез (<see cref="SubsetLimit"/>) считается только по своим записям — тем же путем, что
/// страница.
/// </summary>
public partial class ConstructorRepository
{
    /// <summary>До скольких записей срез считается сам, без столбца целиком.</summary>
    internal static int SubsetLimit = 20_000;

    private const int ComputedTargetBatch = 1000;

    /// <summary>Обращения движка к базе (проход по ребрам, пакеты целей) — тест сверяет, что их число
    /// не растет с числом записей. null — не считать.</summary>
    internal static readonly AsyncLocal<StrongBox<int>?> ColumnQueries = new();

    private static void CountColumnQuery()
    {
        if (ColumnQueries.Value is { } counter) counter.Value++;
    }

    /// <summary>Фоновый расчет, в котором идет чтение (отбор, сортировка, окно фильтра): отмена и
    /// строка хода. null — расчет не в фоне (страница, тесты).</summary>
    internal RelationColumnRun? ComputedRun { get; set; }

    private CancellationToken ComputedToken(CancellationToken ct) =>
        ct.CanBeCanceled ? ct : ComputedRun?.Token ?? CancellationToken.None;

    /// <summary>Столбец <paramref name="field"/> по всем записям коллекции.</summary>
    internal RelationComputedColumn ReadRelationComputedColumn(int collectionId, DbField field, CancellationToken ct = default)
    {
        ct = ComputedToken(ct);
        var run = ComputedRun;
        if (field.Id is not int fid || field.CollectionId != collectionId) return RelationComputedColumn.Constant("");
        using var scope = RelationPropertyResolution.Enter(DbPath, fid);
        if (scope is null) return RelationComputedColumn.Constant(RelationProperty.Cycle);
        if (RelationComputedSetup(field) is not var (cfg, relationConfig))
            return RelationComputedColumn.Constant("(вычисление не настроено)");
        var separator = relationConfig.SeparatorString;
        var defaultValue = RelationComputedFold.Fold(field.FieldType, cfg.Aggregate, separator, field.ConfigJson, []).Value;
        run?.Report($"Столбец «{field.Name}»: чтение связей");
        ct.ThrowIfCancellationRequested();

        // Ребра записей коллекции по записи-источнику и порядку ребер; повтор пары цели у записи
        // отбрасывается, как у страницы.
        var sources = new List<int>();
        var starts = new List<int>();
        var edgeDb = new List<int>();
        var edgeTo = new List<int>();
        var wanted = new Dictionary<int, HashSet<int>>();
        var seen = new HashSet<(int, int)>();
        var rows = 0;
        CountColumnQuery();
        foreach (var edge in ComputedColumnEdges(collectionId, cfg.RelationFieldId))
        {
            if ((++rows & 0x3FFF) == 0)
            {
                run?.Report($"Столбец «{field.Name}»: прочитано {sources.Count:N0} записей");
                ct.ThrowIfCancellationRequested();
            }
            if (sources.Count == 0 || sources[^1] != edge.FromRecordId)
            {
                sources.Add(edge.FromRecordId);
                starts.Add(edgeDb.Count);
                // Clear стоит размера таблицы, а не числа элементов: после записи с тысячами ребер
                // каждая следующая чистила бы ее целиком.
                if (seen.Count > 256) seen = []; else seen.Clear();
            }
            if (!seen.Add((edge.TargetDbId, edge.ToRecordId))) continue;
            edgeDb.Add(edge.TargetDbId);
            edgeTo.Add(edge.ToRecordId);
            if (!wanted.TryGetValue(edge.TargetDbId, out var ids)) wanted[edge.TargetDbId] = ids = [];
            ids.Add(edge.ToRecordId);
        }
        starts.Add(edgeDb.Count);
        ct.ThrowIfCancellationRequested();

        var (targets, groupError) = ReadComputedTargets(wanted, cfg, relationConfig, field.Name, ct);
        var valueIndex = new int[sources.Count];
        var values = new List<string>();
        var interned = new Dictionary<string, int>(StringComparer.Ordinal);
        var selected = new List<RelationPropertyValue>();
        for (var i = 0; i < sources.Count; i++)
        {
            if ((i & 0x3FFF) == 0x3FFF)
            {
                run?.Report($"Столбец «{field.Name}»: {i + 1:N0} из {sources.Count:N0} записей", i + 1, sources.Count);
                ct.ThrowIfCancellationRequested();
            }
            string? failed = null;
            selected.Clear();
            for (var e = starts[i]; e < starts[i + 1]; e++)
            {
                if (failed is null && groupError.TryGetValue(edgeDb[e], out var error)) failed = error;
                if (targets.TryGetValue((edgeDb[e], edgeTo[e]), out var value)) selected.Add(value);
            }
            var text = failed ?? RelationComputedFold.Fold(field.FieldType, cfg.Aggregate, separator, field.ConfigJson, selected).Value;
            if (!interned.TryGetValue(text, out var index))
            {
                interned[text] = index = values.Count;
                values.Add(text);
            }
            valueIndex[i] = index;
        }
        return new RelationComputedColumn { Default = defaultValue, Ids = [.. sources], ValueIndex = valueIndex, Values = [.. values] };
    }

    /// <summary>Ребра свойства-связи от записей коллекции в порядке записи-источника и ребер — проход
    /// по индексу (FieldId, FromRecordId, Position) без сортировки (план проверяет тест).</summary>
    internal IQueryable<ComputedEdge> ComputedColumnEdges(int collectionId, int relationFieldId) =>
        from l in db.DbRecordLinks.AsNoTracking()
        join s in db.DbRecords.AsNoTracking() on l.FromRecordId equals s.Id
        where l.FieldId == relationFieldId && s.CollectionId == collectionId
        orderby l.FromRecordId, l.Position, l.Id
        select new ComputedEdge(l.FromRecordId, l.TargetDbId, l.ToRecordId);

    internal readonly record struct ComputedEdge(int FromRecordId, int TargetDbId, int ToRecordId);

    /// <summary>Значения столбца у записей <paramref name="ordered"/>: малый срез — сам, тем же
    /// путем, что страница; больший — из столбца целиком.</summary>
    internal Func<int, string> RelationComputedValuesFor(int collectionId, DbField field, IReadOnlyList<int> ordered,
        CancellationToken ct = default)
    {
        ct = ComputedToken(ct);
        if (ordered.Count > SubsetLimit)
            return ReadRelationComputedColumn(collectionId, field, ct).ValueOf;
        var fid = field.Id!.Value;
        var values = new Dictionary<int, string>(ordered.Count);
        var done = 0;
        foreach (var chunk in ordered.Chunk(ComputedTargetBatch))
        {
            ComputedRun?.Report($"Столбец «{field.Name}»: {done:N0} из {ordered.Count:N0} записей", done, ordered.Count);
            ct.ThrowIfCancellationRequested();
            done += chunk.Length;
            CountColumnQuery();
            var batch = GetRelationComputedValues(chunk, [field]);
            foreach (var id in chunk) values[id] = batch.GetValueOrDefault(id)?.GetValueOrDefault(fid, "") ?? "";
        }
        return id => values.GetValueOrDefault(id, "");
    }

    // Настройка вычисляемого столбца, если она годна: связь своей коллекции, итог подходит к типу.
    private (RelationComputedConfig Config, RelationConfig Relation)? RelationComputedSetup(DbField field)
    {
        var cfg = RelationComputedConfig.FromConfig(field.ConfigJson);
        if (cfg is null) return null;
        var relation = db.DbFields.AsNoTracking().FirstOrDefault(f => f.Id == cfg.RelationFieldId);
        if (relation is null || relation.CollectionId != field.CollectionId || relation.FieldType != "relation"
            || RelationConfig.FromConfig(relation.ConfigJson) is not { } relationConfig
            || field.FieldType == "lookup" && cfg.Aggregate != "lookup"
            || field.FieldType == "rollup" && !RelationComputedConfig.Aggregates.Contains(cfg.Aggregate, StringComparer.Ordinal))
            return null;
        return (cfg, relationConfig);
    }

    // Значения различных целей по базам и отказы баз — по тем же правилам, что у страницы
    // (CalculateRelationComputedValues): старое ребро с числовым ключом свойства не открывает чужую
    // базу, недоступная база и закрытая коллекция — отказ группы.
    private (Dictionary<(int Db, int Record), RelationPropertyValue> Targets, Dictionary<int, string> Errors)
        ReadComputedTargets(Dictionary<int, HashSet<int>> wanted, RelationComputedConfig cfg, RelationConfig relationConfig,
            string column, CancellationToken ct)
    {
        var run = ComputedRun;
        var targets = new Dictionary<(int Db, int Record), RelationPropertyValue>();
        var errors = new Dictionary<int, string>();
        var propertyKey = cfg.Aggregate == "count" ? TitleKey : cfg.TargetField;
        foreach (var (dbId, ids) in wanted)
        {
            if (int.TryParse(propertyKey, out _) && !IsExpectedRelationTargetDatabase(dbId, relationConfig.TargetDbId))
            {
                foreach (var id in ids) targets[(dbId, id)] = new(id, "", "", RelationProperty.MissingField, "", null);
                continue;
            }
            AppDbContext? isolatedDb = null;
            try
            {
                var targetRepo = ComputedTargetRepo(dbId, relationConfig.TargetDbId);
                if (targetRepo is null) { errors[dbId] = UnavailableLabel(dbId); continue; }
                var reader = ComputedTargetReader(dbId, targetRepo, out isolatedDb);
                var done = 0;
                foreach (var chunk in ids.Order().Chunk(ComputedTargetBatch))
                {
                    run?.Report($"Столбец «{column}»: {(dbId == 0 ? "связанные записи" : $"база «{run.BaseName(dbId)}»")}, " +
                                $"{done:N0} из {ids.Count:N0}", done, ids.Count);
                    ct.ThrowIfCancellationRequested();
                    done += chunk.Length;
                    CountColumnQuery();
                    foreach (var value in ReadComputedTargetProperties(reader, relationConfig.TargetCollectionId, propertyKey, chunk))
                        targets[(dbId, value.RecordId)] = value;
                }
            }

            catch (Exception ex) when (ex is System.Data.Common.DbException or System.IO.IOException
                                                or InvalidOperationException or ArgumentException)
            { errors[dbId] = "(вычисление недоступно)"; }
            finally { isolatedDb?.Dispose(); }
        }
        return (targets, errors);
    }

    // База группы целей. Настроенная цель подключается из реестра (RepoFor), другие базы старых
    // ребер — только уже подключенные: фон снимает пути подключенных баз (CrossDbPathsForKeys), и
    // значение записи одно на странице, в карточке и в отборе.
    private ConstructorRepository? ComputedTargetRepo(int dbId, int configuredDbId) =>
        dbId == 0 ? this
        : IsExpectedRelationTargetDatabase(dbId, configuredDbId) ? RelationDbResolver.RepoFor(dbId)
        : RelationDbResolver.MountedRepoFor(dbId);

    // Читатель целей базы. В фоне репозиторий области (ReadScope) уже на своем подключении — второе
    // не открывается. Без области общий контекст чужой базы в фон не уходит: свое подключение,
    // его освобождает вызывающий (owned).
    private static ConstructorRepository ComputedTargetReader(int dbId, ConstructorRepository targetRepo, out AppDbContext? owned)
    {
        owned = null;
        if (dbId == 0 || RelationDbResolver.HasScopedResolver) return targetRepo;
        owned = new AppDbContext(targetRepo.DbPath);
        owned.ApplyPerformancePragmas();
        return new ConstructorRepository(owned);
    }
}

/// <summary>
/// Фоновый расчет данных из связанных записей (отбор, сортировка, окно фильтра): токен отмены и
/// строка в значке фоновых работ — «Данные из связанных записей», под ней столбец и ход, — если
/// расчет идет дольше секунды (решение пользователя 23.10.2026). Расчет идет фазами
/// (<see cref="Phase"/>: отбор, сортировка или список значений по такой колонке): секунда
/// отсчитывается от начала фазы, строка встает по таймеру и без следующего отчета — один долгий
/// запрос к сетевой базе ее не прячет, — а конец фазы строку снимает: долгий обычный запрос после
/// нее (сортировка по другой колонке) строки не дает. Работа без данных из связанных записей
/// отчетов не шлет. Остановки нет: расчет отменяется сам, когда таблица просит новый отбор или
/// сортировку или закрыто окно фильтра. Имена баз снимаются на UI-потоке (<see cref="Capture"/>):
/// путей и строк подключения в строке хода нет.
/// </summary>
internal sealed class RelationColumnRun(CancellationToken ct, IReadOnlyDictionary<int, string> baseNames) : IDisposable
{
    internal static TimeSpan ShowAfter = TimeSpan.FromSeconds(1);
    private const string Title = "Данные из связанных записей";
    private readonly string _id = "relation-column:" + Guid.NewGuid().ToString("N");
    private readonly System.Diagnostics.Stopwatch _phase = new();
    // _lock — состояние; _publish — порядок отчетов значку работ: поздний отчет несет последний ход,
    // а закрытие и конец фазы не обгоняют идущий отчет. Порядок захвата: _publish, затем _lock.
    private readonly object _lock = new();
    private readonly object _publish = new();
    private (string Detail, int Done, int Total) _last;
    private Timer? _timer;
    private bool _active, _shown, _disposed;

    public CancellationToken Token => ct;

    /// <summary>Только с UI-потока: имена подключенных баз по номеру связи.</summary>
    public static RelationColumnRun Capture(CancellationToken ct) => new(ct,
        RelationDbResolver.MountedReposExcept(0).ToDictionary(p => p.DbId, p => RelationDbResolver.DbName(p.DbId) ?? ""));

    public string BaseName(int dbId) => baseNames.GetValueOrDefault(dbId) is { Length: > 0 } name ? name : "другая база";

    /// <summary>Начать фазу расчета первым отчетом; конец фазы — Dispose результата.</summary>
    public IDisposable Phase(string detail)
    {
        Report(detail);
        return new PhaseEnd(this);
    }

    private sealed class PhaseEnd(RelationColumnRun run) : IDisposable
    {
        public void Dispose() => run.Pause();
    }

    public void Report(string detail, int done = 0, int total = 0)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _last = (detail, done, total);
            if (!_active)
            {
                _active = true;
                _phase.Restart();
            }
            if (!_shown)
            {
                var wait = ShowAfter - _phase.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    _timer ??= new Timer(_ => Publish(), null, wait, Timeout.InfiniteTimeSpan);
                    return;
                }
            }
        }
        Publish();
    }

    // Фаза кончилась: несработавший таймер снимается, показанная строка уходит.
    private void Pause()
    {
        lock (_publish)
        {
            bool shown;
            lock (_lock)
            {
                if (_disposed || !_active) return;
                _active = false;
                _timer?.Dispose();
                _timer = null;
                shown = _shown;
                _shown = false;
            }
            if (shown) Core.BackgroundJobs.Finish(_id);
        }
    }

    private void Publish()
    {
        lock (_publish)
        {
            (string Detail, int Done, int Total) last;
            lock (_lock)
            {
                if (_disposed || !_active) return;
                _shown = true;
                last = _last;
            }
            Core.BackgroundJobs.Report(_id, Title, last.Detail, last.Done, last.Total, icon: "IconFieldRelation");
        }
    }

    public void Dispose()
    {
        lock (_publish)
        {
            bool shown;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _timer?.Dispose();
                _timer = null;
                shown = _shown;
            }
            if (shown) Core.BackgroundJobs.Finish(_id);
        }
    }
}
