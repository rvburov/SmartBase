using System.Text;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Значение для потока COPY в текстовом формате PostgreSQL (перенос коллекции в PG): служебные
/// символы формата экранируются, иначе значение с табуляцией или переводом строки порвало бы
/// строку потока, а буквальное «\N» прочиталось бы как пустое значение. Числа — без
/// разделителей культуры: программа работает с русской культурой, где дробная часть через запятую.
/// </summary>
public sealed class CopyTextFormatTests
{
    [Theory]
    [InlineData("обычный текст",      "обычный текст")]
    [InlineData("а\tб",               @"а\tб")]
    [InlineData("а\nб\rв",            @"а\nб\rв")]
    [InlineData(@"косая \ одна",      @"косая \\ одна")]
    [InlineData(@"\N",                @"\\N")]
    [InlineData("",                   "")]
    public void Strings_are_escaped(string value, string expected) =>
        Assert.Equal(expected, Copy(value));

    [Fact]
    public void Null_is_marked_as_null()
    {
        Assert.Equal(@"\N", Copy(null));
        Assert.Equal(@"\N", Copy(DBNull.Value));
    }

    [Fact]
    public void Numbers_ignore_the_current_culture()
    {
        var saved = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("ru-RU");
            Assert.Equal("1234567", Copy(1234567));
            Assert.Equal("9876543210", Copy(9876543210L));
            Assert.Equal("1.5", Copy(1.5));
        }
        finally { Thread.CurrentThread.CurrentCulture = saved; }
    }

    private static string Copy(object? value)
    {
        var sb = new StringBuilder();
        ConstructorRepository.AppendCopyValue(sb, value);
        return sb.ToString();
    }
}
