using System.Text.Json;
using SmartBase.Database;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Совсем простой канал: <c>SmartBase --inbox сообщение.json</c>
/// (<see cref="InboxCommand"/>, docs/ROADMAP-API.md, раздел 7).
///
/// Проверяется его половина до базы — разбор ключей и проверка сообщения. Она же самая ломкая:
/// канал зовет планировщик задач, читая КОД ВОЗВРАТА, и «не с чего было начать» обязано
/// отличаться от «отклонено» кодом, а не текстом, который никто не читает. Вторая половина —
/// укладка — общая с сетевым каналом и проверена там же.
/// </summary>
public sealed class InboxCommandTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sbcmd_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка */ }
    }

    private string Put(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static (int Code, string Text) Run(params string[] args)
    {
        var output = new StringWriter();
        var code   = InboxCommand.Run(args, output);

        return (code, output.ToString());
    }

    /// <summary>Ответ — всегда квитанция, даже у отказа: два формата отправителю не нужны.</summary>
    private static JsonElement Receipt(string text) => JsonDocument.Parse(text).RootElement;

    [Fact]
    public void The_channel_is_recognized_by_its_flag()
    {
        Assert.True(InboxCommand.Wanted([InboxCommand.Flag, "файл.json"]));
        Assert.False(InboxCommand.Wanted(["--install"]));
        Assert.False(InboxCommand.Wanted([]));
    }

    [Fact]
    public void Without_a_file_the_channel_says_what_it_wanted()
    {
        var (code, text) = Run(InboxCommand.Flag);

        Assert.Equal(InboxCommand.Failed, code);
        Assert.Contains(InboxCommand.Flag, Receipt(text).GetProperty("explanation").GetString()!,
                        StringComparison.Ordinal);
    }

    /// <summary>Ключ без значения — это ключ без значения, а не имя файла «--db».</summary>
    [Fact]
    public void A_flag_does_not_swallow_the_next_flag_as_its_value()
    {
        var (code, text) = Run(InboxCommand.Flag, InboxCommand.DatabaseFlag, "Основная");

        Assert.Equal(InboxCommand.Failed, code);
        Assert.Contains("Не указан файл", Receipt(text).GetProperty("explanation").GetString()!,
                        StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_file_is_named()
    {
        var (code, text) = Run(InboxCommand.Flag, Path.Combine(_dir, "нет-такого.json"));

        Assert.Equal(InboxCommand.Failed, code);
        Assert.Contains("не найден", Receipt(text).GetProperty("explanation").GetString()!,
                        StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_is_not_json_is_refused_with_a_reason()
    {
        var (code, text) = Run(InboxCommand.Flag, Put("мусор.json", "это не json"));

        Assert.Equal(InboxCommand.Failed, code);
        Assert.False(Receipt(text).GetProperty("accepted").GetBoolean());
    }

    /// <summary>
    /// Проверка формата идет ДО открытия базы: сообщение без номера в режиме «add» не должно
    /// добираться до записей, а отказ по нему не должен зависеть от того, открылась ли база.
    /// </summary>
    [Fact]
    public void A_message_without_an_id_is_refused_before_any_database_is_touched()
    {
        var path = Put("заказы.json", """
            { "collection": "Заказы", "mode": "add", "rows": [ { "Название": "Молоток" } ] }
            """);

        var (code, text) = Run(InboxCommand.Flag, path);

        Assert.Equal(InboxCommand.Failed, code);
        Assert.Contains("id", Receipt(text).GetProperty("explanation").GetString()!,
                        StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_message_is_refused()
    {
        var path = Put("пусто.json", """
            { "collection": "Заказы", "mode": "add", "id": "m1", "rows": [] }
            """);

        var (code, _) = Run(InboxCommand.Flag, path);

        Assert.Equal(InboxCommand.Failed, code);
    }

    /// <summary>
    /// Файл ЗАВЕДОМО больше потолка сетевого тела разбирается, а не отвергается по размеру.
    ///
    /// Канал этот заводят под ночную выгрузку из планировщика — под самые большие файлы, какие
    /// вообще приходят, — и отказывать им по размеру значило бы отказать по сути. Раньше отказ
    /// был: файл читался в строку целиком, и потолок был обязателен.
    ///
    /// Сообщение здесь негодное НАРОЧНО («update» без колонки ключа): так проверка не доходит до
    /// базы данных и не зависит от того, какая база у машины по умолчанию. Важно ровно одно — что
    /// отказ пришел по СОДЕРЖАНИЮ, а не по размеру, то есть файл был прочитан целиком.
    /// </summary>
    [Fact]
    public void A_file_over_the_network_body_cap_is_still_read()
    {
        var path = Path.Combine(_dir, "выгрузка.json");

        using (var file = new StreamWriter(path))
        {
            file.Write("""{ "collection": "Заказы", "mode": "update", "rows": [""");

            // Много мелких записей, а не одна огромная: так проверяется потоковое чтение, а не
            // способность буфера дорасти под длинное значение.
            for (var i = 0; i < 700_000; i++)
            {
                if (i > 0) file.Write(',');
                file.Write($$"""{"Номер":"А-{{i}}","Статус":"новый"}""");
            }

            file.Write("] }");
        }

        Assert.True(new FileInfo(path).Length > SmartBase.Integrations.IngressGate.MaxBodyBytes,
                    "файл должен быть заведомо больше потолка сетевого тела");

        var (code, text) = Run(InboxCommand.Flag, path);

        Assert.Equal(InboxCommand.Failed, code);

        var explanation = Receipt(text).GetProperty("explanation").GetString()!;
        Assert.Contains("key", explanation, StringComparison.Ordinal);
        Assert.DoesNotContain("потолк", explanation, StringComparison.Ordinal);
    }

    /// <summary>Коды возврата читает планировщик — они обязаны различаться.</summary>
    [Fact]
    public void The_three_outcomes_are_three_different_codes()
    {
        Assert.NotEqual(InboxCommand.Accepted, InboxCommand.Rejected);
        Assert.NotEqual(InboxCommand.Rejected, InboxCommand.Failed);
        Assert.NotEqual(InboxCommand.Accepted, InboxCommand.Failed);
    }

    [Fact]
    public void The_usage_names_both_flags()
    {
        var usage = InboxCommand.Usage();

        Assert.Contains(InboxCommand.Flag,         usage, StringComparison.Ordinal);
        Assert.Contains(InboxCommand.DatabaseFlag, usage, StringComparison.Ordinal);
    }
}
