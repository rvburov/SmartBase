using SmartBase.Core.Importers;
using SmartBase.Core.Importers.TableSources;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Источник импорта поверх ответа чужой программы (<see cref="JsonTextSource"/>).
///
/// Правила у него те же, что у файлового <see cref="JsonTableSource"/>, и это проверяется
/// отдельно: один и тот же набор записей, пришедший файлом и по сети, обязан ложиться в
/// коллекцию одинаково — иначе человек, сверяющий выгрузку с импортом, сойдет с ума.
/// </summary>
public sealed class JsonTextSourceTests
{
    private static (ImportSchema Schema, List<ImportRow> Rows) Read(string json, string path = "")
    {
        var source = new JsonTextSource(json, path);
        var schema = source.Analyze(null, CancellationToken.None);
        return (schema, [.. source.ReadRows(CancellationToken.None)]);
    }

    // ── Обычный ответ ─────────────────────────────────────────────────────────

    [Fact]
    public void Array_of_objects_becomes_rows()
    {
        var (schema, rows) = Read("""
            [{"Название":"Молоток","Цена":120,"Склад":"А"},
             {"Название":"Отвертка","Цена":80,"Склад":"Б"}]
            """);

        Assert.Equal("Название", schema.TitleHeader);
        Assert.Equal(["Цена", "Склад"], schema.FieldNames);
        Assert.Equal(2, schema.RowCount);

        Assert.Equal(["Молоток", "Отвертка"], rows.Select(r => r.Title));
        Assert.Equal(["120", "А"], rows[0].FieldValues);
    }

    /// <summary>Ключи объединяются по всем записям — чужие программы редко отдают ровный список.</summary>
    [Fact]
    public void Keys_are_united_across_records()
    {
        var (schema, rows) = Read("""[{"name":"А","x":1},{"name":"Б","y":2}]""");

        Assert.Equal(["x", "y"], schema.FieldNames);
        Assert.Equal(["1", ""], rows[0].FieldValues);
        Assert.Equal(["", "2"], rows[1].FieldValues);
    }

    /// <summary>Названием становится «Название», иначе name, иначе title, иначе первый ключ.</summary>
    [Theory]
    [InlineData("""[{"id":1,"title":"Т","name":"Н","Название":"З"}]""", "Название")]
    [InlineData("""[{"id":1,"title":"Т","name":"Н"}]""",                "name")]
    [InlineData("""[{"id":1,"title":"Т"}]""",                           "title")]
    [InlineData("""[{"id":1,"prop":2}]""",                              "id")]
    public void Title_key_is_picked_by_the_common_rule(string json, string expected)
    {
        Assert.Equal(expected, Read(json).Schema.TitleHeader);
    }

    /// <summary>Числа и вложенные объекты сохраняются как в ответе, null — пусто.</summary>
    [Fact]
    public void Values_keep_their_source_form()
    {
        var (_, rows) = Read("""
            [{"name":"А","число":12.50,"флаг":true,"пусто":null,"вложение":{"a":1}}]
            """);

        Assert.Equal(["12.50", "true", "", """{"a":1}"""], rows[0].FieldValues);
    }

    // ── Путь к массиву ────────────────────────────────────────────────────────

    [Fact]
    public void Items_are_taken_by_the_path()
    {
        var (schema, rows) = Read("""
            {"ok":true,"data":{"items":[{"name":"А"},{"name":"Б"}]}}
            """, "data.items");

        Assert.Equal(2, schema.RowCount);
        Assert.Equal(["А", "Б"], rows.Select(r => r.Title));
    }

    /// <summary>Справочный вызов часто отдает один объект — это одна запись, а не ошибка.</summary>
    [Fact]
    public void Single_object_is_one_row()
    {
        var (schema, rows) = Read("""{"name":"А","x":1}""");

        Assert.Equal(1, schema.RowCount);
        Assert.Equal("А", rows[0].Title);
    }

    /// <summary>Неверный путь называет, какая именно часть не нашлась.</summary>
    [Fact]
    public void Wrong_path_says_which_part_is_missing()
    {
        var error = Assert.Throws<ImportException>(
            () => Read("""{"data":{"items":[{"name":"А"}]}}""", "data.список"));

        Assert.Contains("список", error.Message, StringComparison.Ordinal);
    }

    // ── Пустые названия ───────────────────────────────────────────────────────

    /// <summary>Пустые названия считаются: по ним делается нумерация «Без названия N».</summary>
    [Fact]
    public void Empty_titles_are_counted()
    {
        var (schema, _) = Read("""[{"name":"","x":1},{"name":"Б","x":2},{"name":"   ","x":3}]""");

        Assert.Equal(3, schema.RowCount);
        Assert.Equal(2, schema.EmptyTitleCount);
    }

    // ── Что не годится ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("",                     "пустой")]
    [InlineData("   ",                  "пустой")]
    [InlineData("не json",              "не является JSON")]
    [InlineData("[]",                   "нет записей")]
    [InlineData("""[1,2,3]""",          "не является объектом")]
    [InlineData("\"строка\"",           "не список записей")]
    public void Unusable_answer_gives_a_readable_reason(string json, string expected)
    {
        var error = Assert.Throws<ImportException>(() => Read(json));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    /// <summary>Текст ответа в сообщение об ошибке не попадает: в нем может оказаться секрет.</summary>
    [Fact]
    public void Broken_answer_does_not_leak_its_text()
    {
        var error = Assert.Throws<ImportException>(() => Read("""{"token":"T0K3N",,,"""));

        Assert.DoesNotContain("T0K3N", error.Message, StringComparison.Ordinal);
    }
}
