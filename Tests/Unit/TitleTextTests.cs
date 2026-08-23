using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Название для показа — одна строка (Core/Models/TitleText.cs).
///
/// В базе <c>Title</c> лежит как есть: вставка текста в ячейку «Название» и импорт приносят
/// переводы строк. Все блоки, где название рисуется одним куском — карточка задачи, строка
/// «Расписания», лента «Журнала», плашка события, попап года, — показывают первую непустую
/// строку. Полное значение остается в самом <c>Title</c>: по нему идут поиск, сортировка,
/// экспорт и правка в окнах записи и задачи.
/// </summary>
public sealed class TitleTextTests
{
    // ── FirstLine ─────────────────────────────────────────────────────────────

    [Fact]
    public void Single_line_is_returned_as_is()
    {
        // Быстрый путь: ни разбора, ни Trim — что набрал пользователь, то и показываем.
        Assert.Equal("Изучить раздел «Календарь»", TitleText.FirstLine("Изучить раздел «Календарь»"));
        Assert.Equal("  с пробелами  ", TitleText.FirstLine("  с пробелами  "));
    }

    [Theory]
    [InlineData("Первая\nВторая\nТретья",     "Первая")]
    [InlineData("Первая\r\nВторая",           "Первая")]   // CRLF: '\r' снимается Trim
    [InlineData("Первая\rВторая",             "Первая")]   // одинокий CR (старый Mac / вставка)
    [InlineData("\n\n  Первая  \nВторая",     "Первая")]   // пустые строки в начале пропускаются
    [InlineData("   \n\t\nТекст",             "Текст")]    // строка из пробелов — не строка
    public void Multiline_gives_the_first_non_empty_line(string input, string expected) =>
        Assert.Equal(expected, TitleText.FirstLine(input));

    [Theory]
    [InlineData(null,      "")]
    [InlineData("",        "")]
    [InlineData("\n\n\n",  "")]
    [InlineData("  \n \r", "")]
    public void Nothing_to_show_gives_empty(string? input, string expected) =>
        Assert.Equal(expected, TitleText.FirstLine(input));

    // ── DisplayTitle моделей ──────────────────────────────────────────────────

    /// <summary>
    /// Событие календаря: показ — первая строка, <c>Title</c> не тронут (по нему ищут
    /// и его же правят в окне записи).
    /// </summary>
    [Fact]
    public void Calendar_record_shows_the_first_line_only()
    {
        var ev = new CalendarRecord { Title = "Изучить раздел «Расширения»\nВторая\nТретья" };

        Assert.Equal("Изучить раздел «Расширения»", ev.DisplayTitle);
        Assert.Equal("Изучить раздел «Расширения»\nВторая\nТретья", ev.Title);
    }

    [Fact]
    public void Task_record_shows_the_first_line_only()
    {
        var task = new TaskRecord { Title = "Сдать отчет\nдо пятницы" };

        Assert.Equal("Сдать отчет", task.DisplayTitle);
        Assert.Equal("Сдать отчет\nдо пятницы", task.Title);
    }

    [Fact]
    public void Db_record_shows_the_first_line_only()
    {
        var rec = new DbRecord { Title = "Договор №17\nот 01.02.2026" };

        Assert.Equal("Договор №17", rec.DisplayTitle);
    }

    /// <summary>Название из одних переводов строк — такое же «пустое», как и вовсе пустое.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    public void Blank_title_falls_back_to_the_placeholder(string title)
    {
        Assert.Equal(CalendarRecord.UntitledPlaceholder, new CalendarRecord { Title = title }.DisplayTitle);
        Assert.Equal(CalendarRecord.UntitledPlaceholder, new TaskRecord     { Title = title }.DisplayTitle);
        Assert.Equal("Без названия",                     new DbRecord       { Title = title }.DisplayTitle);
    }
}
