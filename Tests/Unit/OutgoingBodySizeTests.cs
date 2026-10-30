using System.Text;
using SmartBase.Core.Exporters;
using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>Технический предел сообщения считается в байтах готового JSON, включая экранирование.</summary>
public sealed class OutgoingBodySizeTests
{
    [Theory]
    [InlineData("text", "Кириллица \"кавычки\"\nновая строка\\путь")]
    [InlineData("number", "1234,5")]
    [InlineData("number", "не число")]
    [InlineData("date", "2026-10-30T09:00")]
    [InlineData("date", "")]
    [InlineData("select", "Первый; Второй")]
    [InlineData("file", "C:\\Папка\\Файл.pdf")]
    [InlineData("file_json", "[{\"path\":\"a.pdf\",\"name\":\"Файл\"}]")]
    [InlineData("date_period", "7200")]
    [InlineData("checkbox", "1")]
    [InlineData("checkbox", "0")]
    [InlineData("user", "Иванов Иван")]
    public void Prepared_row_counter_matches_the_actual_serialized_object_and_resets_between_rows(string type, string value)
    {
        var binding = new OutgoingBinding(1, 1, [new OutgoingField(1, "Поле \"имя\"")], OutgoingShape.Single);
        var columns = new[] { new ExportColumn("1", "Поле", 1, type) };
        using var counter = OutgoingBody.CreateRowSizer(binding, columns);
        foreach (var raw in new[] { value, "", value })
        {
            var row = new[] { raw };
            var actual = OutgoingBody.Build(binding, columns, [row], "Коллекция");
            Assert.Equal(Encoding.UTF8.GetByteCount(actual.Body), counter.Measure(row));
        }
    }

    [Fact]
    public void Exact_message_limit_succeeds_and_one_more_byte_is_rejected()
    {
        var binding = new OutgoingBinding(1, 1, [new OutgoingField(1, "text")], OutgoingShape.Single);
        var columns = new[] { new ExportColumn("1", "Текст", 1, "text") };
        var overhead = Encoding.UTF8.GetByteCount(OutgoingBody.Build(binding, columns, [[""]], "Коллекция").Body);
        var value = new string('x', checked((int)EgressHttp.MaxResponseBytes - overhead));
        var fit = OutgoingBody.Build(binding, columns, [[value]], "Коллекция");
        Assert.Equal(EgressHttp.MaxResponseBytes, Encoding.UTF8.GetByteCount(fit.Body));
        var failure = Assert.ThrowsAny<InvalidOperationException>(() =>
            OutgoingBody.Build(binding, columns, [[value + "x"]], "Коллекция"));
        Assert.Contains("32 МБ", failure.Message, StringComparison.Ordinal);
    }
}
