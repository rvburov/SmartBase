using SmartBase.Core.Exporters;
using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Исходящая привязка «коллекция → JSON» (<see cref="OutgoingBinding"/>, <see cref="OutgoingBody"/>):
/// что считается заполненным правильно, что входит в отпечаток состава и каким уходит тело.
///
/// Чтение записей из настоящей коллекции — <c>Tests/Integration/OutgoingSourceTests</c>; здесь
/// только форма и сборка, зато без базы и мгновенно.
/// </summary>
public sealed class OutgoingBindingTests
{
    private static OutgoingBinding Valid() =>
        new(3, 7, [new OutgoingField(null, "order_no"), new OutgoingField(11, "sum")]);

    private static IReadOnlyList<ExportColumn> Columns(params string[] types) =>
        [.. types.Select((t, i) => new ExportColumn($"k{i}", $"Поле {i}", i, t))];

    // ── Заполнено правильно ───────────────────────────────────────────────────

    [Fact]
    public void Filled_binding_has_no_problems() => Assert.Empty(Valid().Problems());

    [Fact]
    public void Binding_without_fields_has_nothing_to_send() =>
        Assert.Contains(
            (Valid() with { Fields = [] }).Problems(),
            p => p.Contains("не выбрано ни одного поля", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Field_without_a_name_is_refused() =>
        Assert.Contains(
            (Valid() with { Fields = [new OutgoingField(11, "  ")] }).Problems(),
            p => p.Contains("имя", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Два поля под одним именем — не «последнее победит», а молчаливая потеря одного из них:
    /// JSON с повторяющимся ключом разбирают все по-своему.
    /// </summary>
    [Fact]
    public void One_name_taken_twice_is_refused()
    {
        var binding = Valid() with
        {
            Fields = [new OutgoingField(null, "sum"), new OutgoingField(11, "SUM")],
        };

        Assert.Contains(binding.Problems(),
                        p => p.Contains("дважды", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Wrapped_body_needs_the_name_of_its_field() =>
        Assert.Contains(
            (Valid() with { Shape = OutgoingShape.Wrapped }).Problems(),
            p => p.Contains("имя поля", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Collection_must_be_chosen() =>
        Assert.Contains(
            (Valid() with { DbId = 0 }).Problems(),
            p => p.Contains("коллекция", StringComparison.OrdinalIgnoreCase));

    // ── Отпечаток состава ─────────────────────────────────────────────────────

    /// <summary>
    /// Состав входит в отпечаток запроса, поэтому дописанное поле обязано ронять подтверждение
    /// направления: иначе состав выгрузки остался бы единственной частью отправки, которую можно
    /// поменять после согласования.
    /// </summary>
    [Fact]
    public void Mark_changes_with_everything_that_changes_what_goes_out()
    {
        var mark = Valid().Mark();

        Assert.Equal(mark, Valid().Mark());   // сам по себе не «плывет»

        Assert.NotEqual(mark, (Valid() with
        {
            Fields = [.. Valid().Fields, new OutgoingField(12, "client")],
        }).Mark());

        Assert.NotEqual(mark, (Valid() with
        {
            Fields = [new OutgoingField(null, "order_no"), new OutgoingField(11, "amount")],
        }).Mark());

        Assert.NotEqual(mark, (Valid() with { Shape = OutgoingShape.Single }).Mark());
        Assert.NotEqual(mark, (Valid() with { CollectionId = 8 }).Mark());
    }

    /// <summary>
    /// Отпечаток запроса БЕЗ состава остается прежним: иначе в день обновления программы
    /// подтверждения слетели бы у всех разом, включая тех, кто ничего не менял.
    /// </summary>
    [Fact]
    public void Request_without_outgoing_keeps_its_old_fingerprint()
    {
        var request = new IntegrationRequest(
            5, "Отдать заказы", 1, RequestMethod.Post, "/orders", [], """{"итог":1}""",
            RequestPaging.None, "", "");

        Assert.Equal(request.Fingerprint(), request.Fingerprint(""));
        Assert.NotEqual(request.Fingerprint(), request.Fingerprint(Valid().Mark()));
    }

    // ── Тело ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Array_body_is_a_bare_array()
    {
        var payload = OutgoingBody.Build(
            Valid(), Columns("text", "number"),
            [["А-1", "100"], ["А-2", "200"]], "Заказы");

        Assert.Equal("""[{"order_no":"А-1","sum":100},{"order_no":"А-2","sum":200}]""", payload.Body);
        Assert.Equal(2, payload.Rows);
        Assert.Equal("order_no, sum", payload.Fields);
        Assert.Equal("Заказы", payload.Collection);
    }

    [Fact]
    public void Wrapped_body_puts_the_array_into_its_field()
    {
        var payload = OutgoingBody.Build(
            Valid() with { Shape = OutgoingShape.Wrapped, Envelope = "orders" },
            Columns("text", "number"), [["А-1", "100"]], "Заказы");

        Assert.Equal("""{"orders":[{"order_no":"А-1","sum":100}]}""", payload.Body);
    }

    [Fact]
    public void Single_body_is_one_object()
    {
        var payload = OutgoingBody.Build(
            Valid() with { Shape = OutgoingShape.Single },
            Columns("text", "number"), [["А-1", "100"]], "Заказы");

        Assert.Equal("""{"order_no":"А-1","sum":100}""", payload.Body);
        Assert.Equal(1, payload.Rows);
    }

    /// <summary>
    /// Тип берется у СВОЙСТВА, а не угадывается по значению. Иначе артикул «007» ушел бы числом
    /// 7, и нашлось бы это через неделю по ненайденному товару.
    /// </summary>
    [Fact]
    public void Value_type_comes_from_the_property_not_from_the_text()
    {
        var payload = OutgoingBody.Build(
            Valid(), Columns("text", "text"), [["007", "12,5"]], "Товары");

        Assert.Equal("""[{"order_no":"007","sum":"12,5"}]""", payload.Body);
    }

    [Fact]
    public void Checkbox_and_multiselect_keep_their_json_shape()
    {
        var binding = Valid() with
        {
            Fields = [new OutgoingField(11, "paid"), new OutgoingField(12, "tags")],
        };

        var payload = OutgoingBody.Build(
            binding, Columns("checkbox", "multiselect"), [["1", "срочно|важно"]], "Заказы");

        Assert.Contains("\"paid\":true", payload.Body, StringComparison.Ordinal);
        Assert.Contains("\"tags\":[", payload.Body, StringComparison.Ordinal);
    }

    /// <summary>Тело показывают человеку сухим прогоном — читать его должно быть можно.</summary>
    [Fact]
    public void Cyrillic_stays_readable()
    {
        var payload = OutgoingBody.Build(
            Valid() with { Fields = [new OutgoingField(null, "название")] },
            Columns("text"), [["Насос"]], "Заказы");

        Assert.Equal("""[{"название":"Насос"}]""", payload.Body);
        Assert.DoesNotContain("\\u", payload.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Значений в строке меньше, чем полей, — остальные уходят пустыми: пропустить ключ значило
    /// бы отдать той стороне разные наборы полей у разных записей.
    /// </summary>
    [Fact]
    public void Short_row_fills_the_rest_with_empty_values()
    {
        var payload = OutgoingBody.Build(
            Valid(), Columns("text", "text"), [["А-1"]], "Заказы");

        Assert.Equal("""[{"order_no":"А-1","sum":""}]""", payload.Body);
    }

    [Fact]
    public void Empty_collection_gives_an_empty_array_not_an_empty_body()
    {
        var payload = OutgoingBody.Build(Valid(), Columns("text", "text"), [], "Заказы");

        Assert.Equal("[]", payload.Body);
        Assert.Equal(0, payload.Rows);
    }

    /// <summary>Списки полей и колонок разошлись — это ошибка вызывающего, и молчать о ней нельзя.</summary>
    [Fact]
    public void Mismatched_columns_are_a_programming_error() =>
        Assert.Throws<ArgumentException>(
            () => OutgoingBody.Build(Valid(), Columns("text"), [["А-1"]], "Заказы"));

    // ── Отбор записей ─────────────────────────────────────────────────────────

    /// <summary>Отбора нет — это норма, а не незаполненность: уходит вся коллекция.</summary>
    [Fact]
    public void No_filter_is_a_valid_binding()
    {
        Assert.Empty(Valid().Tests);
        Assert.Empty(Valid().Problems());
        Assert.Equal("", Valid().DescribeTests());
    }

    /// <summary>Негодное условие не проходит вместе с привязкой — и называет себя.</summary>
    [Fact]
    public void A_condition_without_a_sample_is_refused()
    {
        var binding = Valid() with
        {
            Conditions = [new ScenarioCondition("Статус", ScenarioTest.Equals, "")],
        };

        Assert.Contains(binding.Problems(),
                        p => p.Contains("не заполнен образец", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Условию «заполнено» образец не нужен — и не спрашивается.</summary>
    [Fact]
    public void Filled_needs_no_sample()
    {
        var binding = Valid() with
        {
            Conditions = [new ScenarioCondition("Статус", ScenarioTest.Filled)],
        };

        Assert.Empty(binding.Problems());
    }

    /// <summary>
    /// Условий больше потолка — отказ: столько правил читаются хуже, чем отдельная коллекция, и
    /// разобраться, почему запись не ушла, по ним уже нельзя.
    /// </summary>
    [Fact]
    public void Too_many_conditions_are_refused()
    {
        var many = Enumerable.Range(0, OutgoingBinding.MaxConditions + 1)
                             .Select(i => new ScenarioCondition($"Поле {i}", ScenarioTest.Filled))
                             .ToList();

        Assert.Contains((Valid() with { Conditions = many }).Problems(),
                        p => p.Contains("условий отбора больше", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Отбор входит в отпечаток состава наравне с полями. Снятое условие расширяет выгрузку не
    /// меньше, чем дописанное поле, — не считай его, и отбор остался бы единственной частью
    /// отправки, которую можно поменять после согласования, не уронив подтверждения направления.
    /// </summary>
    [Fact]
    public void The_filter_is_part_of_the_fingerprint()
    {
        var plain = Valid();

        var filtered = plain with
        {
            Conditions = [new ScenarioCondition("Статус", ScenarioTest.Equals, "Отгружен")],
        };

        var otherSample = plain with
        {
            Conditions = [new ScenarioCondition("Статус", ScenarioTest.Equals, "Оплачен")],
        };

        var otherTest = plain with
        {
            Conditions = [new ScenarioCondition("Статус", ScenarioTest.Contains, "Отгружен")],
        };

        Assert.NotEqual(plain.Mark(),    filtered.Mark());
        Assert.NotEqual(filtered.Mark(), otherSample.Mark());
        Assert.NotEqual(filtered.Mark(), otherTest.Mark());

        // Тот же отбор — тот же отпечаток: пересохранение без правок подтверждения не роняет.
        Assert.Equal(filtered.Mark(), (plain with
        {
            Conditions = [new ScenarioCondition("Статус", ScenarioTest.Equals, "Отгружен")],
        }).Mark());
    }

    /// <summary>Отбор виден на карточке: и числом в общей строке, и словами отдельно.</summary>
    [Fact]
    public void The_filter_shows_up_in_the_description()
    {
        var binding = Valid() with
        {
            Conditions =
            [
                new ScenarioCondition("Статус",     ScenarioTest.Equals, "Отгружен"),
                new ScenarioCondition("Отправлено", ScenarioTest.Empty),
            ],
        };

        Assert.Contains("отбор: 2", binding.Describe(), StringComparison.Ordinal);
        Assert.Equal("Статус равно Отгружен и Отправлено пусто", binding.DescribeTests());
    }
}
