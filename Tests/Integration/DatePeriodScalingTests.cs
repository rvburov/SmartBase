using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SmartBase.Core.Exporters;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>Размер коллекции не превращает чтение окна в расчет всей таблицы,
/// а массовый фильтр и счетчики не читают источник отдельными порциями по 200 записей.</summary>
public sealed class DatePeriodScalingTests
{
    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public void Reading_a_period_window_returns_only_requested_records_and_columns(string profile)
    {
        using var f = new Fixture(profile);
        var ids = new[] { f.RecordIds[0], f.RecordIds[1199], f.RecordIds[^1] };
        var values = f.Repo.GetFieldValuesBatch(ids, [f.Automatic]);
        Assert.Equal(ids.Order(), values.Keys.Order());
        foreach (var id in ids)
        {
            Assert.Equal([f.Automatic], values[id].Keys);
            Assert.Equal(f.Expected(id), values[id][f.Automatic]);
        }
        Assert.False(f.Db.Manager.Context.DbFieldValues.AsNoTracking().Any(value => value.FieldId == f.Automatic));

        // Скрытая расчетная колонка не должна запускать даже получение часов.
        f.Repo.DatePeriodClock = () => throw new InvalidOperationException("Расчет вне окна колонок");
        var textOnly = f.Repo.GetFieldValuesBatch(ids, [f.Text]);
        Assert.Equal(ids.Order(), textOnly.Keys.Order());
        Assert.All(textOnly.Values, row => Assert.Equal([f.Text], row.Keys));
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public void Fixed_period_read_uses_the_snapshot_without_asking_for_the_current_time(string profile)
    {
        using var f = new Fixture(profile);
        var ids = new[] { f.RecordIds[1000], f.RecordIds[1001] };
        // После фиксации исходные даты могут исчезнуть или измениться. Чтение снимка
        // не должно превращаться в повторный расчет по этим датам или по часам.
        f.Db.Manager.Context.Database.ExecuteSqlInterpolated($@"UPDATE db_field_values
            SET value = 'не дата' WHERE fieldid = {f.Start} OR fieldid = {f.End}");
        f.Repo.DatePeriodClock = () => throw new InvalidOperationException("Часы не нужны для снимка");
        using var commands = new Commands(f.Db.Path);
        var values = f.Repo.GetFieldValuesBatch(ids, [f.Fixed]);
        Assert.Equal(ids.Order(), values.Keys.Order());
        foreach (var id in ids)
            Assert.Equal(f.PositionOf(id) % 2 == 0 ? "1800" : "", values[id][f.Fixed]);
        commands.AssertNoAdditionalValueReads();
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public async Task Page_and_horizontal_top_up_use_one_callers_clock_snapshot(string profile)
    {
        using var f = new Fixture(profile);
        var original = new DateTime(2026, 10, 28, 9, 23, 47);
        var clock = original;
        var calls = 0;
        f.Repo.DatePeriodClock = () => { Interlocked.Increment(ref calls); return clock; };
        var work = f.Repo.LoadPageAsync(f.Collection,
            repo => new ConstructorRepository.RecordPage(repo.GetRecordsPage(f.Collection, 0, 3)), [f.Current]);
        clock = original.AddHours(10);
        var page = await work;
        Assert.Equal(1, calls);
        Assert.Equal(3, page.Page.Records.Count);
        foreach (var record in page.Page.Records)
            Assert.Equal(f.PositionOf(record.Id!.Value) % 3 == 0 ? "" : "1380",
                page.Values[record.Id.Value][f.Current]);

        clock = original.AddMinutes(2);
        var ids = page.Page.Records.Select(record => record.Id!.Value).ToArray();
        var topUpWork = f.Repo.LoadValuesAsync(ids, [f.Current]);
        clock = original.AddDays(5);
        var topUp = await topUpWork;
        Assert.Equal(2, calls);
        foreach (var id in ids)
            Assert.Equal(f.PositionOf(id) % 3 == 0 ? "" : "1500", topUp.Values[id][f.Current]);
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public void Export_keeps_one_current_time_snapshot_across_page_boundaries(string profile)
    {
        using var f = new Fixture(profile, count: 9);
        var clock = new DateTime(2026, 10, 28, 9, 23, 47);
        var calls = 0;
        f.Repo.DatePeriodClock = () => { calls++; return clock; };
        var columns = new[] { new ExportColumn(f.Current.ToString(), "Период", f.Current, "date_period") };
        using var rows = f.Repo.ReadExportRowsIsolated(f.Collection, columns, false, default, pageSize: 3).GetEnumerator();
        var count = 0;
        while (rows.MoveNext())
        {
            var position = ++count;
            Assert.Equal([position % 3 == 0 ? "" : "1380"], rows.Current);
            // Первая страница уже прочитана. Следующие страницы должны пользоваться
            // тем же моментом, даже если реальная длительность успела измениться.
            if (count == 1) clock = clock.AddHours(10);
        }
        Assert.Equal(9, count);
        Assert.Equal(1, calls);
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public void Exporting_title_and_text_does_not_read_hidden_period_dependencies(string profile)
    {
        using var f = new Fixture(profile, count: 9);
        var calls = 0;
        f.Repo.DatePeriodClock = () => { calls++; return new DateTime(2026, 10, 28, 9, 23, 47); };
        var columns = new[]
        {
            new ExportColumn("__title__", "Название", null, "text"),
            new ExportColumn(f.Text.ToString(), "Группа", f.Text, "text"),
        };
        using var commands = new Commands(f.Db.Path);
        var rows = f.Repo.ReadExportRowsIsolated(f.Collection, columns, false, default, pageSize: 3).ToArray();
        Assert.Equal(9, rows.Length);
        for (var index = 0; index < rows.Length; index++)
        {
            var position = index + 1;
            Assert.Equal(["Запись " + position, position % 2 == 0 ? "even" : "odd"], rows[index]);
        }
        Assert.Equal(1, calls);
        commands.AssertNoAdditionalValueReads();
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public async Task Period_counts_and_value_list_read_a_column_without_per_page_source_queries(string profile)
    {
        using var f = new Fixture(profile);
        var expected = new Dictionary<string, int> { ["7200"] = 800, ["10800"] = 800, [""] = 800 };
        using (var commands = new Commands(f.Db.Path))
        {
            var counts = await f.Repo.GetColumnCountsAsync(f.Collection, f.Key);
            AssertCounts(expected, counts);
            commands.AssertBounded();
        }
        using (var commands = new Commands(f.Db.Path))
        {
            var listed = await f.Repo.GetColumnValueListAsync(f.Collection, f.Key);
            AssertCounts(expected, listed.Values);
            commands.AssertBounded();
        }
        using (var commands = new Commands(f.Db.Path))
        {
            var filters = new Dictionary<string, HashSet<string>> { [f.Text.ToString()] = ["even"] };
            var cascade = await f.Repo.GetColumnCountsAsync(f.Collection, f.Key, filters);
            AssertCounts(new() { ["7200"] = 400, ["10800"] = 400, [""] = 400 }, cascade);
            commands.AssertBounded();
        }
        using (var commands = new Commands(f.Db.Path))
        {
            var snapshots = await f.Repo.GetColumnCountsAsync(f.Collection, f.Fixed.ToString());
            AssertCounts(new() { ["1800"] = 1200, [""] = 1200 }, snapshots);
            commands.AssertBounded();
        }
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public async Task Period_sort_and_include_exclude_filters_agree_with_the_whole_collection(string profile)
    {
        using var f = new Fixture(profile);
        using (var commands = new Commands(f.Db.Path))
        {
            var sorted = await f.Repo.GetFilteredRecordIdsAsync(f.Collection, new Dictionary<string, HashSet<string>>(),
                false, f.Key, true, "date_period", default);
            var expected = f.RecordIds.Where(id => f.Expected(id) == "7200")
                .Concat(f.RecordIds.Where(id => f.Expected(id) == "10800"))
                .Concat(f.RecordIds.Where(id => f.Expected(id) == ""));
            Assert.Equal(expected, sorted);
            commands.AssertBounded();
        }
        using (var commands = new Commands(f.Db.Path))
        {
            var filtered = f.Repo.GetFilteredRecordIds(f.Collection,
                new Dictionary<string, HashSet<string>> { [f.Key] = ["7200", ""] });
            Assert.Equal(f.RecordIds.Where(id => f.Expected(id) != "10800"), filtered);
            commands.AssertBounded();
        }
        using (var commands = new Commands(f.Db.Path))
        {
            var excluded = f.Repo.GetFilteredRecordIds(f.Collection,
                new Dictionary<string, HashSet<string>> { [f.Key] = FilterExclusion.Exclude(["7200", ""]) });
            Assert.Equal(f.RecordIds.Where(id => f.Expected(id) == "10800"), excluded);
            commands.AssertBounded();
        }
        using (var commands = new Commands(f.Db.Path))
        {
            var all = f.Repo.GetFilteredRecordIds(f.Collection,
                new Dictionary<string, HashSet<string>> { [f.Key] = ["7200", "10800", ""] });
            Assert.Equal(f.RecordIds, all);
            commands.AssertBounded();
        }
        using (var commands = new Commands(f.Db.Path))
        {
            var none = f.Repo.GetFilteredRecordIds(f.Collection,
                new Dictionary<string, HashSet<string>> { [f.Key] = ["1"] });
            Assert.Empty(none);
            commands.AssertBounded();
        }
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public async Task Fixed_period_sql_sort_counts_and_filters_use_only_preserved_snapshots(string profile)
    {
        using var f = new Fixture(profile);
        var high = f.RecordIds[3];
        var missing = f.RecordIds[0];
        var key = f.Fixed.ToString();
        var context = f.Db.Manager.Context;
        context.Database.ExecuteSqlInterpolated($@"UPDATE db_field_values SET value='5400'
            WHERE recordid={high} AND fieldid={f.Fixed}");
        context.Database.ExecuteSqlInterpolated($@"DELETE FROM db_field_values
            WHERE recordid={missing} AND fieldid={f.Fixed}");
        // Снимок остается пригоден для сортировки и отбора, даже если даты после
        // фиксации стали непригодны для нового вычисления.
        context.Database.ExecuteSqlInterpolated($@"UPDATE db_field_values SET value='не дата'
            WHERE fieldid={f.Start} OR fieldid={f.End}");
        var lowIds = f.RecordIds.Where(id => f.PositionOf(id) % 2 == 0 && id != high).ToArray();
        var emptyIds = f.RecordIds.Where(id => f.PositionOf(id) % 2 != 0).ToArray();
        foreach (var asc in new[] { true, false })
        {
            using var commands = new Commands(f.Db.Path);
            var sorted = await f.Repo.GetFilteredRecordIdsAsync(f.Collection, new Dictionary<string, HashSet<string>>(),
                false, key, asc, "date_period", default);
            var nonempty = asc ? lowIds.Append(high) : new[] { high }.Concat(lowIds);
            Assert.Equal(nonempty.Concat(emptyIds), sorted);
            commands.AssertBounded();
        }

        // Синхронные операции не имеют общего async-снимка часов. Ни расчет
        // текущего времени, ни подмена сохраненного результата здесь не нужны.
        f.Repo.DatePeriodClock = () => throw new InvalidOperationException("Фильтр снимка не использует часы");
        using (var commands = new Commands(f.Db.Path))
        {
            AssertCounts(new() { ["1800"] = 1199, ["5400"] = 1, [""] = 1200 },
                f.Repo.GetColumnCounts(f.Collection, key));
            commands.AssertBounded();
        }
        var selections = new[]
        {
            new HashSet<string> { "1800" },
            new HashSet<string> { "5400" },
            new HashSet<string> { "" },
            new HashSet<string> { "1800", "" },
            FilterExclusion.Exclude(["1800"]),
            FilterExclusion.Exclude([""]),
            FilterExclusion.Exclude(["1800", ""]),
        };
        string Snapshot(int id) => id == high ? "5400" : f.PositionOf(id) % 2 == 0 ? "1800" : "";
        foreach (var selection in selections)
        {
            var excluded = FilterExclusion.IsExclude(selection);
            var allowed = excluded ? FilterExclusion.Values(selection).ToHashSet() : selection;
            var expected = f.RecordIds.Where(id => allowed.Contains(Snapshot(id)) != excluded);
            using var commands = new Commands(f.Db.Path);
            var filtered = f.Repo.GetFilteredRecordIds(f.Collection,
                new Dictionary<string, HashSet<string>> { [key] = selection });
            Assert.Equal(expected, filtered);
            commands.AssertBounded();
        }
    }

    private static void AssertCounts(Dictionary<string, int> expected, IEnumerable<(string Value, int Count)> actual)
    {
        var map = actual.ToDictionary(value => value.Value, value => value.Count);
        Assert.Equal(expected.Count, map.Count);
        foreach (var (value, count) in expected) Assert.Equal(count, map[value]);
    }

    private sealed class Fixture : IDisposable
    {
        // EF кеширует решение о диагностике на контексте. Подписка должна появиться
        // до создания основного контекста, а не только перед отдельной командой.
        // Этот слушатель лишь включает события; scoped Commands ниже собирают замер.
        private readonly Commands _enableCommandEvents = new(null);
        internal readonly MultiUserTestDatabase Db;
        internal ConstructorRepository Repo => Db.Repo;
        internal readonly int Collection, Start, End, Text, Automatic, Fixed, Current;
        internal readonly int[] RecordIds;
        private readonly Dictionary<int, int> _positions;
        internal string Key => Automatic.ToString();

        internal Fixture(string profile, int count = 2400)
        {
            try
            {
                Db = new MultiUserTestDatabase(profile);
                var context = Db.Manager.Context;
                Collection = SampleData.AddCollection(Repo, "Окно периодов").Id!.Value;
                Start = SampleData.AddField(Repo, Collection, "Начало", "date").Id!.Value;
                End = SampleData.AddField(Repo, Collection, "Конец", "date").Id!.Value;
                Text = SampleData.AddField(Repo, Collection, "Группа").Id!.Value;
                var records = Enumerable.Range(1, count).Select(position => new DbRecordEntity
                    { CollectionId = Collection, Position = position, Title = "Запись " + position }).ToArray();
                context.DbRecords.AddRange(records);
                context.SaveChanges();
                RecordIds = records.Select(record => record.Id).ToArray();
                _positions = records.ToDictionary(record => record.Id, record => record.Position);
                // Свойства добавляются к уже существующим записям: их появление не запускает
                // расчет всей коллекции. Снимки ниже моделируют ранее сохраненное состояние.
                Automatic = Period("Автоматический", new(Start, End));
                Fixed = Period("Зафиксированный", new(Start, End, Automatic: false));
                Current = Period("До текущего времени", new(Start));
                context.Database.ExecuteSqlInterpolated($@"INSERT INTO db_field_values (recordid,fieldid,value)
                    SELECT id,{Start},'2026-10-28T09:00' FROM db_records
                    WHERE collectionid={Collection} AND position % 3 <> 0");
                context.Database.ExecuteSqlInterpolated($@"INSERT INTO db_field_values (recordid,fieldid,value)
                    SELECT id,{End},CASE WHEN position % 3 = 1 THEN '2026-10-28T11:00' ELSE '2026-10-28T12:00' END
                    FROM db_records WHERE collectionid={Collection}");
                context.Database.ExecuteSqlInterpolated($@"INSERT INTO db_field_values (recordid,fieldid,value)
                    SELECT id,{Text},CASE WHEN position % 2 = 0 THEN 'even' ELSE 'odd' END
                    FROM db_records WHERE collectionid={Collection}");
                context.Database.ExecuteSqlInterpolated($@"INSERT INTO db_field_values (recordid,fieldid,value)
                    SELECT id,{Fixed},CASE WHEN position % 2 = 0 THEN '1800' ELSE '' END
                    FROM db_records WHERE collectionid={Collection}");
                context.ChangeTracker.Clear();
            }
            catch
            {
                Db?.Dispose();
                _enableCommandEvents.Dispose();
                throw;
            }
        }

        internal int PositionOf(int id) => _positions[id];
        internal string Expected(int id) => (PositionOf(id) % 3) switch { 1 => "7200", 2 => "10800", _ => "" };

        private int Period(string name, DatePeriodConfig config) => Repo.AddField(new DbField
        {
            CollectionId = Collection, Name = name, FieldType = "date_period", IsSystem = true,
            ConfigJson = config.ToConfigJson(),
        }).Id!.Value;

        public void Dispose()
        {
            try { Db.Dispose(); }
            finally { _enableCommandEvents.Dispose(); }
        }
    }

    /// <summary>Считаем настоящие команды EF, включая изолированные фоновые контексты.
    /// Время не ограничиваем: медленный диск не превращает проверку формы запросов в случайную.</summary>
    private sealed class Commands : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly string? _identity;
        private readonly IDisposable _all;
        private readonly List<IDisposable> _subscriptions = [];
        private readonly ConcurrentQueue<string> _commands = new();

        internal Commands(string? path)
        {
            _identity = path is null ? null : path.StartsWith("Host=", StringComparison.OrdinalIgnoreCase)
                ? new Npgsql.NpgsqlConnectionStringBuilder(path).Database! : path;
            _all = DiagnosticListener.AllListeners.Subscribe(this);
        }

        internal void AssertBounded()
        {
            Assert.NotEmpty(_commands);
            Assert.True(_commands.Count <= 20,
                $"Одна колонка потребовала {_commands.Count} команд; число запросов не должно расти на каждую порцию из 200 записей.");
        }

        internal void AssertNoAdditionalValueReads()
        {
            Assert.NotEmpty(_commands);
            // GetFieldValuesBatch читает само окно одним прямым SQL. Дополнительный EF-запрос
            // значений означал бы ненужное повторное чтение снимка либо его исходных дат.
            Assert.DoesNotContain(_commands, sql => sql.Contains("db_field_values", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(_commands, sql => sql.Contains("= 'date'", StringComparison.OrdinalIgnoreCase));
        }

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name != "Microsoft.EntityFrameworkCore") return;
            lock (_subscriptions) _subscriptions.Add(listener.Subscribe(this,
                name => name == "Microsoft.EntityFrameworkCore.Database.Command.CommandExecuted"));
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (_identity is not null && value.Value is CommandExecutedEventData data
                && (data.Command.Connection?.ConnectionString.Contains(_identity, StringComparison.OrdinalIgnoreCase) ?? false))
                _commands.Enqueue(data.Command.CommandText);
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
        public void Dispose()
        {
            _all.Dispose();
            lock (_subscriptions) foreach (var subscription in _subscriptions) subscription.Dispose();
        }
    }
}
