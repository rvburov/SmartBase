using System.Text.Json;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>Чтение HTTP API: состав полей, курсоры, дата изменения и история без авторизации.</summary>
[Collection(EgressCollection.Name)]
public sealed class IngressReadTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDatabase _db = new(dbId: 7, name: "Основная");

    public IngressReadTests() => RunJournal.Attach(null);
    public void Dispose() { RunJournal.Attach(null); _db.Dispose(); }

    private (int Id, int Number, int Total) Orders()
    {
        var collection = SampleData.AddCollection(_db.Constructor, "Заказы");
        var number     = SampleData.AddField(_db.Constructor, collection.Id!.Value, "Номер");
        var total      = SampleData.AddField(_db.Constructor, collection.Id!.Value, "Сумма",
                                             position: 2000);

        var first  = SampleData.AddRecord(_db.Constructor, collection.Id!.Value, "Молоток");
        var second = SampleData.AddRecord(_db.Constructor, collection.Id!.Value, "Гвозди",
                                          position: 2000);

        _db.Constructor.SetFieldValue(first.Id!.Value,  number.Id!.Value, "А-1");
        _db.Constructor.SetFieldValue(first.Id!.Value,  total.Id!.Value,  "100");
        _db.Constructor.SetFieldValue(second.Id!.Value, number.Id!.Value, "А-2");
        _db.Constructor.SetFieldValue(second.Id!.Value, total.Id!.Value,  "250");

        return (collection.Id!.Value, number.Id!.Value, total.Id!.Value);
    }

    private IngressAnswer Get(string path, string query) => IngressHandler.Handle(
        new IngressCall("GET", path, "", "127.0.0.1", query), [_db.Manager], null, Now);

    private IngressAnswer Read(string query = "collection=Заказы") => Get(IngressApi.RecordsPath, query);
    private static JsonElement Body(IngressAnswer answer) => JsonDocument.Parse(answer.Body).RootElement;

    private static IEnumerable<string> Values(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p => Values(p.Value)),
        JsonValueKind.Array  => element.EnumerateArray().SelectMany(Values),
        JsonValueKind.String => new[] { element.GetString()! },
        JsonValueKind.Number => new[] { element.GetRawText() },
        _                    => Array.Empty<string>(),
    };
    [Fact]
    public void Only_the_selected_fields_are_returned()
    {
        var (id, number, _) = Orders();


        var answer = Read($"collection=Заказы&fields=t,{number}");

        Assert.Equal(200, answer.Status);

        var rows = Body(answer).GetProperty("rows");
        Assert.Equal(2, rows.GetArrayLength());

        var values = rows[0].GetProperty("values");

        Assert.Equal("Молоток", values.GetProperty("Название").GetString());
        Assert.Equal("А-1",     values.GetProperty("Номер").GetString());

        // «Сумма» в выборе не названа — ее в ответе нет вовсе, ни у одной записи: ни имени поля,
        // ни значений ни под каким именем.
        Assert.DoesNotContain("Сумма", answer.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(Values(Body(answer)), value => value is "100" or "250");
    }

    [Fact]
    public void The_cursor_pages_by_key()
    {
        var (id, number, _) = Orders();


        var first = Read("collection=Заказы&limit=1");

        Assert.Equal(200, first.Status);
        Assert.Equal(1, Body(first).GetProperty("rows").GetArrayLength());

        var next = Body(first).GetProperty("next").GetInt32();

        var second = Read($"collection=Заказы&limit=1&after={next}");

        Assert.Equal(200, second.Status);
        Assert.Equal(1, Body(second).GetProperty("rows").GetArrayLength());

        // Страницы не пересекаются: вторая начинается ЗА ключом первой.
        Assert.True(Body(second).GetProperty("rows")[0].GetProperty("id").GetInt32() > next);
    }

    [Fact]
    public void Since_narrows_to_records_touched_after_the_moment()
    {
        var (id, number, _) = Orders();


        // Момент в далеком будущем — не правился никто; в далеком прошлом — правились все.
        Assert.Equal(0, Body(Read("collection=Заказы&since=2199-01-01T00:00:00Z"))
                            .GetProperty("rows").GetArrayLength());
        Assert.Equal(2, Body(Read("collection=Заказы&since=2000-01-01T00:00:00Z"))
                            .GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public void Garbage_parameters_are_named_not_guessed()
    {
        var (id, number, _) = Orders();


        Assert.Equal(400, Read("collection=Заказы&since=вчера").Status);
        Assert.Equal(400, Read("collection=Заказы&after=-5").Status);
        Assert.Equal(400, Read("").Status);
        Assert.Equal(404, Read("collection=Накладные").Status);
    }

    [Fact]
    public void Changes_answer_deletions_by_name()
    {
        var (id, number, _) = Orders();


        var doomed = _db.Constructor.GetRecords(id).First(r => r.Title == "Гвозди");
        _db.Constructor.DeleteRecord(doomed.Id!.Value);

        var answer = Get(IngressApi.ChangesPath, "collection=Заказы");

        Assert.Equal(200, answer.Status);

        var changes = Body(answer).GetProperty("changes").EnumerateArray().ToList();

        Assert.Contains(changes, c => c.GetProperty("action").GetString() == "delete"
                                   && c.GetProperty("name").GetString() == "Гвозди");
    }

    [Fact]
    public void Without_a_field_selection_all_columns_are_read_without_a_registry_or_journal()
    {
        Orders();
        var answer = Read();
        Assert.Equal(200, answer.Status);
        var values = Body(answer).GetProperty("rows")[0].GetProperty("values");
        Assert.Equal("Молоток", values.GetProperty("Название").GetString());
        Assert.Equal("А-1", values.GetProperty("Номер").GetString());
        Assert.Equal("100", values.GetProperty("Сумма").GetString());
    }

    [Fact]
    public void Fields_can_be_selected_by_live_names_without_reading_other_columns()
    {
        Orders();
        var answer = Read("collection=Заказы&fields=Номер");
        Assert.Equal(200, answer.Status);
        var values = Body(answer).GetProperty("rows")[0].GetProperty("values");
        Assert.Single(values.EnumerateObject());
        Assert.Equal("А-1", values.GetProperty("Номер").GetString());
    }

    [Theory]
    [InlineData("collection=Заказы&limit=-1")]
    [InlineData("collection=Заказы&limit=много")]
    [InlineData("collection=Заказы&after=2147483648")]
    [InlineData("collection=Заказы&collection=Другая")]
    [InlineData("collection=Заказы&fields=нет")]
    public void Invalid_read_parameters_do_not_expand_or_guess_the_selection(string query)
    {
        Orders();
        Assert.Equal(400, Read(query).Status);
    }

    [Fact]
    public void Large_valid_page_limit_is_clamped_without_overflow()
    {
        Orders();
        var answer = Read("collection=Заказы&limit=9223372036854775807");
        Assert.Equal(200, answer.Status);
        Assert.Equal(2, Body(answer).GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public void Historical_security_metadata_no_longer_restricts_api_reading()
    {
        var (id, number, _) = Orders();
        _db.Context.DbMeta.Add(new DbMetaEntity { Key = "confidentiality", Value = "closed" });
        _db.Context.DbMeta.Add(new DbMetaEntity { Key = "password.hash", Value = "старый-хэш" });
        var user = _db.Context.DbUsers.First();
        user.IsApproved = false;
        user.IsAdmin = false;
        _db.Context.DbPermissions.Add(new DbPermissionEntity { UserId = user.Id, ActionsMask = 0 });
        _db.Context.DbFields.Find(number)!.ConfigJson = """{"confidentiality":"restricted"}""";
        _db.Context.SaveChanges();

        var answer = Read();
        Assert.Equal(200, answer.Status);
        Assert.Equal(2, Body(answer).GetProperty("rows").GetArrayLength());
        Assert.Contains("А-1", answer.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_field_selection_keeps_record_keys_and_does_not_compute_hidden_periods()
    {
        var (id, _, _) = Orders();
        var start = SampleData.AddField(_db.Constructor, id, "Начало", "date").Id!.Value;
        _db.Constructor.AddField(new DbField
        {
            CollectionId = id, Name = "Период", FieldType = "date_period", IsSystem = true,
            ConfigJson = new DatePeriodConfig(start).ToConfigJson(),
        });
        using var commands = new DatePeriodScalingTests.Commands(_db.Path);
        var page = _db.Constructor.ApiReadIsolated(id, [], "", 0, 10);
        Assert.Null(page.Trouble);
        Assert.Empty(page.Columns);
        Assert.Equal(2, page.Rows.Count);
        Assert.All(page.Rows, r => Assert.Empty(r.Values));
        commands.AssertNoAdditionalValueReads();
    }
    [Fact]
    public void Api_period_reads_the_selected_date_range_dependency_without_returning_it()
    {
        var (id, _, _) = Orders();
        var start = SampleData.AddField(_db.Constructor, id, "Начало", "date").Id!.Value;
        var period = _db.Constructor.AddField(new DbField
        {
            CollectionId = id, Name = "Период", FieldType = "date_period", IsSystem = true,
            ConfigJson = new DatePeriodConfig(start).ToConfigJson(),
        }).Id!.Value;
        var ids = _db.Constructor.GetRecords(id).Select(record => record.Id!.Value).ToArray();
        _db.Constructor.SetFieldValue(ids[0], start, "2026-10-28T07:00/2026-10-28T09:00");
        _db.Constructor.SetFieldValue(ids[1], start, "2026-10-28T08:00/2026-10-28T11:00");
        var page = _db.Constructor.ApiReadIsolated(id, [period.ToString()], "", 0, 1);
        Assert.Null(page.Trouble);
        Assert.True(page.More);
        Assert.Single(page.Columns);
        Assert.Equal("7200", Assert.Single(Assert.Single(page.Rows).Values));
        var next = _db.Constructor.ApiReadIsolated(id, [period.ToString()], "", Assert.Single(page.Rows).Id, 1);
        Assert.Null(next.Trouble);
        Assert.False(next.More);
        Assert.Single(next.Columns);
        Assert.Equal("10800", Assert.Single(Assert.Single(next.Rows).Values));
    }

    [Fact]
    public void An_ambiguous_collection_name_is_rejected_and_can_be_selected_by_its_key()
    {
        var firstGroup = _db.Constructor.AddGroup(new DbGroup { Name = "Первая" }).Id!.Value;
        var secondGroup = _db.Constructor.AddGroup(new DbGroup { Name = "Вторая" }).Id!.Value;
        var first = SampleData.AddCollection(_db.Constructor, "Совпадение", firstGroup).Id!.Value;
        var second = SampleData.AddCollection(_db.Constructor, "Совпадение", secondGroup).Id!.Value;
        SampleData.AddRecord(_db.Constructor, first, "Первая запись");
        SampleData.AddRecord(_db.Constructor, second, "Вторая запись");

        var ambiguous = Read("collection=Совпадение");
        Assert.Equal(400, ambiguous.Status);
        Assert.Contains("collectionId", ambiguous.Body, StringComparison.Ordinal);
        var selected = Read($"collectionId={second}");
        Assert.Equal(200, selected.Status);
        Assert.Equal(second, Body(selected).GetProperty("collectionId").GetInt32());
        Assert.Equal("Вторая запись", Assert.Single(Body(selected).GetProperty("rows").EnumerateArray())
            .GetProperty("values").GetProperty("Название").GetString());
        Assert.DoesNotContain("Первая запись", selected.Body, StringComparison.Ordinal);

        var schema = Body(Get(IngressApi.SchemaPath, "")).GetProperty("collections");
        Assert.Equal(new[] { first, second }.Order(), schema.EnumerateArray()
            .Where(c => c.GetProperty("name").GetString() == "Совпадение")
            .Select(c => c.GetProperty("id").GetInt32()).Order());
    }

    [Fact]
    public void Numeric_collection_names_are_names_and_never_implicit_keys()
    {
        var (original, _, _) = Orders();
        var numeric = SampleData.AddCollection(_db.Constructor, original.ToString()).Id!.Value;
        SampleData.AddRecord(_db.Constructor, numeric, "Числовое имя");
        var byName = Read($"collection={original}");
        Assert.Equal(200, byName.Status);
        Assert.Equal(numeric, Body(byName).GetProperty("collectionId").GetInt32());
        Assert.Equal("Числовое имя", Assert.Single(Body(byName).GetProperty("rows").EnumerateArray())
            .GetProperty("values").GetProperty("Название").GetString());
        Assert.Equal(404, Read($"collection={original}&collectionId={original}").Status);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2147483648")]
    [InlineData("нет")]
    public void Invalid_collection_keys_are_reported_without_guessing_a_name(string key)
    {
        Orders();
        Assert.Equal(400, Read($"collectionId={key}").Status);
    }

    [Fact]
    public void A_duplicate_output_name_switches_to_field_keys_without_losing_values()
    {
        var (id, number, total) = Orders();
        _db.Constructor.SetSystemFieldName(id, "__title__", "Номер", "Название");
        var namedT = SampleData.AddField(_db.Constructor, id, "t").Id!.Value;
        var numericName = SampleData.AddField(_db.Constructor, id, number.ToString()).Id!.Value;
        var first = _db.Constructor.GetRecords(id).First();
        _db.Constructor.SetFieldValue(first.Id!.Value, namedT, "Буквенное имя");
        _db.Constructor.SetFieldValue(first.Id!.Value, numericName, "Числовое имя");

        var answer = Read();
        Assert.Equal(200, answer.Status);
        var root = Body(answer);
        var values = root.GetProperty("rows")[0].GetProperty("values");
        Assert.Equal(5, values.EnumerateObject().Count());
        Assert.Equal("Молоток", values.GetProperty("t").GetString());
        Assert.Equal("А-1", values.GetProperty(number.ToString()).GetString());
        Assert.Equal("100", values.GetProperty(total.ToString()).GetString());
        Assert.Equal("Буквенное имя", values.GetProperty(namedT.ToString()).GetString());
        Assert.Equal("Числовое имя", values.GetProperty(numericName.ToString()).GetString());
        var columns = root.GetProperty("columns").EnumerateArray().ToDictionary(
            c => c.GetProperty("fieldKey").GetString()!, c => c);
        Assert.Equal("Номер", columns["t"].GetProperty("name").GetString());
        Assert.Equal("Номер", columns[number.ToString()].GetProperty("name").GetString());
        Assert.Equal("t", columns[namedT.ToString()].GetProperty("name").GetString());
        Assert.Equal(number.ToString(), columns[numericName.ToString()].GetProperty("name").GetString());
        foreach (var column in columns.Values)
            Assert.True(values.TryGetProperty(column.GetProperty("key").GetString()!, out _));

        var one = Body(Read($"collectionId={id}&fields={number}")).GetProperty("rows")[0].GetProperty("values");
        Assert.Single(one.EnumerateObject());
        Assert.Equal("А-1", one.GetProperty("Номер").GetString());
    }

    [Fact]
    public void Repeated_selection_of_one_field_by_key_and_name_is_deduplicated()
    {
        var (_, number, _) = Orders();
        var answer = Read($"collection=Заказы&fields=t,{number},Номер,t");
        Assert.Equal(200, answer.Status);
        Assert.Equal(2, Body(answer).GetProperty("columns").GetArrayLength());
        Assert.Equal(2, Body(answer).GetProperty("rows")[0].GetProperty("values").EnumerateObject().Count());
    }

    [Fact]
    public void History_with_duplicate_collection_names_is_not_mixed_even_with_an_explicit_key()
    {
        var (id, _, _) = Orders();
        var group = _db.Constructor.AddGroup(new DbGroup { Name = "Другая группа" }).Id!.Value;
        var other = SampleData.AddCollection(_db.Constructor, "Заказы", group).Id!.Value;
        SampleData.AddRecord(_db.Constructor, other, "Из другой коллекции");
        var answer = Get(IngressApi.ChangesPath, $"collectionId={id}");
        Assert.Equal(409, answer.Status);
        Assert.Contains("только имя", answer.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Из другой коллекции", answer.Body, StringComparison.Ordinal);
    }

}
