using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Готовые заготовки запросов (<see cref="RequestPresets"/>, docs/ROADMAP-API.md, раздел 5).
///
/// Правило, которое здесь сторожится: <b>заготовка не создает «другой» запрос</b> — она
/// заполняет тот же самый объект, и заполняет его ПРАВИЛЬНО. Заготовка, которую карточка
/// откажется сохранить, хуже пустой: человек нажал кнопку «упростить» и получил окно с ошибкой.
/// </summary>
public sealed class RequestPresetsTests
{
    [Fact]
    public void Every_preset_fills_a_card_that_can_be_saved()
    {
        Assert.NotEmpty(RequestPresets.All);

        foreach (var preset in RequestPresets.All)
        {
            // Соединение человек выбирает сам — его отсутствие карточка назовет отдельно, и
            // это не вина заготовки. Проверяем все остальное.
            var card = preset.Request with { ConnectionId = 1 };

            Assert.Equal([], card.Problems());
        }
    }

    [Fact]
    public void Every_preset_says_what_it_is_and_when_it_is_taken()
    {
        foreach (var preset in RequestPresets.All)
        {
            Assert.NotEqual("", preset.Title.Trim());
            Assert.NotEqual("", preset.Hint.Trim());
            Assert.NotEqual("", preset.Request.Name.Trim());
        }
    }

    /// <summary>Названия в списке не повторяются: выбирать из двух одинаковых строк нельзя.</summary>
    [Fact]
    public void Preset_titles_are_distinct()
    {
        var titles = RequestPresets.All.Select(p => p.Title).ToList();

        Assert.Equal(titles.Count, titles.Distinct(StringComparer.CurrentCultureIgnoreCase).Count());
    }

    /// <summary>
    /// Заготовка с разбивкой обязана назвать поле разбивки: без него выгрузка молча оборвется
    /// на первой сотне записей — ровно та беда, ради которой эта заготовка и заведена.
    /// </summary>
    [Fact]
    public void A_paging_preset_names_its_paging_field()
    {
        foreach (var preset in RequestPresets.All.Where(p => p.Request.NeedsPagingField))
            Assert.NotEqual("", preset.Request.PagingField);
    }

    /// <summary>
    /// Имен сторонних программ в заготовках нет: названия меняются, а подпись в интерфейсе читается
    /// как обещание поддержки, которого никто не давал (docs/ROADMAP-API.md, преамбула).
    /// </summary>
    [Theory]
    [InlineData("Telegram")]
    [InlineData("Bitrix")]
    [InlineData("1С")]
    [InlineData("1C")]
    [InlineData("amoCRM")]
    public void No_preset_names_a_third_party_program(string name)
    {
        foreach (var preset in RequestPresets.All)
        {
            Assert.DoesNotContain(name, preset.Title, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(name, preset.Hint,  StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(name, preset.Request.Name, StringComparison.OrdinalIgnoreCase);
        }
    }
}
