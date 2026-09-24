using SmartBase.Core.Models;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;
using SmartBase.ViewModels.ConstructorDb;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Страницы таблицы этапа 1 плана drawn-grid.md (<c>ConstructorRepository.GridPages</c>): сортировка
/// по индексу значений (голова keyset в обе стороны и смещением с ближнего края, хвост без значения
/// в порядке таблицы), смещение с конца в обычном порядке, окно колонок, оценка числа записей и
/// снимок без COUNT, фоновый список id с сортировкой по типам, выбор источника планировщиком.
/// Эталон — та же выборка, посчитанная в памяти: байтовый порядок значений, пустые в конце,
/// равные — в порядке таблицы.
/// </summary>
public sealed class GridPagesTests
{
    private static readonly string[] Cities =
        ["Москва", "казань", "Тверь", "Омск", "москва", "Ялта", "Абакан", "Berlin", "берлин", "Омск"];

    private sealed record Seeded(int Col, int Text, int Num, int Date, List<DbRecord> Recs,
                                 Dictionary<int, string> TextOf, Dictionary<int, string> NumOf);

    // Коллекция на n записей: текст — каждая 4-я без значения, одна — с пустой строкой в базе;
    // число — «18,5»/«7»/…, каждая 5-я без значения, каждая 7-я нечисловая; дата — ISO, каждая 3-я пустая.
    private static Seeded Seed(TempDatabase db, int n = 25)
    {
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Сортировка").Id!.Value;
        var text = SampleData.AddField(repo, col, "Город",  position: 10).Id!.Value;
        var num  = SampleData.AddField(repo, col, "Сумма",  "number", position: 20).Id!.Value;
        var date = SampleData.AddField(repo, col, "Дата",   "date",   position: 30).Id!.Value;

        var recs   = new List<DbRecord>();
        var textOf = new Dictionary<int, string>();
        var numOf  = new Dictionary<int, string>();
        for (var i = 1; i <= n; i++)
        {
            var r  = SampleData.AddRecord(repo, col, $"З{i:D3}", i * 1000);
            var id = r.Id!.Value;
            recs.Add(r);

            if (i % 4 != 0)
            {
                textOf[id] = Cities[i % Cities.Length];
                repo.SetFieldValue(id, text, textOf[id]);
            }
            if (i % 5 != 0)
            {
                numOf[id] = i % 7 == 0 ? "abc" : (i * 37 % 23).ToString() + (i % 2 == 0 ? ",5" : "");
                repo.SetFieldValue(id, num, numOf[id]);
            }
            if (i % 3 != 0)
                repo.SetFieldValue(id, date, new DateValue(new DateTime(2026, 1, 1).AddDays(i * 7 % 400), default, true).ToStorage());
        }

        // Пустая строка значения в базе — тоже «без значения» (хвост), как и отсутствующая строка.
        var emptyId = recs[7].Id!.Value;   // i = 8: без значения текста — добавим строку ""
        db.Context.DbFieldValues.Add(new DbFieldValueEntity { RecordId = emptyId, FieldId = text, Value = "" });
        db.Context.SaveChanges();

        return new Seeded(col, text, num, date, recs, textOf, numOf);
    }

    private static List<int> Ids(IEnumerable<DbRecord> recs) => recs.Select(r => r.Id!.Value).ToList();

    // Эталон головы: непустые значения по байтовому порядку (совпадает с порядком UTF-16 для
    // кириллицы и латиницы), равные — по Id; asc/desc — направление.
    private static List<int> HeadReference(Seeded s, bool asc)
    {
        var q = s.Recs.Where(r => s.TextOf.ContainsKey(r.Id!.Value));
        var ordered = asc
            ? q.OrderBy(r => s.TextOf[r.Id!.Value], StringComparer.Ordinal).ThenBy(r => r.Id)
            : q.OrderByDescending(r => s.TextOf[r.Id!.Value], StringComparer.Ordinal).ThenByDescending(r => r.Id);
        return Ids(ordered);
    }

    private static List<int> TailReference(Seeded s) =>
        Ids(s.Recs.Where(r => !s.TextOf.ContainsKey(r.Id!.Value)).OrderBy(r => r.Position).ThenBy(r => r.Id));

    private static List<int> WalkHead(ConstructorRepository repo, Seeded s, bool asc, int pageSize)
    {
        var ids = new List<int>();
        string? v = null; int? id = null;
        while (true)
        {
            var p = repo.GetRecordsSortedHead(s.Col, s.Text, asc, false, pageSize, v, id, null, null);
            if (p.Records.Count == 0) break;
            ids.AddRange(Ids(p.Records));
            v  = p.SortValues![^1];
            id = p.Records[^1].Id;
            if (p.Records.Count < pageSize) break;
        }
        return ids;
    }

    private static List<int> WalkTail(ConstructorRepository repo, Seeded s, int pageSize, bool excludeHidden = false)
    {
        var ids = new List<int>();
        int? pos = null, id = null;
        while (true)
        {
            var rows = repo.GetRecordsWithoutValue(s.Col, s.Text, excludeHidden, pageSize, pos, id, null, null);
            if (rows.Count == 0) break;
            ids.AddRange(Ids(rows));
            pos = rows[^1].Position; id = rows[^1].Id;
            if (rows.Count < pageSize) break;
        }
        return ids;
    }

    // ── Сортировка по индексу: голова и хвост ─────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Sorted_head_keyset_walk_matches_reference_in_both_directions(bool asc)
    {
        using var db = new TempDatabase();
        var s = Seed(db);

        var walked = WalkHead(db.Constructor, s, asc, pageSize: 4);
        Assert.Equal(HeadReference(s, asc), walked);
        Assert.Equal(walked.Count, walked.Distinct().Count());
    }

    [Fact]
    public void Tail_keyset_walk_is_table_order_and_includes_empty_string_values()
    {
        using var db = new TempDatabase();
        var s = Seed(db);

        var walked = WalkTail(db.Constructor, s, pageSize: 3);
        Assert.Equal(TailReference(s), walked);
        Assert.Contains(s.Recs[7].Id!.Value, walked);   // строка со значением "" — в хвосте
        Assert.Equal(s.Recs.Count, HeadReference(s, true).Count + walked.Count);
    }

    [Fact]
    public void Head_count_equals_head_length()
    {
        using var db = new TempDatabase();
        var s = Seed(db);
        Assert.Equal(HeadReference(s, true).Count, db.Constructor.CountNonEmptyValues(s.Col, s.Text, false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Head_offset_from_either_edge_gives_same_page(bool asc)
    {
        using var db = new TempDatabase();
        var s    = Seed(db);
        var repo = db.Constructor;
        var reference = HeadReference(s, asc);
        var head = reference.Count;

        foreach (var skip in new[] { 0, 3, head / 2, head - 5, head - 2 })
        {
            var forward = repo.GetRecordsSortedHead(s.Col, s.Text, asc, false, 5, null, null, null, null, skip: skip, headCount: null);
            var nearest = repo.GetRecordsSortedHead(s.Col, s.Text, asc, false, 5, null, null, null, null, skip: skip, headCount: head);
            var expected = reference.Skip(skip).Take(5).ToList();
            Assert.Equal(expected, Ids(forward.Records));
            Assert.Equal(expected, Ids(nearest.Records));
            // Значение сортировки идет с записью — курсор для соседней страницы.
            Assert.Equal(expected.Select(id => s.TextOf[id]), nearest.SortValues!);
        }
    }

    [Fact]
    public void Head_before_cursor_walks_backwards_to_the_top()
    {
        using var db = new TempDatabase();
        var s    = Seed(db);
        var repo = db.Constructor;
        var reference = HeadReference(s, asc: true);

        // Стартуем с последней страницы головы (смещение с конца), идем вверх курсором «до».
        var head = reference.Count;
        var page = repo.GetRecordsSortedHead(s.Col, s.Text, true, false, 4, null, null, null, null, skip: head - 4, headCount: head);
        var collected = new List<int>(Ids(page.Records));
        while (true)
        {
            var first = page.Records[0];
            page = repo.GetRecordsSortedHead(s.Col, s.Text, true, false, 4, null, null, page.SortValues![0], first.Id);
            if (page.Records.Count == 0) break;
            collected.InsertRange(0, Ids(page.Records));
        }
        Assert.Equal(reference, collected);
    }

    [Fact]
    public void Tail_offset_with_and_without_known_lengths_gives_same_page()
    {
        using var db = new TempDatabase();
        var s    = Seed(db);
        var repo = db.Constructor;
        var reference = TailReference(s);
        var head = s.Recs.Count - reference.Count;

        foreach (var skip in new[] { 0, 2, reference.Count / 2, reference.Count - 2 })
        {
            var expected = reference.Skip(skip).Take(3).ToList();
            Assert.Equal(expected, Ids(repo.GetRecordsWithoutValue(s.Col, s.Text, false, 3, null, null, null, null, skip)));
            Assert.Equal(expected, Ids(repo.GetRecordsWithoutValue(s.Col, s.Text, false, 3, null, null, null, null, skip,
                                                                   tailCount: reference.Count, headCount: head)));
            Assert.Equal(expected, Ids(repo.GetRecordsWithoutValue(s.Col, s.Text, false, 3, null, null, null, null, skip,
                                                                   tailCount: null, headCount: 0)));   // форма NOT IN
        }
    }

    [Fact]
    public void Tail_before_cursor_returns_previous_rows()
    {
        using var db = new TempDatabase();
        var s    = Seed(db);
        var repo = db.Constructor;
        var reference = TailReference(s);

        var last = s.Recs.First(r => r.Id == reference[^1]);
        var before = repo.GetRecordsWithoutValue(s.Col, s.Text, false, 3, null, null, last.Position, last.Id);
        Assert.Equal(reference.Skip(reference.Count - 4).Take(3).ToList(), Ids(before));
    }

    [Fact]
    public void Hidden_records_are_excluded_from_head_tail_and_count()
    {
        using var db = new TempDatabase();
        var s    = Seed(db);
        var repo = db.Constructor;
        var headRef = HeadReference(s, true);
        var tailRef = TailReference(s);
        repo.SaveRecordDisplay(s.Col, [(headRef[1], false, 0), (tailRef[0], false, 0)]);

        var head = new List<int>();
        string? v = null; int? id = null;
        while (true)
        {
            var p = repo.GetRecordsSortedHead(s.Col, s.Text, true, true, 4, v, id, null, null);
            if (p.Records.Count == 0) break;
            head.AddRange(Ids(p.Records));
            v = p.SortValues![^1]; id = p.Records[^1].Id;
            if (p.Records.Count < 4) break;
        }
        Assert.Equal(headRef.Where(x => x != headRef[1]).ToList(), head);
        Assert.Equal(tailRef.Skip(1).ToList(), WalkTail(repo, s, 3, excludeHidden: true));
        Assert.Equal(headRef.Count - 1, repo.CountNonEmptyValues(s.Col, s.Text, true));
        // Смещение с ближнего края тоже уважает скрытые.
        var fromEnd = repo.GetRecordsSortedHead(s.Col, s.Text, true, true, 3, null, null, null, null,
                                                skip: headRef.Count - 4, headCount: headRef.Count - 1);
        Assert.Equal(headRef.Where(x => x != headRef[1]).Skip(headRef.Count - 4).Take(3).ToList(), Ids(fromEnd.Records));
    }

    // ── Порядок таблицы: смещение с конца ────────────────────────────────────

    [Fact]
    public void Records_page_offset_from_end_equals_forward()
    {
        using var db = new TempDatabase();
        var s    = Seed(db);
        var repo = db.Constructor;
        var all  = Ids(s.Recs);
        var n    = all.Count;

        foreach (var skip in new[] { 0, n / 2 - 1, n / 2 + 1, n - 5, n - 2 })
        {
            var expected = all.Skip(skip).Take(5).ToList();
            Assert.Equal(expected, Ids(repo.GetRecordsPage(s.Col, skip, 5)));
            Assert.Equal(expected, Ids(repo.GetRecordsPage(s.Col, skip, 5, total: n)));
        }
        Assert.Empty(repo.GetRecordsPage(s.Col, n, 5, total: n));
    }

    // ── Окно колонок ──────────────────────────────────────────────────────────

    [Fact]
    public void Field_values_batch_reads_only_window_fields()
    {
        using var db = new TempDatabase();
        var s    = Seed(db);
        var repo = db.Constructor;
        var ids  = Ids(s.Recs);

        var all    = repo.GetFieldValuesBatch(ids, null);
        var window = repo.GetFieldValuesBatch(ids, [s.Num]);
        var none   = repo.GetFieldValuesBatch(ids, []);

        Assert.Equal(all.Count, repo.GetFieldValuesBatch(ids).Count);
        Assert.All(window.Values, byField => Assert.Equal([s.Num], byField.Keys));
        Assert.Equal(s.NumOf.Count, window.Count);
        Assert.Equal(s.NumOf, window.ToDictionary(kv => kv.Key, kv => kv.Value[s.Num]));
        Assert.Empty(none);
        Assert.True(all.Values.Sum(v => v.Count) > window.Values.Sum(v => v.Count));
    }

    // ── Счетчик без ожидания ──────────────────────────────────────────────────

    [Fact]
    public void Estimate_matches_count_for_standard_positions_and_snapshot_marks_it()
    {
        using var db = new TempDatabase();
        var s    = Seed(db);
        var repo = db.Constructor;
        // Снимок открытия читает настройки вида коллекции из хранилища настроек программы —
        // подменяем его, как другие тесты, чтобы не зависеть от общего контекста системной базы.
        SmartBase.Utils.AppSettings.AttachStore(new FakeSettingsDatabase());

        Assert.Equal(s.Recs.Count, repo.EstimateRecordCount(s.Col));

        var deferred = repo.ReadOpenSnapshot(s.Col, deferCount: true);
        Assert.True(deferred.CountIsEstimate);
        Assert.Equal(s.Recs.Count, deferred.RecordCount);
        Assert.Equal(s.Recs.Count, deferred.VisibleRecordCount);

        var exact = repo.ReadOpenSnapshot(s.Col);
        Assert.False(exact.CountIsEstimate);
        Assert.Equal(s.Recs.Count, exact.RecordCount);

        // Пустая коллекция — ноль; мелкие позиции — хотя бы единица (край уточнят страницы).
        var empty = SampleData.AddCollection(repo, "Пустая").Id!.Value;
        Assert.Equal(0, repo.EstimateRecordCount(empty));
        var tiny = SampleData.AddCollection(repo, "Мелкие позиции").Id!.Value;
        SampleData.AddRecords(repo, tiny, count: 3, step: 10);
        Assert.Equal(1, repo.EstimateRecordCount(tiny));
    }

    [Fact]
    public async Task Count_async_honours_hidden_records()
    {
        using var db = new TempDatabase();
        var s    = Seed(db);
        var repo = db.Constructor;
        repo.SaveRecordDisplay(s.Col, [(s.Recs[0].Id!.Value, false, 0)]);

        Assert.Equal(s.Recs.Count,     await repo.CountRecordsAsync(s.Col));
        Assert.Equal(s.Recs.Count - 1, await repo.CountRecordsAsync(s.Col, excludeHidden: true));
    }

    // ── Список id в фоне: отбор и сортировка по типам ─────────────────────────

    [Fact]
    public async Task Filtered_ids_without_sort_follow_table_order()
    {
        using var db = new TempDatabase();
        var s    = Seed(db);
        var repo = db.Constructor;
        var filters = new Dictionary<string, HashSet<string>> { [s.Text.ToString()] = ["Москва", "Омск"] };

        var ids = await repo.GetFilteredRecordIdsAsync(s.Col, filters, false, null, true, null, CancellationToken.None);
        var expected = Ids(s.Recs.Where(r => s.TextOf.TryGetValue(r.Id!.Value, out var v) && v is "Москва" or "Омск"));
        Assert.Equal(expected, ids);
        Assert.Equal(repo.GetFilteredRecordIds(s.Col, filters), ids);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Filtered_ids_text_sort_matches_index_order_with_empty_last(bool asc)
    {
        using var db = new TempDatabase();
        var s    = Seed(db);
        var repo = db.Constructor;
        var filters = new Dictionary<string, HashSet<string>>
        {
            [s.Num.ToString()] = s.NumOf.Values.Distinct().Where(v => v != "abc").Append("").ToHashSet(),
        };
        var ids = await repo.GetFilteredRecordIdsAsync(s.Col, filters, false, s.Text.ToString(), asc, "text", CancellationToken.None);

        var scope = s.Recs.Where(r => !s.NumOf.TryGetValue(r.Id!.Value, out var nv) || nv != "abc").ToList();
        var head  = scope.Where(r => s.TextOf.ContainsKey(r.Id!.Value));
        var headOrdered = asc
            ? head.OrderBy(r => s.TextOf[r.Id!.Value], StringComparer.Ordinal).ThenBy(r => r.Position)
            : head.OrderByDescending(r => s.TextOf[r.Id!.Value], StringComparer.Ordinal).ThenBy(r => r.Position);
        var tail = scope.Where(r => !s.TextOf.ContainsKey(r.Id!.Value)).OrderBy(r => r.Position);
        Assert.Equal(Ids(headOrdered.Concat(tail)), ids);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Number_sort_is_numeric_with_junk_and_empty_last_in_table_order(bool withFilter)
    {
        using var db = new TempDatabase();
        var s    = Seed(db);
        var repo = db.Constructor;
        var filters = withFilter
            ? new Dictionary<string, HashSet<string>> { [s.Text.ToString()] = Cities.Append("").ToHashSet() }   // все записи
            : new Dictionary<string, HashSet<string>>();

        var asc  = await repo.GetFilteredRecordIdsAsync(s.Col, filters, false, s.Num.ToString(), true,  "number", CancellationToken.None);
        var desc = await repo.GetFilteredRecordIdsAsync(s.Col, filters, false, s.Num.ToString(), false, "number", CancellationToken.None);

        var numeric = s.Recs.Where(r => s.NumOf.TryGetValue(r.Id!.Value, out var v) && v != "abc")
                            .Select(r => (Id: r.Id!.Value, Key: ColumnSort.NumberKey(s.NumOf[r.Id!.Value]), r.Position)).ToList();
        var rest = Ids(s.Recs.Where(r => !s.NumOf.TryGetValue(r.Id!.Value, out var v) || v == "abc").OrderBy(r => r.Position));

        Assert.Equal(numeric.OrderBy(x => x.Key).ThenBy(x => x.Position).Select(x => x.Id).Concat(rest).ToList(), asc);
        Assert.Equal(numeric.OrderByDescending(x => x.Key).ThenBy(x => x.Position).Select(x => x.Id).Concat(rest).ToList(), desc);
        Assert.Equal(s.Recs.Count, asc.Count);
    }

    [Fact]
    public async Task Title_sort_uses_record_column()
    {
        using var db = new TempDatabase();
        var s    = Seed(db);
        var repo = db.Constructor;
        var none = new Dictionary<string, HashSet<string>>();

        var desc = await repo.GetFilteredRecordIdsAsync(s.Col, none, false, TableRowVm.TitleKey, false, "text", CancellationToken.None);
        Assert.Equal(Ids(s.Recs.OrderByDescending(r => r.Title, StringComparer.Ordinal)), desc);
    }

    [Fact]
    public async Task Date_sort_in_id_list_is_chronological()
    {
        using var db = new TempDatabase();
        var s    = Seed(db);
        var repo = db.Constructor;
        var filters = new Dictionary<string, HashSet<string>> { [s.Text.ToString()] = Cities.Append("").ToHashSet() };

        var ids = await repo.GetFilteredRecordIdsAsync(s.Col, filters, false, s.Date.ToString(), true, "date", CancellationToken.None);
        var dates = repo.GetColumnValues(s.Col, s.Date.ToString());
        var withDate = s.Recs.Where(r => dates.ContainsKey(r.Id!.Value))
                             .OrderBy(r => DateValue.Parse(dates[r.Id!.Value])!.Value.Start).ThenBy(r => r.Position);
        var without  = s.Recs.Where(r => !dates.ContainsKey(r.Id!.Value)).OrderBy(r => r.Position);
        Assert.Equal(Ids(withDate.Concat(without)), ids);
    }

    [Fact]
    public async Task Cancelled_query_throws_and_returns_nothing()
    {
        using var db = new TempDatabase();
        var s    = Seed(db);
        var cts  = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            db.Constructor.GetFilteredRecordIdsAsync(s.Col, new Dictionary<string, HashSet<string>>(), false,
                                                     s.Num.ToString(), true, "number", cts.Token));
    }

    // ── Планировщик источника ────────────────────────────────────────────────

    [Fact]
    public async Task Planner_picks_plain_index_or_id_list()
    {
        using var db = new TempDatabase();
        var s      = Seed(db);
        var repo   = db.Constructor;
        var fields = repo.GetFields(s.Col);
        var none   = new Dictionary<string, HashSet<string>>();
        var ct     = CancellationToken.None;

        Assert.IsType<PlainRowSource>(await RowSourcePlanner.BuildAsync(repo, s.Col, fields, none, null, true, false, ct));

        var text = await RowSourcePlanner.BuildAsync(repo, s.Col, fields, none, s.Text.ToString(), false, false, ct);
        Assert.Equal(new SortedRowSource(s.Text, false), text);

        var date = await RowSourcePlanner.BuildAsync(repo, s.Col, fields, none, s.Date.ToString(), true, false, ct);
        Assert.IsType<SortedRowSource>(date);

        var num = await RowSourcePlanner.BuildAsync(repo, s.Col, fields, none, s.Num.ToString(), true, false, ct);
        Assert.IsType<IdListRowSource>(num);
        Assert.Equal(s.Recs.Count, ((IdListRowSource)num).Ids.Count);

        var filtered = await RowSourcePlanner.BuildAsync(repo, s.Col, fields,
            new Dictionary<string, HashSet<string>> { [s.Text.ToString()] = ["Ялта"] }, s.Text.ToString(), true, false, ct);
        Assert.IsType<IdListRowSource>(filtered);
        Assert.Equal(Ids(s.Recs.Where(r => s.TextOf.GetValueOrDefault(r.Id!.Value) == "Ялта")), ((IdListRowSource)filtered).Ids);

        Assert.IsType<IdListRowSource>(await RowSourcePlanner.BuildAsync(repo, s.Col, fields, none, TableRowVm.TitleKey, true, false, ct));
    }

    // ── Порция строк выделения на новом выборе записей ───────────────────────

    [Fact]
    public async Task Cell_rows_carry_sort_value_for_sorted_cursor()
    {
        using var db = new TempDatabase();
        var s    = Seed(db);
        var repo = db.Constructor;
        var reference = HeadReference(s, true);

        var rows = await repo.ReadCellRowsAsync(s.Col,
            r => r.GetRecordsSortedHead(s.Col, s.Text, true, false, 6, null, null, null, null), [s.Text], CancellationToken.None);
        Assert.Equal(reference.Take(6).ToList(), rows.Select(r => r.RecordId).ToList());
        Assert.All(rows, r => Assert.Equal(s.TextOf[r.RecordId], r.SortValue));
        Assert.All(rows, r => Assert.Equal(s.TextOf[r.RecordId], r.Values[s.Text]));
    }
}
