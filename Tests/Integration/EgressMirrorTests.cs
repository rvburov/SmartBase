using System.Text.Json;
using SmartBase.Database;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Зеркало исходящего (<see cref="EgressMirror"/>, docs/ROADMAP-API.md, 10.1).
///
/// Главное, за чем тут следят, — что зеркала НЕТ, пока его не назвала организация: копии
/// секретных данных не должны разбрасываться по машине без указания. Второе — что копия несет
/// то, чего нет в журнале безопасности: само тело. Ради этого зеркало и заведено.
/// </summary>
[Collection(EgressCollection.Name)]
public sealed class EgressMirrorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 14, 30, 0, TimeSpan.Zero);

    private readonly string _root = Directory.CreateTempSubdirectory("sbmirror_").FullName;

    public void Dispose()
    {
        EgressGate.Close();
        try { Directory.Delete(_root, recursive: true); } catch { /* временная папка */ }
    }

    private void Policy(params string[] mirrors)
    {
        EgressGate.AttachPolicy(new EgressPolicySet(
            [.. mirrors.Select((m, i) => new EgressPolicy(
                EgressMode.Internal, [], [], [], 1000, 1024 * 1024, 60, null, false,
                $"источник {i + 1}", "", false, m))]));
    }

    private static IntegrationRequest Request() => IntegrationRequest.New() with
    {
        Name   = "Отдать итог",
        Method = RequestMethod.Post,
        Path   = "/result",
    };

    private static int Note(string body) =>
        EgressMirror.Note(Request(), "https://api.zavod.local/result", body,
                          rows: 2, fields: "Номер, Статус", ok: true,
                          who: "Петров П. П.", scenario: "", now: Now);

    private IReadOnlyList<string> Copies(string root) =>
        Directory.Exists(root)
            ? [.. Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)]
            : [];

    // ── По умолчанию зеркала нет ──────────────────────────────────────────────

    /// <summary>
    /// Политика не назвала папку — копий нет вовсе. Это состояние по умолчанию, и оно
    /// безопасное: разбрасывать копии секретных данных по машине без указания нельзя.
    /// </summary>
    [Fact]
    public void Without_a_named_folder_no_copies_are_kept()
    {
        EgressGate.Close();

        Assert.False(EgressMirror.Configured);
        Assert.Equal(0, Note("""{"итог":1}"""));
    }

    // ── Копия ─────────────────────────────────────────────────────────────────

    [Fact]
    public void A_copy_carries_the_body_that_the_journal_never_keeps()
    {
        Policy(_root);

        Assert.True(EgressMirror.Configured);
        Assert.Equal(1, Note("""{"номер":"А-1","сумма":100}"""));

        var file = Assert.Single(Copies(_root));
        var copy = JsonDocument.Parse(File.ReadAllText(file)).RootElement;

        Assert.Equal("Отдать итог",                    copy.GetProperty("request").GetString());
        Assert.Equal("https://api.zavod.local/result", copy.GetProperty("address").GetString());
        Assert.Equal("Номер, Статус",                  copy.GetProperty("fields").GetString());
        Assert.Equal(2,                                copy.GetProperty("rows").GetInt32());
        Assert.True(copy.GetProperty("ok").GetBoolean());

        // То, ради чего зеркало и заведено: САМО тело, которого в журнале безопасности нет.
        Assert.Contains("А-1", copy.GetProperty("body").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>Отпечаток тела сходится с тем, что пишется в журнал: сверять их будут глазами.</summary>
    [Fact]
    public void The_hash_in_the_copy_matches_the_one_in_the_journal()
    {
        Policy(_root);

        const string body = """{"итог":1}""";
        Note(body);

        var copy = JsonDocument.Parse(File.ReadAllText(Copies(_root).Single())).RootElement;

        Assert.Equal(SecurityJournal.Sha256(body), copy.GetProperty("bodyHash").GetString());
    }

    /// <summary>Раскладка помесячная: в одной папке за год не открыть десятки тысяч файлов.</summary>
    [Fact]
    public void Copies_are_laid_out_by_month()
    {
        Policy(_root);
        Note("""{"итог":1}""");

        Assert.True(Directory.Exists(Path.Combine(_root, "2026-09")));
    }

    /// <summary>Недописанных файлов в папке не остается: запись идет через переименование.</summary>
    [Fact]
    public void No_half_written_files_are_left_behind()
    {
        Policy(_root);
        Note("""{"итог":1}""");

        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    /// <summary>
    /// Два источника политики назвали разные папки — пишем в обе. Пересечения тут нет
    /// намеренно: зеркало не разрешение, а указание, и каждая инстанция хочет свою копию.
    /// </summary>
    [Fact]
    public void Two_named_folders_both_get_a_copy()
    {
        var second = Path.Combine(_root, "вторая");
        Policy(Path.Combine(_root, "первая"), second);

        Assert.Equal(2, Note("""{"итог":1}"""));
        Assert.Single(Copies(Path.Combine(_root, "первая")));
        Assert.Single(Copies(second));
    }

    /// <summary>
    /// Папка недоступна — отправку это не отменяет: зеркало не замок, замком служит журнал
    /// безопасности. Но и молча теряться копия не должна, поэтому ответ честный: легло ноль.
    /// </summary>
    [Fact]
    public void An_unreachable_folder_is_reported_rather_than_hidden()
    {
        // Файл вместо папки: создать в нем подпапку нельзя ни на одной системе.
        var blocker = Path.Combine(_root, "занято");
        File.WriteAllText(blocker, "не папка");

        Policy(blocker);

        Assert.Equal(0, Note("""{"итог":1}"""));
    }
}
