using System.Text.Json;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Описание собственного API (docs/ROADMAP-API.md, раздел 7).
///
/// Два вида одного и того же — текст для человека и JSON для машины, — и главное требование к
/// ним одно: обещать ровно те адреса, которые слушатель принимает. Второе требование —
/// <b>никаких данных</b>: отсюда уходит схема, и значений полей здесь нет по построению.
/// </summary>
public sealed class ApiDescriptionTests
{
    private static readonly IReadOnlyList<ApiCollection> Schema =
    [
        new("Заказы", "Номер",
            [new ApiField("Клиент", "Текст", 11), new ApiField("Сумма", "Число", 12)], 7),
        new("Склад", "Название", [new ApiField("Остаток", "Число", 13)], 9),
    ];

    [Fact]
    public void The_text_promises_the_addresses_the_listener_answers()
    {
        var text = ApiDescription.Text("http://127.0.0.1:8787", "Основная", Schema, 7);

        Assert.Contains(IngressApi.InboxPath,   text, StringComparison.Ordinal);
        Assert.Contains(IngressApi.PingPath,    text, StringComparison.Ordinal);
        Assert.Contains(IngressApi.SchemaPath,  text, StringComparison.Ordinal);
        Assert.Contains("database=7", text, StringComparison.Ordinal);
        Assert.Contains("без входа и токенов", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Потолок тела назван отправителю ЗДЕСЬ, а не выясняется им по отказу. И рядом сказано, что
    /// к файловым каналам он не относится: каналов три, потолок у одного, и первое же число,
    /// написанное без оговорки, прочиталось бы как общее правило — а ночную выгрузку по нему
    /// стали бы резать на части без всякой нужды.
    /// </summary>
    [Fact]
    public void The_text_names_the_body_cap_and_says_where_it_does_not_apply()
    {
        var text = ApiDescription.Text("http://127.0.0.1:8787", "Основная", Schema);

        Assert.Contains(ApiDescription.BodyLimit(), text, StringComparison.Ordinal);
        Assert.Contains("--inbox",                  text, StringComparison.Ordinal);
        Assert.Contains("не ограничен",             text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_text_explains_database_selection_and_read_field_selection()
    {
        var text = ApiDescription.Text("http://127.0.0.1:8787", "Основная", Schema);

        Assert.Contains("ровно одна база", text, StringComparison.Ordinal);
        Assert.Contains("fields", text, StringComparison.Ordinal);
        Assert.Contains("уникальным в пределах базы", text, StringComparison.Ordinal);
        Assert.Contains("collectionId", text, StringComparison.Ordinal);
        Assert.Contains("columns", text, StringComparison.Ordinal);
        Assert.Contains("409", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_text_lists_collections_with_their_columns()
    {
        var text = ApiDescription.Text("http://127.0.0.1:8787", "Основная", Schema);

        Assert.Contains("Заказы",  text, StringComparison.Ordinal);
        Assert.Contains("Клиент",  text, StringComparison.Ordinal);
        Assert.Contains("Число",   text, StringComparison.Ordinal);
        Assert.Contains("Номер",   text, StringComparison.Ordinal);   // колонка названия записи
    }

    /// <summary>
    /// Выключенный прием не называет адреса, а говорит, что он выключен: адрес, по которому
    /// никто не ответит, отправитель будет отлаживать часами.
    /// </summary>
    [Fact]
    public void With_no_address_the_text_says_so_instead_of_inventing_one()
    {
        var text = ApiDescription.Text("", "Основная", Schema);

        Assert.Contains("выключен", text, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_json_carries_the_schema_and_no_values()
    {
        var json = ApiDescription.Json("Основная", Schema, 7);
        var root = JsonDocument.Parse(json).RootElement;

        Assert.Equal("Основная", root.GetProperty("database").GetString());
        Assert.Equal(7, root.GetProperty("databaseId").GetInt32());
        Assert.Equal("none", root.GetProperty("authentication").GetString());
        Assert.Equal(IngressApi.DatabaseParameter, root.GetProperty("databaseParameter").GetString());
        Assert.Equal(IngressApi.InboxPath, root.GetProperty("inbox").GetString());

        var collections = root.GetProperty("collections");
        Assert.Equal(2, collections.GetArrayLength());

        var first = collections[0];
        Assert.Equal("Заказы", first.GetProperty("name").GetString());
        Assert.Equal(7, first.GetProperty("id").GetInt32());
        Assert.Equal(11, first.GetProperty("fields")[0].GetProperty("id").GetInt32());
        Assert.Equal("Номер",  first.GetProperty("title").GetString());
        Assert.Equal(2,        first.GetProperty("fields").GetArrayLength());
        Assert.Equal("Клиент", first.GetProperty("fields")[0].GetProperty("name").GetString());
    }

    /// <summary>Кириллица остается кириллицей: описание читает и человек.</summary>
    [Fact]
    public void Russian_names_stay_readable()
    {
        Assert.Contains("Заказы", ApiDescription.Json("Основная", Schema), StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_database_is_described_as_empty_rather_than_silently()
    {
        var text = ApiDescription.Text("http://127.0.0.1:8787", "Пустая", []);

        Assert.Contains("нет ни одной коллекции", text, StringComparison.Ordinal);
    }
}
