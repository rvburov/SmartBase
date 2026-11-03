using System.Text;
using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

public sealed class ResponseFileNameTests
{
    [Fact]
    public void Quoted_encoded_word_preserves_an_arbitrary_unicode_filename()
    {
        const string original = "Произвольное имя отправителя.xlsx";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(original));

        Assert.Equal(original, ResponseFileName.From([
            new("Content-Disposition", $"attachment; filename=\"=?UTF-8?B?{encoded}?=\""),
        ]));
    }

    [Theory]
    [InlineData("attachment; filename=report.xlsx", "report.xlsx")]
    [InlineData("inline; filename=\"Report draft.xlsx\"", "Report draft.xlsx")]
    [InlineData("attachment; filename=\"Budget%20draft+2026.xlsx\"", "Budget%20draft+2026.xlsx")]
    [InlineData("attachment; filename*=UTF-8''%D0%A1%D0%BF%D0%B8%D1%81%D0%BE%D0%BA%20%D0%9A%D0%94.xlsx", "Список КД.xlsx")]
    [InlineData("attachment; filename*=UTF-8'ru'%D0%9A%D0%94.xlsx; filename=fallback.xlsx", "КД.xlsx")]
    [InlineData("attachment; filename=fallback.xlsx; filename*=ISO-8859-1''caf%E9.xlsx", "café.xlsx")]
    [InlineData("attachment; filename*=unavailable''name.xlsx; filename=fallback.xlsx", "fallback.xlsx")]
    [InlineData("attachment; filename*=UTF-8''..%2Fescape.xlsx; filename=fallback.xlsx", "fallback.xlsx")]
    [InlineData("attachment; filename*=UTF-8''%FF.xlsx; filename=fallback.xlsx", "fallback.xlsx")]
    public void Standard_disposition_preserves_the_name_and_prefers_the_extended_parameter(string disposition, string expected) =>
        Assert.Equal(expected, ResponseFileName.From([new("cOnTeNt-DiSpOsItIoN", disposition)]));

    [Theory]
    [InlineData("attachment")]
    [InlineData("attachment; filename=")]
    [InlineData("attachment; filename=\"\"")]
    [InlineData("attachment; filename=\" \"")]
    [InlineData("attachment; filename=\"../escape.xlsx\"")]
    [InlineData("attachment; filename=\"..\\escape.xlsx\"")]
    [InlineData("attachment; filename=\"C:\\files\\escape.xlsx\"")]
    [InlineData("attachment; filename=\".\"")]
    [InlineData("attachment; filename=\"..\"")]
    [InlineData("attachment; filename=\"report.xlsx.\"")]
    [InlineData("attachment; filename=\"report.xlsx \"")]
    [InlineData("attachment; filename=\"CON.xlsx\"")]
    [InlineData("attachment; filename=\"lpt9.xlsx\"")]
    [InlineData("attachment; filename=\"report?.xlsx\"")]
    [InlineData("attachment; filename*=UTF-8''file%00.xlsx")]
    [InlineData("attachment; filename*=UTF-8''file%0D%0A.xlsx")]
    [InlineData("not a valid disposition")]
    public void Unsafe_or_missing_names_cannot_replace_the_configured_name(string disposition) =>
        Assert.Equal("", ResponseFileName.From([new("Content-Disposition", disposition)]));

    [Fact]
    public void Missing_or_ambiguous_disposition_cannot_replace_the_configured_name()
    {
        Assert.Equal("", ResponseFileName.From([]));
        Assert.Equal("", ResponseFileName.From([new("Content-Type", "application/octet-stream")]));
        Assert.Equal("", ResponseFileName.From([
            new("Content-Disposition", "attachment; filename=first.xlsx"),
            new("Content-Disposition", "attachment; filename=second.xlsx"),
        ]));
        Assert.Equal("", ResponseFileName.From([
            new("Content-Disposition", "attachment; filename=first.xlsx, attachment; filename=second.xlsx"),
        ]));
        Assert.Equal("", ResponseFileName.From([
            new("Content-Disposition", "attachment; filename=first.xlsx; filename=second.xlsx"),
        ]));
    }

    [Fact]
    public void Name_limit_applies_to_the_decoded_filename()
    {
        var accepted = new string('a', ScenarioStep.MaxTextLength - 5) + ".xlsx";
        Assert.Equal(accepted, ResponseFileName.From([new("Content-Disposition", $"attachment; filename=\"{accepted}\"")]));
        Assert.Equal("", ResponseFileName.From([new("Content-Disposition", $"attachment; filename=\"a{accepted}\"")]));
    }
}
