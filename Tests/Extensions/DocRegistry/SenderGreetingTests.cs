using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>
/// Приветствие новым регистрирующим (<see cref="SenderGreeting"/>): кому уходит, что уходит и где
/// кнопка. Сама отправка — путь ответов сценария — проверяется в ядре
/// (<c>ActionRepliesTests</c>): расширение его не знает.
/// </summary>
public sealed class SenderGreetingTests
{
    /// <summary>Новые — только те, кому инструкция еще не уходила, в порядке списка и с
    /// пометкой: так человек и видит их в окне подтверждения.</summary>
    [Fact]
    public void Fresh_are_the_listed_who_have_not_got_the_instruction()
    {
        var fresh = SenderGreeting.Fresh("791390 Буров; 1001 Иванов; 1002  Петров П. П.", "791390");

        Assert.Equal([("1001", "1001 Иванов"), ("1002", "1002 Петров П. П.")], fresh);
    }

    /// <summary>Убранный из списка не новый: удаление ничего не отправляет, а получившие
    /// помнятся и без списка.</summary>
    [Fact]
    public void Removed_ones_are_not_fresh_and_returned_ones_get_nothing_twice()
    {
        Assert.Empty(SenderGreeting.Fresh("", "1001; 1002"));
        Assert.Empty(SenderGreeting.Fresh("1001 Иванов", "1001; 1002"));
    }

    /// <summary>Идентификаторы сравниваются без регистра — как при проверке отправителя.</summary>
    [Fact]
    public void Ids_are_compared_ignoring_case()
    {
        Assert.Empty(SenderGreeting.Fresh("ivanov Иванов", "IVANOV"));
    }

    [Fact]
    public void Remember_adds_once()
    {
        var once  = SenderGreeting.Remember("", "1001");
        var twice = SenderGreeting.Remember(SenderGreeting.Remember(once, "1002"), "1001");

        Assert.Equal("1001", once);
        Assert.Equal("1001; 1002", twice);
    }

    /// <summary>Сообщение — строка заголовка, пустая строка и инструкция целиком. Переводы строк
    /// — «\n»: перенос под ту сторону ставит сценарий.</summary>
    [Fact]
    public void Text_is_the_heading_and_the_instruction()
    {
        var seen = ReplyMarkup.Text(SenderGreeting.Text);

        Assert.StartsWith("Инструкция по регистрации документов в журнале КД.\n\nИнструкция по работе с журналом КД\n",
                          seen, StringComparison.Ordinal);
        Assert.EndsWith("Инструкция", seen.TrimEnd(), StringComparison.Ordinal);
        Assert.DoesNotContain('\r', SenderGreeting.Text);
    }

    /// <summary>Первая строка — жирным, как заголовок; за ней — оформленная инструкция.</summary>
    [Fact]
    public void The_first_line_is_bold_like_a_heading()
    {
        Assert.StartsWith("<b>Инструкция по регистрации документов в журнале КД.</b>\n\n"
                          + "<b><font size='4'>Инструкция по работе с журналом КД</font></b>\n",
                          SenderGreeting.Text, StringComparison.Ordinal);
    }

    /// <summary>Кнопка стоит справа от списка участников на одной линии с ним.</summary>
    [Fact]
    public void The_instruction_button_is_inline_with_the_senders_list()
    {
        var settings = new DocRegistryExtension().Settings;

        var withButton = Assert.Single(settings, s => s.Button is not null);
        Assert.Equal(DocRegistryExtension.SendersKey, withButton.Key);
        Assert.Equal("Отправить инструкцию", withButton.Button!.Text);
        Assert.True(withButton.Button.Inline);
    }

    [Fact]
    public void Sender_entries_keep_order_and_skip_repeats()
    {
        Assert.Equal([("1001", "1001 Иванов"), ("1002", "1002")],
                     MessageRegistration.SenderEntries("1001 Иванов;\n1002, 1001 Иванов-второй"));
    }
}
