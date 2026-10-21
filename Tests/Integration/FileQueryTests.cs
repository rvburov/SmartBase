using System.Text;
using System.Text.Json;
using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>Файловый фильтр работает с полными путями внутри значения, а сортировка — с видимыми
/// именами. Проверяем настоящий SQL обоих провайдеров, включая страницы и ограниченный чеклист.</summary>
public sealed class FileQueryTests
{
    private const string A = "/отдел/Счет, приложение.pdf";
    private const string B = "/архив/Счет, приложение.pdf";
    private const string C = "C:\\Папка\\O'Брайен \"архив\".txt";
    private const string SameName = "C:\\Другая\\Счет, приложение.pdf";
    private const string Supplementary = "/данные/😀.pdf";
    private const string PrivateUse = "/данные/\uE000.pdf";
    private const string InvalidArray = "{\"paths\":[\"сломано\",7]}";
    private const string InvalidJson = "{\"paths\":[\"незакрыто";
    private const string Quoted = "\"цитата, имя.pdf\"";

    private sealed class Fixture : IDisposable
    {
        private readonly TempDatabase? _sqlite;
        public DatabaseManager Manager { get; }
        public ConstructorRepository Repo => Manager.Constructor;
        public int Collection { get; }
        public int File { get; }
        public int Group { get; }
        public string Key => File.ToString();

        public Fixture(bool postgres)
        {
            if (postgres)
            {
                Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL (порт 5455) не поднят");
                var connectionString = PgTestServer.ConnectionString(PgTestServer.Database1);
                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
                    command.ExecuteNonQuery();
                }
                Manager = DatabaseManager.Open(101, "Файловые запросы PG", connectionString, provider: "postgres");
            }
            else
            {
                _sqlite = new TempDatabase();
                Manager = _sqlite.Manager;
            }
            Collection = SampleData.AddCollection(Repo, "Файлы").Id!.Value;
            File = SampleData.AddField(Repo, Collection, "Файл", "file").Id!.Value;
            Group = SampleData.AddField(Repo, Collection, "Группа").Id!.Value;
        }

        public void Dispose()
        {
            if (_sqlite is not null) _sqlite.Dispose();
            else Manager.Dispose();
        }
    }

    private sealed record Row(int Id, int Position, string Raw, string Group);

    private static List<Row> Seed(Fixture f)
    {
        (string Raw, string Group, int Position)[] values =
        [
            (A, "оставить", 90),
            (FileValue.Serialize([A, B]), "оставить", 20),
            // ReadPaths сохраняет повторы в старом/вручную записанном JSON. Счетчик — число записей.
            (JsonSerializer.Serialize(new { paths = new[] { A, A, C } }), "оставить", 100),
            (B, "убрать", 10),
            ("", "оставить", 60),
            ("{\"paths\":[]}", "оставить", 30),
            ("{\"paths\":[\"\",\"\"]}", "убрать", 30),
            (InvalidArray, "оставить", 50),
            (InvalidJson, "оставить", 80),
            (Quoted, "оставить", 70),
            (C, "убрать", 110),
            (FileValue.Serialize([B, A, Supplementary]), "оставить", 120),
            (SameName, "оставить", 15),
            (PrivateUse, "оставить", 140),
            (Supplementary, "убрать", 5),
        ];
        var rows = new List<Row>();
        foreach (var value in values)
        {
            var id = SampleData.AddRecord(f.Repo, f.Collection, $"Запись {rows.Count}", value.Position).Id!.Value;
            if (value.Raw.Length > 0) f.Repo.SetFieldValue(id, f.File, value.Raw);
            f.Repo.SetFieldValue(id, f.Group, value.Group);
            rows.Add(new(id, value.Position, value.Raw, value.Group));
        }
        f.Repo.SaveRecordDisplay(f.Collection, [(rows[3].Id, false, 0), (rows[11].Id, false, 1)]);
        return rows;
    }

    private static Dictionary<string, HashSet<string>> Filter(Fixture f, params string[] paths) =>
        new() { [f.Key] = paths.ToHashSet(StringComparer.Ordinal) };

    private static int[] InTableOrder(IEnumerable<Row> rows) =>
        rows.OrderBy(r => r.Position).ThenBy(r => r.Id).Select(r => r.Id).ToArray();

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paths_are_individual_exact_options_and_counts_measure_containing_records(bool postgres)
    {
        using var f = new Fixture(postgres);
        var rows = Seed(f);
        var repo = f.Repo;

        Assert.Equal(InTableOrder([rows[0], rows[1], rows[2], rows[11]]),
            repo.GetFilteredRecordIds(f.Collection, Filter(f, A)));
        var either = repo.GetFilteredRecordIds(f.Collection, Filter(f, A, B));
        Assert.Equal(InTableOrder([rows[0], rows[1], rows[2], rows[3], rows[11]]), either);
        Assert.Equal(either.Count, either.Distinct().Count());
        Assert.Equal(InTableOrder([rows[2], rows[10]]), repo.GetFilteredRecordIds(f.Collection, Filter(f, C)));
        Assert.Equal([rows[12].Id], repo.GetFilteredRecordIds(f.Collection, Filter(f, SameName)));
        Assert.Empty(repo.GetFilteredRecordIds(f.Collection, Filter(f, "Счет, приложение.pdf")));
        Assert.Empty(repo.GetFilteredRecordIds(f.Collection, Filter(f, "/отдел/Счет")));
        Assert.Equal([rows[7].Id], repo.GetFilteredRecordIds(f.Collection, Filter(f, InvalidArray)));
        Assert.Equal([rows[8].Id], repo.GetFilteredRecordIds(f.Collection, Filter(f, InvalidJson)));
        Assert.Equal([rows[9].Id], repo.GetFilteredRecordIds(f.Collection, Filter(f, Quoted)));
        Assert.Equal(InTableOrder([rows[4], rows[5], rows[6]]),
            repo.GetFilteredRecordIds(f.Collection, Filter(f, "")));

        Assert.Equal(InTableOrder(rows.Where(r => !new[] { rows[0].Id, rows[1].Id, rows[2].Id, rows[11].Id }.Contains(r.Id))),
            repo.GetFilteredRecordIds(f.Collection, new Dictionary<string, HashSet<string>>
                { [f.Key] = FilterExclusion.Exclude([A]) }));
        Assert.Equal(InTableOrder(rows.Where(r => r.Id != rows[4].Id && r.Id != rows[5].Id && r.Id != rows[6].Id)),
            repo.GetFilteredRecordIds(f.Collection, new Dictionary<string, HashSet<string>>
                { [f.Key] = FilterExclusion.Exclude([""]) }));

        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [A] = 4, [B] = 3, [C] = 2, [SameName] = 1, [Supplementary] = 2,
            [PrivateUse] = 1, [InvalidArray] = 1, [InvalidJson] = 1, [Quoted] = 1, [""] = 3,
        };
        AssertCounts(expected, repo.GetColumnCounts(f.Collection, f.Key));
        AssertCounts(expected, repo.GetFieldValueCounts(f.Collection, f.File));
        AssertCounts(expected, await repo.GetColumnCountsAsync(f.Collection, f.Key));
        var list = await repo.GetColumnValueListAsync(f.Collection, f.Key);
        Assert.False(list.Capped);
        AssertCounts(expected, list.Values);
        Assert.Equal((9L, 3), await repo.GetColumnValueTotalsAsync(f.Collection, f.Key));

        // Свой фильтр списка игнорируется; чужой и скрытые записи одновременно сужают счетчики.
        var scoped = Filter(f, C);
        scoped[f.Group.ToString()] = ["оставить"];
        var scopedExpected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [A] = 3, [B] = 1, [C] = 1, [SameName] = 1, [PrivateUse] = 1,
            [InvalidArray] = 1, [InvalidJson] = 1, [Quoted] = 1, [""] = 2,
        };
        AssertCounts(scopedExpected, repo.GetColumnCounts(f.Collection, f.Key, scoped, excludeHidden: true));
        AssertCounts(scopedExpected, (await repo.GetColumnValueListAsync(f.Collection, f.Key, scoped, excludeHidden: true)).Values);
        Assert.Equal((8L, 2), await repo.GetColumnValueTotalsAsync(f.Collection, f.Key, scoped, excludeHidden: true));
        Assert.Equal([rows[2].Id], repo.GetFilteredRecordIds(f.Collection, scoped, excludeHidden: true));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Saved_raw_group_filters_keep_exact_membership_without_expanding_their_paths(bool postgres)
    {
        using var f = new Fixture(postgres);
        var rows = Seed(f);
        var group = rows[1].Raw;
        Assert.Equal([rows[1].Id], f.Repo.GetFilteredRecordIds(f.Collection, Filter(f, group)));
        Assert.Equal(InTableOrder(rows.Where(r => r.Id != rows[1].Id)),
            f.Repo.GetFilteredRecordIds(f.Collection, new Dictionary<string, HashSet<string>>
                { [f.Key] = FilterExclusion.Exclude([group]) }));

        // Совпадение содержимого JSON недостаточно: раньше ключ сравнивался целиком.
        var reformatted = JsonSerializer.Serialize(new { paths = new[] { A, B } },
            new JsonSerializerOptions { WriteIndented = true });
        Assert.NotEqual(group, reformatted);
        Assert.Empty(f.Repo.GetFilteredRecordIds(f.Collection, Filter(f, reformatted)));

        // Новый путь и старый групповой ключ могут одновременно оставаться в сохраненном наборе.
        Assert.Equal(InTableOrder([rows[1], rows[2], rows[10]]),
            f.Repo.GetFilteredRecordIds(f.Collection, Filter(f, group, C)));
        Assert.Equal(InTableOrder(rows.Where(r => r.Id != rows[1].Id && r.Id != rows[2].Id && r.Id != rows[10].Id)),
            f.Repo.GetFilteredRecordIds(f.Collection, new Dictionary<string, HashSet<string>>
                { [f.Key] = FilterExclusion.Exclude([group, C]) }));
        Assert.DoesNotContain(f.Repo.GetColumnCounts(f.Collection, f.Key), v => v.Value == group);
    }

    private static void AssertCounts(Dictionary<string, int> expected, List<(string Value, int Count)> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(actual.Count, actual.Select(v => v.Value).Distinct(StringComparer.Ordinal).Count());
        foreach (var value in actual)
        {
            Assert.True(expected.TryGetValue(value.Value, out var count), $"Лишний вариант: {value.Value}");
            Assert.Equal(count, value.Count);
        }
    }

    // Порядок SQLite BINARY / PostgreSQL C — байты UTF-8; UTF-16 Ordinal иначе упорядочит 😀 и U+E000.
    private static readonly IComparer<string> Utf8 = Comparer<string>.Create((a, b) =>
        Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b)));

    private static List<Row> Head(IEnumerable<Row> rows, bool asc)
    {
        var nonempty = rows.Where(r => FileValue.ReadPaths(r.Raw).Count > 0);
        return asc
            ? nonempty.OrderBy(r => FileValue.DisplayName(r.Raw), Utf8).ThenBy(r => r.Id).ToList()
            : nonempty.OrderByDescending(r => FileValue.DisplayName(r.Raw), Utf8).ThenByDescending(r => r.Id).ToList();
    }

    private static List<Row> Tail(IEnumerable<Row> rows) => rows.Where(r => FileValue.ReadPaths(r.Raw).Count == 0)
        .OrderBy(r => r.Position).ThenBy(r => r.Id).ToList();

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Display_name_order_is_shared_by_keyset_offset_rank_and_filtered_sort(bool postgres)
    {
        using var f = new Fixture(postgres);
        var all = Seed(f);
        foreach (var excludeHidden in new[] { false, true })
        foreach (var asc in new[] { false, true })
        {
            var shown = all.Where(r => !excludeHidden || r.Id != all[3].Id && r.Id != all[11].Id).ToList();
            var head = Head(shown, asc);
            var tail = Tail(shown);
            var repo = f.Repo;
            Assert.Equal(head.Count, repo.CountNonEmptyValues(f.Collection, f.File, excludeHidden));
            Assert.Equal(head.Count, await repo.CountNonEmptyValuesAsync(f.Collection, f.File, excludeHidden));

            var walked = new List<int>();
            string? after = null;
            int? afterId = null;
            // Верхняя граница превращает повтор курсора в конечный провал теста вместо зависания.
            for (var n = 0; n <= all.Count; n++)
            {
                var page = repo.GetRecordsSortedHead(f.Collection, f.File, asc, excludeHidden, 3, after, afterId, null, null);
                if (page.Records.Count == 0) break;
                var ids = page.Records.Select(r => r.Id!.Value).ToList();
                Assert.Equal(ids.Select(id => FileValue.DisplayName(all.Single(r => r.Id == id).Raw)), page.SortValues);
                walked.AddRange(ids);
                after = page.SortValues![^1];
                afterId = ids[^1];
            }
            Assert.Equal(head.Select(r => r.Id), walked);

            var last = repo.GetRecordsSortedHead(f.Collection, f.File, asc, excludeHidden, 3, null, null, null, null,
                skip: Math.Max(0, head.Count - 3), headCount: head.Count);
            var backwards = last.Records.Select(r => r.Id!.Value).ToList();
            for (var n = 0; n <= all.Count && last.Records.Count > 0; n++)
            {
                last = repo.GetRecordsSortedHead(f.Collection, f.File, asc, excludeHidden, 3, null, null,
                    last.SortValues![0], last.Records[0].Id);
                backwards.InsertRange(0, last.Records.Select(r => r.Id!.Value));
            }
            Assert.Equal(head.Select(r => r.Id), backwards);

            foreach (var skip in new[] { 0, 1, head.Count / 2, head.Count - 2, head.Count, head.Count + 2 }.Distinct())
            foreach (int? knownHead in new int?[] { null, head.Count })
            {
                var page = repo.GetRecordsSortedHead(f.Collection, f.File, asc, excludeHidden, 3, null, null, null, null,
                    skip: skip, headCount: knownHead);
                Assert.Equal(head.Skip(skip).Take(3).Select(r => r.Id), page.Records.Select(r => r.Id!.Value));
            }

            var tailWalk = new List<int>();
            int? afterPosition = null;
            afterId = null;
            for (var n = 0; n <= all.Count; n++)
            {
                var page = repo.GetRecordsWithoutValue(f.Collection, f.File, excludeHidden, 2,
                    afterPosition, afterId, null, null);
                if (page.Count == 0) break;
                tailWalk.AddRange(page.Select(r => r.Id!.Value));
                afterPosition = page[^1].Position;
                afterId = page[^1].Id;
            }
            Assert.Equal(tail.Select(r => r.Id), tailWalk);
            var tailBefore = repo.GetRecordsWithoutValue(f.Collection, f.File, excludeHidden, 2,
                null, null, tail[^1].Position, tail[^1].Id);
            Assert.Equal(tail.Skip(Math.Max(0, tail.Count - 3)).Take(2).Select(r => r.Id), tailBefore.Select(r => r.Id!.Value));
            foreach (var skip in Enumerable.Range(0, tail.Count + 1))
            {
                var page = repo.GetRecordsWithoutValue(f.Collection, f.File, excludeHidden, 2, null, null, null, null,
                    skip: skip, tailCount: tail.Count, headCount: head.Count);
                Assert.Equal(tail.Skip(skip).Take(2).Select(r => r.Id), page.Select(r => r.Id!.Value));
            }

            var order = head.Concat(tail).Select(r => r.Id).ToList();
            for (var i = 0; i < order.Count; i++)
                Assert.Equal(i, repo.RankInSortedView(f.Collection, f.File, asc, excludeHidden, order[i], head.Count));
            if (excludeHidden)
                Assert.Null(repo.RankInSortedView(f.Collection, f.File, asc, true, all[11].Id, head.Count));

            var filters = new Dictionary<string, HashSet<string>> { [f.Group.ToString()] = ["оставить"] };
            var subset = shown.Where(r => r.Group == "оставить").ToList();
            Assert.Equal(Head(subset, asc).Concat(Tail(subset)).Select(r => r.Id),
                await repo.GetFilteredRecordIdsAsync(f.Collection, filters, excludeHidden, f.Key, asc, "file", CancellationToken.None));
            filters[f.Key] = [A, B];
            subset = subset.Where(r => new[] { all[0].Id, all[1].Id, all[2].Id, all[11].Id }.Contains(r.Id)).ToList();
            Assert.Equal(Head(subset, asc).Select(r => r.Id),
                await repo.GetFilteredRecordIdsAsync(f.Collection, filters, excludeHidden, f.Key, asc, "file", CancellationToken.None));
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capped_checklist_counts_paths_and_searches_decoded_paths_without_json_metadata(bool postgres)
    {
        using var f = new Fixture(postgres);
        var count = ConstructorRepository.FilterValueCap + 3;
        const string common = "/0 общий/Общий.pdf";
        string Unique(int i) => $"/каталог/Группа{i:D4}/Отчет.pdf";
        var values = Enumerable.Range(0, count)
            .Select(i => new BulkRecordRow($"Запись {i}", [FileValue.Serialize([Unique(i), common]), i % 2 == 0 ? "оставить" : "убрать"]))
            .Concat([new("Без значения", ["", "оставить"]), new("Пустой список", ["{\"paths\":[]}", "оставить"]),
                new("Пустые элементы", ["{\"paths\":[\"\",\"\"]}", "убрать"])])
            .ToList();
        var ids = f.Repo.BulkAddRecords(f.Collection, [f.File, f.Group], values, values.Count, CancellationToken.None);
        var page = await f.Repo.GetColumnValueListAsync(f.Collection, f.Key);
        Assert.True(page.Capped);
        Assert.True(page.More);
        Assert.Equal(ConstructorRepository.FilterValueCap, page.Values.Count);
        Assert.Equal(count, Assert.Single(page.Values, v => v.Value == common).Count);
        Assert.All(page.Values, v => Assert.True(v.Value == common || v.Value.StartsWith("/каталог/", StringComparison.Ordinal)));
        Assert.Equal((count + 1L, 3), await f.Repo.GetColumnValueTotalsAsync(f.Collection, f.Key));

        var folder = await f.Repo.GetColumnValueListAsync(f.Collection, f.Key, search: $"гРуПпА{count - 1:D4}");
        Assert.True(folder.Capped);
        Assert.False(folder.More);
        Assert.Equal([(Unique(count - 1), 1)], folder.Values);
        var name = await f.Repo.GetColumnValueListAsync(f.Collection, f.Key, search: "отЧет.PDF");
        Assert.True(name.More);
        Assert.Equal(ConstructorRepository.FilterValueCap, name.Values.Count);
        Assert.All(name.Values, v => Assert.Equal(1, v.Count));
        Assert.Empty((await f.Repo.GetColumnValueListAsync(f.Collection, f.Key, search: "paths")).Values);
        Assert.Empty((await f.Repo.GetColumnValueListAsync(f.Collection, f.Key, search: "\\u")).Values);

        f.Repo.SaveRecordDisplay(f.Collection, [(ids[0], false, 0)]);
        var filters = Filter(f, Unique(1)); // свой отбор не сужает варианты
        filters[f.Group.ToString()] = ["оставить"];
        var narrowed = await f.Repo.GetColumnValueListAsync(f.Collection, f.Key, filters, excludeHidden: true);
        var visibleEven = (count + 1) / 2 - 1;
        Assert.True(narrowed.Capped); // порог режима относится ко всей колонке
        Assert.False(narrowed.More);
        Assert.Equal(visibleEven, Assert.Single(narrowed.Values, v => v.Value == common).Count);
        Assert.DoesNotContain(narrowed.Values, v => v.Value == Unique(0) || v.Value == Unique(1));
        Assert.Equal((visibleEven + 1L, 2),
            await f.Repo.GetColumnValueTotalsAsync(f.Collection, f.Key, filters, excludeHidden: true));
    }
}
