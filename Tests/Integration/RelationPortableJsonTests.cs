using System.Text.Json;
using SmartBase.Core.Exporters;
using SmartBase.Core.Exporters.TableWriters;
using SmartBase.Core.Importers;
using SmartBase.Core.Importers.TableSources;
using SmartBase.Core.Models;

namespace SmartBase.Tests.Integration;

public sealed class RelationPortableJsonTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sbportable_").FullName;

    private static RelationPortableSchema Schema() => new(1, 10, 101, "sqlite:source-test",
        new Dictionary<int, string> { [303] = "sqlite:other-test" },
        [
            new(1, "Код", "text"), new(2, "Сумма", "number"),
            new(3, "Связь", "relation", new RelationConfig(10, false, DisplayField: "1").ToConfigJson()),
            new(4, "Показ", "lookup", new RelationComputedConfig(3, "1").ToConfigJson()),
            new(5, "Итог", "rollup", new RelationComputedConfig(3, "2", "sum").ToConfigJson()),
        ]);

    [Fact]
    public void Writer_and_reader_preserve_typed_schema_raw_values_and_full_ordered_targets()
    {
        var path = Path.Combine(_directory, "roundtrip.json");
        var schema = Schema();
        List<ExportColumn> columns = [new("__title__", "Название", null, "text") { PortableSchema = schema }];
        columns.AddRange(schema.Fields.Select(f => new ExportColumn(f.Id.ToString(), f.Name, f.Id, f.FieldType, f.ConfigJson)));
        var expected = new RelationPortableRow(20, "Запись, А", new() { [1] = "А, Б", [2] = "0" },
            new() { [3] = [new(303, 21), new(0, 21)] });
        using (var writer = new JsonTableWriter(path))
        {
            writer.BeginTable("Таблица", columns, true);
            writer.WriteRow(["Название", "видимое значение", "0", "подпись связи", "устаревший lookup", "устаревшая сумма",
                JsonSerializer.Serialize(expected)]);
            writer.EndTable();
            writer.Finish();
        }

        var source = new JsonTableSource(path);
        var analyzed = source.Analyze(null, default);
        var actual = Assert.Single(source.ReadRows(default));
        Assert.Equal(1, analyzed.RowCount);
        Assert.Equal(schema.SourceIdentity, analyzed.PortableSchema!.SourceIdentity);
        Assert.Equal(schema.DatabaseIdentities, analyzed.PortableSchema.DatabaseIdentities);
        Assert.Equal(schema.Fields, analyzed.PortableSchema.Fields);
        Assert.Equal(schema.Fields.Select(f => f.Name), analyzed.FieldNames);
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(["А, Б", "0", "", "", ""], actual.FieldValues);
        Assert.Equal(expected.Id, actual.PortableRow!.Id);
        Assert.Equal(expected.Values, actual.PortableRow.Values);
        Assert.Equal(expected.Links[3], actual.PortableRow.Links[3]);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1, document.RootElement.GetProperty("$smartbase").GetInt32());
        Assert.False(document.RootElement.GetProperty("rows")[0].GetProperty("Values").TryGetProperty("4", out _));
    }

    [Fact]
    public void Schema_after_a_large_rows_array_is_detected_and_every_row_keeps_its_source_id()
    {
        var path = Path.Combine(_directory, "schema-after.json");
        using (var stream = File.Create(path))
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("rows");
            writer.WriteStartArray();
            for (var i = 1; i <= 2000; i++)
                JsonSerializer.Serialize(writer, new RelationPortableRow(i, i == 2000 ? "" : $"Запись {i}",
                    new() { [1] = new string('а', 100) }, new()));
            writer.WriteEndArray();
            writer.WritePropertyName("schema");
            JsonSerializer.Serialize(writer, Schema());
            writer.WriteNumber("$smartbase", 1);
            writer.WriteEndObject();
        }
        var source = new JsonTableSource(path);
        var schema = source.Analyze(null, default);
        Assert.Equal(2000, schema.RowCount);
        Assert.Equal(1, schema.EmptyTitleCount);
        var rows = SourceHelpers.WithTitles(source.ReadRows(default), schema.EmptyTitleCount).ToList();
        Assert.Equal(Enumerable.Range(1, 2000), rows.Select(r => r.PortableRow!.Id));
        Assert.Equal("Без названия", rows[^1].Title);
        Assert.NotNull(rows[^1].PortableRow);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{"Id":20,"Title":"А","Values":null,"Links":{}}""")]
    [InlineData("""{"Id":20,"Id":21,"Title":"А","Values":{},"Links":{}}""")]
    [InlineData("""{"Id":20,"Title":"А","Values":{"1":"А","1":"Б"},"Links":{}}""")]
    [InlineData("""{"Id":20,"Title":"А","Values":{"4":"устаревший lookup"},"Links":{}}""")]
    [InlineData("""{"Id":20,"Title":"А","Values":{"3":"подпись вместо цели"},"Links":{}}""")]
    [InlineData("""{"Id":20,"Title":"А","Values":{},"Links":{"1":[]}}""")]
    [InlineData("""{"Id":20,"Title":"А","Values":{},"Links":{"3":[{"DbId":404,"RecordId":21}]}}""")]
    [InlineData("""{"Id":20,"Title":"А","Values":{},"Links":{"3":[{"DbId":0,"RecordId":21},{"DbId":0,"RecordId":21}]}}""")]
    public void Analyze_refuses_invalid_rows_before_the_import_stage(string row)
    {
        var path = Write("invalid.json", Envelope(row));
        Assert.Throws<ImportException>(() => new JsonTableSource(path).Analyze(null, default));
    }

    [Fact]
    public void Analyze_checks_late_rows_and_the_closing_envelope()
    {
        var good = """{"Id":20,"Title":"А","Values":{},"Links":{}}""";
        var bad = """{"Id":21,"Title":"Б","Values":{"99":"неизвестное поле"},"Links":{}}""";
        Assert.Throws<ImportException>(() => new JsonTableSource(Write("late.json", Envelope(good + "," + bad))).Analyze(null, default));
        var truncated = Envelope(good)[..^1];
        Assert.Throws<ImportException>(() => new JsonTableSource(Write("truncated.json", truncated)).Analyze(null, default));
        Assert.Throws<ImportException>(() => new JsonTableSource(Write("tail.json", Envelope(good) + "{}")).Analyze(null, default));
    }

    [Fact]
    public void Ordinary_ndjson_with_schema_and_rows_properties_remains_an_ordinary_table()
    {
        var path = Write("legacy.json", """
            {"title":"А","schema":"обычное поле","rows":[1,2]}
            {"title":"Б","schema":"другое значение","rows":[]}
            """);
        var source = new JsonTableSource(path);
        var schema = source.Analyze(null, default);
        Assert.Null(schema.PortableSchema);
        var rows = source.ReadRows(default).ToList();
        Assert.Equal(["А", "Б"], rows.Select(r => r.Title));
        Assert.All(rows, row => Assert.Null(row.PortableRow));
        Assert.Equal(["обычное поле", "[1,2]"], rows[0].FieldValues);
    }

    [Fact]
    public void Portable_writer_refuses_a_scalar_row_without_its_graph_payload()
    {
        using var writer = new JsonTableWriter(Path.Combine(_directory, "missing.json"));
        writer.BeginTable("Таблица", [new ExportColumn("__title__", "Название", null, "text") { PortableSchema = Schema() }], true);
        Assert.Throws<ExportException>(() => writer.WriteRow(["Название"]));
    }

    private static string Envelope(string rows) =>
        "{\"$smartbase\":1,\"schema\":" + JsonSerializer.Serialize(Schema()) + ",\"rows\":[" + rows + "]}";

    private string Write(string name, string text)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* временная папка */ }
    }
}
