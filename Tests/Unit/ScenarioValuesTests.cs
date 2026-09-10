using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Значения полей действия (<see cref="ScenarioValues"/>): постоянный текст плюс колонки строки.
///
/// Проверяется ровно то, ради чего подстановки заведены, и то, чем они опасны: имя колонки
/// приходит из чужой выгрузки, а опечатка в нем обязана останавливать шаг, а не заполнять поле
/// пустотой. Пустое поле в журнале регистрации ищут потом неделю.
/// </summary>
public sealed class ScenarioValuesTests
{
    private static IReadOnlyDictionary<string, string> Row(params (string Column, string Value)[] cells) =>
        cells.ToDictionary(c => c.Column, c => c.Value, StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> Message =
        Row(("Проект", "Молоток"), ("Децимальный номер", "АБВГ.301121"), ("Примечание", ""));

    // ── Обычная подстановка ───────────────────────────────────────────────────

    /// <summary>Текст без скобок — постоянное значение: код организации у всех документов один.</summary>
    [Fact]
    public void Plain_text_goes_through_as_is()
    {
        Assert.Equal("АБВГ", ScenarioValues.Apply("АБВГ", Message, out var unknown));
        Assert.Empty(unknown);
    }

    [Fact]
    public void A_column_replaces_the_whole_value()
    {
        Assert.Equal("Молоток", ScenarioValues.Apply("{{Проект}}", Message, out var unknown));
        Assert.Empty(unknown);
    }

    [Fact]
    public void A_column_replaces_a_part_of_the_text()
    {
        Assert.Equal("Проект Молоток, узел АБВГ.301121",
                     ScenarioValues.Apply("Проект {{Проект}}, узел {{Децимальный номер}}",
                                          Message, out var unknown));
        Assert.Empty(unknown);
    }

    /// <summary>
    /// Регистр имени не важен — так же, как колонки источника ложатся на поля коллекции при
    /// импорте: «Номер» и «номер» в чужой выгрузке означают одно и то же.
    /// </summary>
    [Fact]
    public void The_column_name_is_matched_ignoring_case()
    {
        Assert.Equal("Молоток", ScenarioValues.Apply("{{проект}}", Message, out var unknown));
        Assert.Empty(unknown);
    }

    /// <summary>Пробелы вокруг имени — след ручного набора, а не другое имя.</summary>
    [Fact]
    public void Spaces_around_the_name_are_ignored()
    {
        Assert.Equal("Молоток", ScenarioValues.Apply("{{  Проект  }}", Message, out var unknown));
        Assert.Empty(unknown);
    }

    /// <summary>
    /// Пустая колонка подставляется пустотой и НЕ считается неизвестной: пустое значение —
    /// обычное дело, а обязательность поля проверяет уже действие.
    /// </summary>
    [Fact]
    public void An_empty_column_is_not_an_unknown_one()
    {
        Assert.Equal("", ScenarioValues.Apply("{{Примечание}}", Message, out var unknown));
        Assert.Empty(unknown);
    }

    // ── Незнакомое имя ────────────────────────────────────────────────────────

    /// <summary>
    /// Опечатка в имени колонки — отказ, а не тихая пустота. Иначе действие зарегистрировало бы
    /// документ без проекта, и увидели бы это через неделю по журналу.
    /// </summary>
    [Fact]
    public void An_unknown_column_is_reported_and_left_in_place()
    {
        var result = ScenarioValues.Apply("{{Проэкт}}", Message, out var unknown);

        Assert.Equal(["Проэкт"], unknown);
        Assert.Equal("{{Проэкт}}", result);   // текст остается целиком — видно, что набрали
    }

    [Fact]
    public void Every_unknown_column_is_named_once()
    {
        ScenarioValues.Apply("{{нет}} и {{нет}} и {{тоже нет}}", Message, out var unknown);

        Assert.Equal(["нет", "тоже нет"], unknown);
    }

    /// <summary>Известное подставляется даже рядом с неизвестным — отчет получается полным.</summary>
    [Fact]
    public void The_known_part_is_still_substituted()
    {
        var result = ScenarioValues.Apply("{{Проект}} / {{Автор}}", Message, out var unknown);

        Assert.Equal("Молоток / {{Автор}}", result);
        Assert.Equal(["Автор"], unknown);
    }

    // ── Живучесть ─────────────────────────────────────────────────────────────

    /// <summary>Незакрытая скобка — текст, а не поломка: набирает человек, а не программа.</summary>
    [Fact]
    public void An_unclosed_brace_stays_text()
    {
        Assert.Equal("{{Проект", ScenarioValues.Apply("{{Проект", Message, out var unknown));
        Assert.Empty(unknown);
    }

    [Fact]
    public void Nothing_is_an_empty_string()
    {
        Assert.Equal("", ScenarioValues.Apply(null, Message, out _));
        Assert.Equal("", ScenarioValues.Apply("", Message, out _));
    }

    /// <summary>Строки нет вовсе — все имена неизвестны, и об этом говорится сразу.</summary>
    [Fact]
    public void Without_a_row_every_name_is_unknown()
    {
        ScenarioValues.Apply("{{Проект}}", new Dictionary<string, string>(), out var unknown);

        Assert.Equal(["Проект"], unknown);
    }
}
