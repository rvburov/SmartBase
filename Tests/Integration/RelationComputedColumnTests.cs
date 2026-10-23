using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Данные из связанных записей по столбцу целиком (отбор, сортировка, окно фильтра) дают ровно то
/// же значение, что страница и карточка записи, — на случайных данных, со старыми, удаленными и
/// перенесенными целями. Число запросов не зависит от числа записей.
/// </summary>
public sealed class RelationComputedColumnTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void The_column_equals_the_value_of_each_record(int seed)
    {
        using var db = new TempDatabase();
        var f = new Fixture(db, seed, sources: 400);
        foreach (var field in f.Computed)
        {
            var column = f.Repo.ReadRelationComputedColumn(f.Sources, field);
            var single = f.SourceIds.ToDictionary(id => id,
                id => f.Repo.GetRelationComputedValues([id], [field]).GetValueOrDefault(id)?.GetValueOrDefault(field.Id!.Value, "") ?? "");
            foreach (var id in f.SourceIds)
                Assert.True(single[id] == column.ValueOf(id), $"{field.Name}, запись {id}: «{single[id]}» и «{column.ValueOf(id)}»");
            // Малый срез считается сам и дает то же.
            var subset = f.Repo.RelationComputedValuesFor(f.Sources, field, f.SourceIds);
            Assert.All(f.SourceIds, id => Assert.Equal(single[id], subset(id)));
            Assert.Equal(f.SourceIds, f.Repo.GetRelationComputedColumnValues(f.Sources, field).Keys);
        }
    }

    [SkippableFact]
    public void The_column_equals_the_value_of_each_record_on_PostgreSQL()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        var cs = PgTestServer.ConnectionString(PgTestServer.Database1);
        using (var connection = new Npgsql.NpgsqlConnection(cs))
        {
            connection.Open();
            using var reset = connection.CreateCommand();
            reset.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
            reset.ExecuteNonQuery();
        }
        using var pg = DatabaseManager.Open(101, "pg-computed-column", cs, provider: "postgres");
        var f = new Fixture(pg.Constructor, pg.Context, pg.Constructor.RelationBaseId, seed: 9, sources: 300);
        foreach (var field in f.Computed)
        {
            var column = f.Repo.ReadRelationComputedColumn(f.Sources, field);
            foreach (var id in f.SourceIds)
                Assert.Equal(f.Repo.GetRelationComputedValues([id], [field]).GetValueOrDefault(id)?.GetValueOrDefault(field.Id!.Value, "") ?? "",
                    column.ValueOf(id));
        }
    }

    [Fact]
    public void Filter_and_sort_agree_with_the_values_for_a_small_slice_and_the_whole_column()
    {
        using var db = new TempDatabase();
        var f = new Fixture(db, seed: 7, sources: 300);
        var count = f.Computed.Single(c => c.Name == "Количество");
        var sum = f.Computed.Single(c => c.Name == "Сумма");
        var previous = ConstructorRepository.SubsetLimit;
        try
        {
            foreach (var limit in new[] { previous, 10 })
            {
                ConstructorRepository.SubsetLimit = limit;
                var values = f.SourceIds.ToDictionary(id => id, id => f.Repo.ReadRelationComputedColumn(f.Sources, count).ValueOf(id));
                var key = count.Id!.Value.ToString();
                var zero = f.Repo.GetFilteredRecordIds(f.Sources, new Dictionary<string, HashSet<string>> { [key] = ["0"] });
                Assert.Equal(f.SourceIds.Where(id => values[id] == "0"), zero);
                var notZero = f.Repo.GetFilteredRecordIds(f.Sources,
                    new Dictionary<string, HashSet<string>> { [key] = FilterExclusion.Exclude(["0"]) });
                Assert.Equal(f.SourceIds.Where(id => values[id] != "0"), notZero);
                var two = f.Repo.GetFilteredRecordIds(f.Sources, new Dictionary<string, HashSet<string>> { [key] = ["2", "3"] });
                Assert.Equal(f.SourceIds.Where(id => values[id] is "2" or "3"), two);

                // Сортировка итога: числа по направлению, равные — в порядке таблицы, пустые и
                // подписи ошибок — в конце при любом направлении.
                foreach (var asc in new[] { true, false })
                {
                    var sorted = f.Repo.GetFilteredRecordIdsAsync(f.Sources, new Dictionary<string, HashSet<string>>(), false,
                        sum.Id!.Value.ToString(), asc, "rollup", default).GetAwaiter().GetResult();
                    var sums = f.SourceIds.ToDictionary(id => id, id => f.Repo.ReadRelationComputedColumn(f.Sources, sum).ValueOf(id));
                    var numbers = f.SourceIds.Where(id => decimal.TryParse(sums[id], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out _)).ToList();
                    var expected = (asc ? numbers.OrderBy(id => decimal.Parse(sums[id], System.Globalization.CultureInfo.InvariantCulture))
                            : numbers.OrderByDescending(id => decimal.Parse(sums[id], System.Globalization.CultureInfo.InvariantCulture)))
                        .Concat(f.SourceIds.Where(id => !numbers.Contains(id))).ToList();
                    Assert.Equal(expected, sorted);
                }
            }
        }
        finally { ConstructorRepository.SubsetLimit = previous; }
    }

    [Fact]
    public void The_number_of_queries_does_not_grow_with_the_number_of_records()
    {
        int Queries(int sources)
        {
            using var db = new TempDatabase();
            var f = new Fixture(db, seed: 11, sources: sources, targets: 30);
            var field = f.Computed.Single(c => c.Name == "Номера");
            var counter = new System.Runtime.CompilerServices.StrongBox<int>();
            ConstructorRepository.ColumnQueries.Value = counter;
            try { f.Repo.ReadRelationComputedColumn(f.Sources, field); }
            finally { ConstructorRepository.ColumnQueries.Value = null; }
            return counter.Value;
        }
        var small = Queries(1000);
        Assert.True(small > 0);
        Assert.Equal(small, Queries(5000));
    }

    [Fact]
    public async Task The_filter_window_reuses_its_snapshot_and_searches_the_shown_text()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var target = SampleData.AddCollection(repo, "Цели").Id!.Value;
        var number = SampleData.AddField(repo, target, "Число", "number").Id!.Value;
        var sources = SampleData.AddCollection(repo, "Источники").Id!.Value;
        var relation = SampleData.AddField(repo, sources, "Связь", "relation");
        relation.ConfigJson = new RelationConfig(target, false).ToConfigJson();
        repo.UpdateField(relation);
        const int linked = ConstructorRepository.FilterValueCap + 100;
        var targets = repo.BulkAddRecords(target, [number], Enumerable.Range(1, linked)
            .Select(i => new BulkRecordRow($"Ц{i}", [i == 1 ? "11.75" : i.ToString(System.Globalization.CultureInfo.InvariantCulture)])), linked, default);
        var ids = repo.BulkAddRecords(sources, [], Enumerable.Range(1, linked + 5).Select(i => new BulkRecordRow($"И{i}", [])), linked + 5, default);
        db.Context.DbRecordLinks.AddRange(Enumerable.Range(0, linked).Select(i => new DbRecordLinkEntity
            { FieldId = relation.Id!.Value, FromRecordId = ids[i], ToRecordId = targets[i], Position = 1000 }));
        db.Context.SaveChanges();
        var sum = repo.AddField(new DbField { CollectionId = sources, Name = "Сумма", FieldType = "rollup",
            ConfigJson = new RelationComputedConfig(relation.Id!.Value, number.ToString(), "sum").ToConfigJson() });
        var values = repo.AddField(new DbField { CollectionId = sources, Name = "Числа", FieldType = "lookup",
            ConfigJson = new RelationComputedConfig(relation.Id!.Value, number.ToString(), "lookup").ToConfigJson() });
        var key = sum.Id!.Value.ToString();
        var previous = ConstructorRepository.SubsetLimit;
        ConstructorRepository.SubsetLimit = 10;
        var counter = new System.Runtime.CompilerServices.StrongBox<int>();
        ConstructorRepository.ColumnQueries.Value = counter;
        try
        {
            var list = await repo.GetColumnValueListAsync(sources, key);
            // Разных значений больше, чем показывает окно: «(Пусто)» досчитают итоги, в списке его нет.
            Assert.True(list.Capped);
            Assert.Equal(ConstructorRepository.FilterValueCap, list.Values.Count);
            Assert.DoesNotContain(list.Values, v => v.Value.Length == 0);
            // Первые значения — порядком итога, как числа.
            Assert.Equal(["0", "2", "3"], list.Values.Take(3).Select(v => v.Value));
            Assert.NotNull(list.Snapshot);
            var read = counter.Value;
            Assert.True(read > 0);

            // Поиск по показанному тексту («11,7» — так итог 11.75 виден в таблице) и итоги окна
            // идут по снимку, столбец заново не считается.
            var found = await repo.GetColumnValueListAsync(sources, key, search: "11,7", reuse: list.Snapshot);
            Assert.Contains(found.Values, v => v.Value == "11.75");
            // Итог суммы у записей без целей — «0», а не пусто.
            Assert.Equal((linked + 1L, 0), await repo.GetColumnValueTotalsAsync(sources, key, reuse: list.Snapshot));
            Assert.Equal(read, counter.Value);
            // У значений без целей — «(Пусто)»: в ограниченном списке его нет, его досчитывают итоги.
            var lookupKey = values.Id!.Value.ToString();
            var lookupList = await repo.GetColumnValueListAsync(sources, lookupKey);
            Assert.True(lookupList.Capped);
            Assert.DoesNotContain(lookupList.Values, v => v.Value.Length == 0);
            Assert.Equal(5, (await repo.GetColumnValueTotalsAsync(sources, lookupKey, reuse: lookupList.Snapshot)).Empty);
            read = counter.Value;
            // Снимок другого среза не годится: столбец считается заново.
            var others = new Dictionary<string, HashSet<string>> { [RelationConfig.TitleField] = FilterExclusion.Exclude(["И1"]) };
            await repo.GetColumnValueListAsync(sources, key, others, reuse: list.Snapshot);
            Assert.True(counter.Value > read);
        }
        finally
        {
            ConstructorRepository.ColumnQueries.Value = null;
            ConstructorRepository.SubsetLimit = previous;
        }
    }

    [Fact]
    public async Task A_long_calculation_shows_progress_and_stops_when_cancelled()
    {
        using var db = new TempDatabase();
        var f = new Fixture(db, seed: 5, sources: 200);
        var field = f.Computed.Single(c => c.Name == "Номера");
        var filters = new Dictionary<string, HashSet<string>> { [field.Id!.Value.ToString()] = [""] };
        var previousLimit = ConstructorRepository.SubsetLimit;
        var previousShow = RelationColumnRun.ShowAfter;
        var seen = new List<SmartBase.Core.BackgroundJob>();
        void Changed() { lock (seen) seen.AddRange(SmartBase.Core.BackgroundJobs.All.Where(j => j.Id.StartsWith("relation-column:", StringComparison.Ordinal))); }
        ConstructorRepository.SubsetLimit = 10;
        RelationColumnRun.ShowAfter = TimeSpan.Zero;
        SmartBase.Core.BackgroundJobs.Changed += Changed;
        try
        {
            var ids = await f.Repo.GetFilteredRecordIdsAsync(f.Sources, filters, false, null, true, null, default);
            Assert.NotEmpty(ids);
            lock (seen)
            {
                Assert.Contains(seen, j => j.Title == "Данные из связанных записей" && j.Detail.StartsWith("Столбец «Номера»", StringComparison.Ordinal));
                Assert.All(seen, j => Assert.DoesNotContain(db.Path, j.Detail, StringComparison.Ordinal));
            }
            Assert.DoesNotContain(SmartBase.Core.BackgroundJobs.All, j => j.Id.StartsWith("relation-column:", StringComparison.Ordinal));

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => f.Repo.ReadRelationComputedColumn(f.Sources, field, cancelled.Token));

            // Отмена посреди расчета — с первой строкой хода, и столбцом целиком, и малым срезом: расчет
            // обрывается, а не доживает свое в фоне.
            foreach (var limit in new[] { 10, 100_000 })
            {
                ConstructorRepository.SubsetLimit = limit;
                using var cts = new CancellationTokenSource();
                void CancelOnProgress()
                {
                    if (SmartBase.Core.BackgroundJobs.All.Any(j => j.Id.StartsWith("relation-column:", StringComparison.Ordinal)))
                        cts.Cancel();
                }
                SmartBase.Core.BackgroundJobs.Changed += CancelOnProgress;
                try
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                        f.Repo.GetFilteredRecordIdsAsync(f.Sources, filters, false, null, true, null, cts.Token));
                }
                finally { SmartBase.Core.BackgroundJobs.Changed -= CancelOnProgress; }
                Assert.True(cts.IsCancellationRequested);
            }

            // Малый срез тоже показывает ход.
            ConstructorRepository.SubsetLimit = 100_000;
            lock (seen) seen.Clear();
            Assert.NotEmpty(await f.Repo.GetFilteredRecordIdsAsync(f.Sources, filters, false, null, true, null, default));
            lock (seen) Assert.Contains(seen, j => j.Detail.StartsWith("Столбец «Номера»: 0 из ", StringComparison.Ordinal) && j.Detail.EndsWith(" записей", StringComparison.Ordinal));
            Assert.DoesNotContain(SmartBase.Core.BackgroundJobs.All, j => j.Id.StartsWith("relation-column:", StringComparison.Ordinal));
        }
        finally
        {
            SmartBase.Core.BackgroundJobs.Changed -= Changed;
            ConstructorRepository.SubsetLimit = previousLimit;
            RelationColumnRun.ShowAfter = previousShow;
        }
    }

    /// <summary>Строка хода появляется через секунду от начала расчета и без следующего отчета: один
    /// долгий запрос к базе ее не прячет. Закрытый расчет убирает строку.</summary>
    [Fact]
    public async Task The_progress_row_appears_on_time_without_a_further_report()
    {
        var previousShow = RelationColumnRun.ShowAfter;
        RelationColumnRun.ShowAfter = TimeSpan.FromMilliseconds(300);
        bool Shown() => SmartBase.Core.BackgroundJobs.All.Any(j => j.Id.StartsWith("relation-column:", StringComparison.Ordinal));
        try
        {
            using (var run = new RelationColumnRun(default, new Dictionary<int, string>()))
            {
                run.Report("Столбец «Итого»: чтение связей");
                Assert.False(Shown());
                for (var i = 0; i < 100 && !Shown(); i++) await Task.Delay(50);
                Assert.Contains(SmartBase.Core.BackgroundJobs.All, j => j.Title == "Данные из связанных записей"
                                                                        && j.Detail == "Столбец «Итого»: чтение связей");
            }
            Assert.False(Shown());

            // Расчет без данных из связанных записей отчетов не шлет — и строки не бывает.
            using (new RelationColumnRun(default, new Dictionary<int, string>())) await Task.Delay(600);
            Assert.False(Shown());

            // Закрытый до срока расчет строку не оставляет.
            var early = new RelationColumnRun(default, new Dictionary<int, string>());
            early.Report("Столбец «Итого»: чтение связей");
            early.Dispose();
            await Task.Delay(600);
            Assert.False(Shown());
        }
        finally { RelationColumnRun.ShowAfter = previousShow; }
    }

    /// <summary>Запись с сотнями ребер не сбивает счет следующих: набор пар цели заводится заново
    /// после большой записи и чистится после малой.</summary>
    [Fact]
    public void A_record_with_many_links_does_not_hide_the_links_of_the_next_records()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var targets = SampleData.AddCollection(repo, "Цели").Id!.Value;
        var sources = SampleData.AddCollection(repo, "Источники").Id!.Value;
        var relation = SampleData.AddField(repo, sources, "Связь", "relation");
        relation.ConfigJson = new RelationConfig(targets, false).ToConfigJson();
        repo.UpdateField(relation);
        var targetIds = repo.BulkAddRecords(targets, [], Enumerable.Range(1, 300).Select(i => new BulkRecordRow($"Ц{i}", [])), 300, default);
        var sourceIds = repo.BulkAddRecords(sources, [], Enumerable.Range(1, 4).Select(i => new BulkRecordRow($"И{i}", [])), 4, default);
        db.Context.DbRecordLinks.AddRange(targetIds.Select((to, i) => new DbRecordLinkEntity
            { FieldId = relation.Id!.Value, FromRecordId = sourceIds[0], ToRecordId = to, Position = i * 1000 }));
        foreach (var source in sourceIds.Skip(1))
            db.Context.DbRecordLinks.Add(new DbRecordLinkEntity { FieldId = relation.Id!.Value, FromRecordId = source, ToRecordId = targetIds[0], Position = 0 });
        db.Context.SaveChanges();
        db.Context.ChangeTracker.Clear();
        var count = repo.AddField(new DbField
        {
            CollectionId = sources, Name = "Количество", FieldType = "rollup",
            ConfigJson = new RelationComputedConfig(relation.Id!.Value, RelationConfig.TitleField, "count").ToConfigJson(),
        });

        var column = repo.ReadRelationComputedColumn(sources, count);

        Assert.Equal(["300", "1", "1", "1"], sourceIds.Select(column.ValueOf));
        var page = repo.GetRelationComputedValues(sourceIds, [count]);
        Assert.Equal(sourceIds.Select(id => page[id][count.Id!.Value]), sourceIds.Select(column.ValueOf));
    }

    [Fact]
    public void The_edge_pass_needs_no_sort()
    {
        using var db = new TempDatabase();
        var f = new Fixture(db, seed: 3, sources: 50);
        var relation = RelationComputedConfig.FromConfig(f.Computed[0].ConfigJson)!.RelationFieldId;
        using var command = f.Repo.ComputedColumnEdges(f.Sources, relation).CreateDbCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
        db.Context.Database.OpenConnection();
        try
        {
            using var reader = command.ExecuteReader();
            var plan = new List<string>();
            while (reader.Read()) plan.Add(reader.GetString(3));
            Assert.DoesNotContain(plan, line => line.Contains("TEMP B-TREE", StringComparison.Ordinal));
            Assert.Contains(plan, line => line.Contains("IX_db_record_links_FieldId_FromRecordId_Position", StringComparison.Ordinal));
        }
        finally { db.Context.Database.CloseConnection(); }
    }

    [Fact]
    public void Ranks_give_the_same_order_as_a_stable_sort_by_the_comparer()
    {
        // Много значений, равных для компаратора (регистр), — сортировка различных значений сама
        // по себе неустойчива, и только ранги держат порядок таблицы.
        var random = new Random(17);
        var words = Enumerable.Range(0, 12).SelectMany(i => new[] { $"ab{i}", $"Ab{i}", $"aB{i}", $"AB{i}" }).ToArray();
        var ids = Enumerable.Range(1, 2000).ToList();
        var lookup = ids.ToDictionary(id => id, _ => random.Next(10) == 0 ? "" : words[random.Next(words.Length)]);
        var totals = ids.ToDictionary(id => id, _ => random.Next(8) switch
        {
            0 => "", 1 => "(нет доступа)", _ => (random.Next(-20, 20) / 4m).ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        // Итоги с равными ключами в разной записи («10» и «10.0») и итоги по датам.
        var mixed = ids.ToDictionary(id => id, _ => random.Next(7) switch
        {
            0 => "", 1 => "(база недоступна)", 2 => "10", 3 => "10.0", 4 => "2026-01-0" + random.Next(1, 4),
            5 => "2026-01-02T10:00", _ => random.Next(-3, 3).ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        foreach (var asc in new[] { true, false })
        {
            var text = ColumnSort.BuildComparer("text", asc);
            Assert.Equal(ids.OrderBy(id => lookup[id], Comparer<string>.Create((a, b) => text(a, b))).ToList(),
                RelationComputedSort.Order(ids, id => lookup[id], "lookup", asc));
            // Итог: как числа, а подписи ошибок — в конце вместе с пустыми.
            var numbers = ids.Where(id => totals[id] is not ("" or "(нет доступа)")).ToList();
            var expected = (asc ? numbers.OrderBy(id => decimal.Parse(totals[id], System.Globalization.CultureInfo.InvariantCulture))
                    : numbers.OrderByDescending(id => decimal.Parse(totals[id], System.Globalization.CultureInfo.InvariantCulture)))
                .Concat(ids.Where(id => !numbers.Contains(id))).ToList();
            Assert.Equal(expected, RelationComputedSort.Order(ids, id => totals[id], "rollup", asc));
            // Тот же порядок, что устойчивая сортировка прежним компаратором, с хвостом в порядке таблицы.
            var rollup = ColumnSort.BuildComparer("rollup", asc);
            bool Tail(string v) => v.Length == 0 || double.IsPositiveInfinity(ColumnSort.RollupKey(v));
            Assert.Equal(ids.Where(id => !Tail(mixed[id])).OrderBy(id => mixed[id], Comparer<string>.Create((a, b) => rollup(a, b)))
                    .Concat(ids.Where(id => Tail(mixed[id]))).ToList(),
                RelationComputedSort.Order(ids, id => mixed[id], "rollup", asc));
        }

        // Первые значения окна фильтра — тем же порядком.
        var shown = mixed.Values.Concat(lookup.Values).Where(v => v.Length > 0).Distinct().ToList();
        foreach (var type in new[] { "rollup", "lookup" })
        {
            var compare = ColumnSort.BuildComparer(type == "rollup" ? "rollup" : "text", true);
            Assert.Equal(shown.OrderBy(v => v, Comparer<string>.Create((a, b) => compare(a, b))).ToList(),
                RelationComputedSort.Ascending(shown, v => v, type).ToList());
        }
    }

    [Fact]
    public void Ranks_keep_the_table_order_for_values_equal_to_the_comparer()
    {
        var values = new Dictionary<int, string> { [1] = "abc", [2] = "", [3] = "ABC", [4] = "b", [5] = "a" };
        Assert.Equal([5, 1, 3, 4, 2], RelationComputedSort.Order([1, 2, 3, 4, 5], id => values[id], "lookup", true));
        Assert.Equal([4, 1, 3, 5, 2], RelationComputedSort.Order([1, 2, 3, 4, 5], id => values[id], "lookup", false));
        var totals = new Dictionary<int, string> { [1] = "(нет доступа)", [2] = "10", [3] = "", [4] = "2.5", [5] = "10" };
        Assert.Equal([4, 2, 5, 1, 3], RelationComputedSort.Order([1, 2, 3, 4, 5], id => totals[id], "rollup", true));
        Assert.Equal([2, 5, 4, 1, 3], RelationComputedSort.Order([1, 2, 3, 4, 5], id => totals[id], "rollup", false));
    }

    // Источники и цели со случайными ребрами: повторы цели, совпадающие позиции, удаленная цель,
    // цель из другой коллекции, ребро записи из другой коллекции, старое ребро с номером базы.
    private sealed class Fixture
    {
        public ConstructorRepository Repo { get; }
        public int Sources { get; }
        public List<int> SourceIds { get; }
        public List<DbField> Computed { get; } = [];

        public Fixture(TempDatabase db, int seed, int sources, int targets = 60)
            : this(db.Constructor, db.Context, db.LinkId, seed, sources, targets) { }

        public Fixture(ConstructorRepository repo, AppDbContext context, int linkId, int seed, int sources, int targets = 60)
        {
            Repo = repo;
            var random = new Random(seed);
            var targetCollection = SampleData.AddCollection(Repo, "Цели").Id!.Value;
            var other = SampleData.AddCollection(Repo, "Другая").Id!.Value;
            Sources = SampleData.AddCollection(Repo, "Источники").Id!.Value;
            var number = SampleData.AddField(Repo, targetCollection, "Число", "number").Id!.Value;
            var date = SampleData.AddField(Repo, targetCollection, "Дата", "date").Id!.Value;
            var text = SampleData.AddField(Repo, targetCollection, "Текст").Id!.Value;
            var relation = SampleData.AddField(Repo, Sources, "Связь", "relation");
            relation.ConfigJson = new RelationConfig(targetCollection, false).ToConfigJson();
            Repo.UpdateField(relation);
            var targetIds = Repo.BulkAddRecords(targetCollection, [number, date, text], Enumerable.Range(1, targets).Select(i =>
                new BulkRecordRow(i % 9 == 0 ? "" : $"Цель {i % 17}",
                [i % 7 == 0 ? "" : i % 11 == 0 ? "abc" : (random.Next(-50, 500) / 4m).ToString(System.Globalization.CultureInfo.InvariantCulture),
                 i % 5 == 0 ? "" : $"2026-{random.Next(1, 13):D2}-{random.Next(1, 28):D2}",
                 i % 3 == 0 ? "" : $"т{random.Next(0, 9)}"])), targets, default);
            var strangers = Repo.BulkAddRecords(other, [], Enumerable.Range(1, 5).Select(i => new BulkRecordRow($"Чужая {i}", [])), 5, default);
            SourceIds = Repo.BulkAddRecords(Sources, [], Enumerable.Range(1, sources).Select(i => new BulkRecordRow($"И{i}", [])), sources, default);
            var deleted = targetIds[^1];
            var edges = new List<DbRecordLinkEntity>();
            foreach (var source in SourceIds)
            {
                var n = random.Next(0, 5);
                for (var k = 0; k < n; k++)
                {
                    var roll = random.Next(0, 20);
                    var to = roll == 0 ? strangers[random.Next(strangers.Count)] : targetIds[random.Next(targetIds.Count)];
                    edges.Add(new DbRecordLinkEntity
                    {
                        // Старое ребро со своим номером базы и ребро в базу, которой здесь нет.
                        FieldId = relation.Id!.Value, FromRecordId = source, ToRecordId = to,
                        TargetDbId = roll == 1 ? linkId : roll == 2 ? 1_234_567 : 0,
                        Position = random.Next(0, 3) * 1000,
                    });
                }
            }
            // Ребро записи из другой коллекции.
            edges.Add(new DbRecordLinkEntity { FieldId = relation.Id!.Value, FromRecordId = strangers[0], ToRecordId = targetIds[0], Position = 0 });
            var unique = edges.GroupBy(e => (e.FieldId, e.FromRecordId, e.TargetDbId, e.ToRecordId)).Select(g => g.First()).ToList();
            context.DbRecordLinks.AddRange(unique);
            context.SaveChanges();
            context.ChangeTracker.Clear();
            Repo.DeleteRecord(deleted);

            DbField Add(string name, string type, string target, string aggregate) => Repo.AddField(new DbField
            {
                CollectionId = Sources, Name = name, FieldType = type,
                ConfigJson = new RelationComputedConfig(relation.Id.Value, target, aggregate).ToConfigJson(),
            });
            Computed.Add(Add("Названия", "lookup", RelationConfig.TitleField, "lookup"));
            Computed.Add(Add("Номера", "lookup", number.ToString(), "lookup"));
            Computed.Add(Add("Тексты", "lookup", text.ToString(), "lookup"));
            Computed.Add(Add("Количество", "rollup", RelationConfig.TitleField, "count"));
            Computed.Add(Add("Сумма", "rollup", number.ToString(), "sum"));
            Computed.Add(Add("Среднее", "rollup", number.ToString(), "avg"));
            Computed.Add(Add("Наименьшее", "rollup", number.ToString(), "min"));
            Computed.Add(Add("Самая ранняя", "rollup", date.ToString(), "earliest"));
            Computed.Add(Add("Самая поздняя", "rollup", date.ToString(), "latest"));
        }
    }
}

public sealed class RelationComputedWorkspaceTests
{
    /// <summary>Старое ребро в базу из списка, которая сейчас не подключена: карточка не подключает ее
    /// синхронно и показывает то же, что таблица и окно фильтра, — «(база недоступна)».</summary>
    [Fact]
    public async Task An_unmounted_base_of_an_old_link_reads_the_same_in_the_card_and_in_the_filter()
    {
        var appDb = TempDatabase.NewTempPath();
        var def = TempDatabase.NewTempPath();
        var archivePath = TempDatabase.NewTempPath();
        try
        {
            using var registry = DatabaseRegistry.Open(def, appDb);
            var defDb = DatabaseManager.Open(registry.GetAll().First(d => d.IsDefault).Id, "def", def);
            var ws = Workspace.Attach(registry, defDb);
            var entry = registry.Add("Архив", archivePath);
            var archive = ws.Mount(entry)!;
            RelationDbResolver.Configure(ws);
            try
            {
                var repo = defDb.Constructor;
                var archived = SampleData.AddCollection(archive.Constructor, "Старые").Id!.Value;
                var old = SampleData.AddRecord(archive.Constructor, archived, "Старая цель").Id!.Value;
                var archiveId = archive.Constructor.RelationBaseId;
                var targets = SampleData.AddCollection(repo, "Цели").Id!.Value;
                var target = SampleData.AddRecord(repo, targets, "Цель").Id!.Value;
                var sources = SampleData.AddCollection(repo, "Источники").Id!.Value;
                var source = SampleData.AddRecord(repo, sources, "И1").Id!.Value;
                var plain = SampleData.AddRecord(repo, sources, "И2").Id!.Value;
                var relation = SampleData.AddField(repo, sources, "Связь", "relation");
                relation.ConfigJson = new RelationConfig(targets, false).ToConfigJson();
                repo.UpdateField(relation);
                repo.SetLinks(relation.Id!.Value, plain, [target]);
                defDb.Context.DbRecordLinks.Add(new DbRecordLinkEntity
                    { FieldId = relation.Id!.Value, FromRecordId = source, ToRecordId = old, TargetDbId = archiveId, Position = 0 });
                defDb.Context.SaveChanges();
                var titles = repo.AddField(new DbField
                {
                    CollectionId = sources, Name = "Названия", FieldType = "lookup",
                    ConfigJson = new RelationComputedConfig(relation.Id!.Value, RelationConfig.TitleField, "lookup").ToConfigJson(),
                });
                var key = titles.Id!.Value;
                ws.Unmount(entry.Id);

                var card = repo.GetRelationComputedValues([source, plain], [titles]);
                var list = await repo.GetColumnValueListAsync(sources, key.ToString());

                Assert.Null(ws.Get(entry.Id));
                Assert.Equal(RelationProperty.Unavailable, card[source][key]);
                Assert.Equal("Цель", card[plain][key]);
                Assert.Equal([RelationProperty.Unavailable, "Цель"], list.Values.Select(v => v.Value).Order(StringComparer.Ordinal));
            }
            finally
            {
                RelationDbResolver.Configure(null);
                ws.CloseAll(TimeSpan.FromSeconds(30));
            }
        }
        finally { foreach (var p in new[] { archivePath, def, appDb }) TempDatabase.Cleanup(p); }
    }
}
