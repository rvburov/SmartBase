using System.Text;
using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

public sealed class ResponseFileNameTests
{
    [Theory]
    [InlineData(@"\u041F\u0435\u0440\u0435\u0447\u0435\u043D\u044C \u0434\u0435\u0442\u0430\u043B\u0435\u0439.xlsx", "Перечень деталей.xlsx")]
    [InlineData(@"\u0412\u0442\u043e\u0440\u043e\u0439.xlsx", "Второй.xlsx")]
    [InlineData("report\\u0020draft.xlsx", "report draft.xlsx")]
    [InlineData("\"\\u0414, v=2; draft.xlsx\"", "Д, v=2; draft.xlsx")]
    [InlineData(@"\u0414; v=2.xlsx", "Д; v=2.xlsx")]
    [InlineData(@"\u0414\u002c draft.xlsx", "Д, draft.xlsx")]
    [InlineData(@"\u00e9.xlsx", "é.xlsx")]
    [InlineData(@"\uD83D\uDCC4.xlsx", "📄.xlsx")]
    public void A_bare_escaped_filename_is_decoded_once_and_preserves_each_senders_name(string header, string expected) =>
        Assert.Equal(expected, ResponseFileName.From([new("Content-Disposition", header)]));

    [Theory]
    [InlineData("Report.xlsx", "Report.xlsx")]
    [InlineData("Report draft.xlsx", "Report draft.xlsx")]
    [InlineData("\"Report draft.xlsx\"", "Report draft.xlsx")]
    [InlineData("\"Report, v=2; draft.xlsx\"", "Report, v=2; draft.xlsx")]
    [InlineData("%D0%94.xlsx", "%D0%94.xlsx")]
    [InlineData("Budget%20draft+2026.xlsx", "Budget%20draft+2026.xlsx")]
    public void A_bare_ascii_filename_keeps_its_name_without_guessing_an_encoding(string header, string expected) =>
        Assert.Equal(expected, ResponseFileName.From([new("Content-Disposition", header)]));

    [Theory]
    [InlineData(@"\u041G.xlsx")]
    [InlineData(@"\u041.xlsx")]
    [InlineData(@"\u.xlsx")]
    [InlineData(@"\U0414.xlsx")]
    [InlineData(@"\u0414\txlsx")]
    [InlineData(@"\u0000.xlsx")]
    [InlineData(@"\u0414\u000A.xlsx")]
    [InlineData(@"\u002e\u002e\u002fescape.xlsx")]
    [InlineData(@"C\u003a\u005cfile.xlsx")]
    [InlineData(@"\uD800.xlsx")]
    [InlineData(@"\uDC00.xlsx")]
    [InlineData(@"\uD800\u0041.xlsx")]
    [InlineData(@"\uD800\uD800.xlsx")]
    [InlineData(@"\uDC00\uD800.xlsx")]
    [InlineData(@"\u005cu0041.xlsx")]
    [InlineData(@"\\u0414.xlsx")]
    [InlineData(@"\u0043ON.xlsx")]
    [InlineData(@"\u0414.xlsx\u0020")]
    [InlineData(@"\u0414.xlsx\u002e")]
    [InlineData(@"\u0022name.xlsx\u0022")]
    [InlineData(@"attachment; filename=\u0414.xlsx")]
    [InlineData(@"InLiNe ; filename=\u0414.xlsx")]
    [InlineData(@"form-data; name=file; filename=\u0414.xlsx")]
    [InlineData(@"\u0414.xlsx, \u0415.xlsx")]
    [InlineData("\"\\u0414.xlsx\", \"\\u0415.xlsx\"")]
    [InlineData("attachment")]
    [InlineData("inline")]
    [InlineData("form-data")]
    [InlineData("attachment; name=report.xlsx")]
    [InlineData("inline; creation-date=report.xlsx")]
    [InlineData("attachment; filename=")]
    [InlineData("report.xlsx; broken-parameter")]
    [InlineData(".xlsx")]
    [InlineData("Report.")]
    [InlineData("UmVwb3J0Lnhsc3g=")]
    public void Malformed_ambiguous_or_unsafe_bare_names_do_not_replace_the_configured_name(string header) =>
        Assert.Equal("", ResponseFileName.From([new("Content-Disposition", header)]));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Escaped_name_limit_is_applied_after_decoding_including_outer_quotes(bool quoted)
    {
        var encoded = string.Concat(Enumerable.Repeat(@"\u0061", ScenarioStep.MaxTextLength));
        var header = quoted ? "\"" + encoded + "\"" : encoded;

        Assert.True(header.Length > ScenarioStep.MaxTextLength);
        Assert.Equal(new string('a', ScenarioStep.MaxTextLength),
            ResponseFileName.From([new("Content-Disposition", header)]));
        Assert.Equal("", ResponseFileName.From([new("Content-Disposition",
            new string('a', ScenarioStep.MaxTextLength) + @"\u0062")]));
    }

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
