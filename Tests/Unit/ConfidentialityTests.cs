using System.Text.Json;
using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Метки конфиденциальности (<see cref="Confidentiality"/>): чтение, запись и правило
/// «непонятная метка — самая строгая».
///
/// Последнее и есть смысл файла. У прочих настроек свойства сломанный конфиг откатывается к
/// значению по умолчанию, и это правильно: колонка ведет себя чуть иначе, чем задумано. Здесь
/// же «по умолчанию» означало бы разрешение отправить данные наружу — а сломанный конфиг или
/// пометка из более новой версии программы не повод это разрешать.
/// </summary>
public sealed class ConfidentialityTests
{
    // ── Разбор хранимого значения ─────────────────────────────────────────────

    [Theory]
    [InlineData("open",       ConfidentialityLabel.Open)]
    [InlineData("restricted", ConfidentialityLabel.Restricted)]
    [InlineData("closed",     ConfidentialityLabel.Closed)]
    [InlineData("CLOSED",     ConfidentialityLabel.Closed)]
    [InlineData("  closed  ", ConfidentialityLabel.Closed)]
    public void Known_values_are_parsed(string stored, ConfidentialityLabel expected) =>
        Assert.Equal(expected, Confidentiality.Parse(stored));

    /// <summary>Метки нет — это обычное состояние базы, а не потеря данных.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Absent_label_reads_as_open(string? stored) =>
        Assert.Equal(ConfidentialityLabel.Open, Confidentiality.Parse(stored));

    /// <summary>А вот пометка, которая есть, но не разобралась, — самая строгая.</summary>
    [Theory]
    [InlineData("secret")]
    [InlineData("совершенно секретно")]
    [InlineData("open-ish")]
    public void Unknown_label_reads_as_closed(string stored) =>
        Assert.Equal(ConfidentialityLabel.Closed, Confidentiality.Parse(stored));

    [Fact]
    public void Storage_round_trips()
    {
        foreach (var label in Enum.GetValues<ConfidentialityLabel>())
            Assert.Equal(label, Confidentiality.Parse(Confidentiality.ToStorage(label)));
    }

    // ── Наследование ──────────────────────────────────────────────────────────

    /// <summary>Наследование только сужает: строже базы поле быть может, свободнее — нет.</summary>
    [Fact]
    public void Strictest_of_two_wins()
    {
        Assert.Equal(ConfidentialityLabel.Closed,
            Confidentiality.Strictest(ConfidentialityLabel.Open, ConfidentialityLabel.Closed));
        Assert.Equal(ConfidentialityLabel.Restricted,
            Confidentiality.Strictest(ConfidentialityLabel.Restricted, ConfidentialityLabel.Open));
        Assert.Equal(ConfidentialityLabel.Open,
            Confidentiality.Strictest(ConfidentialityLabel.Open, ConfidentialityLabel.Open));
    }

    // ── Метка свойства в ConfigJson ───────────────────────────────────────────

    [Fact]
    public void Field_without_a_label_is_open()
    {
        Assert.Equal(ConfidentialityLabel.Open, Confidentiality.FromConfig(null));
        Assert.Equal(ConfidentialityLabel.Open, Confidentiality.FromConfig("{}"));
        Assert.Equal(ConfidentialityLabel.Open, Confidentiality.FromConfig("""{"format":"0.00"}"""));
    }

    [Fact]
    public void Field_label_is_read_from_config()
    {
        Assert.Equal(ConfidentialityLabel.Closed,
            Confidentiality.FromConfig("""{"confidentiality":"closed"}"""));
        Assert.Equal(ConfidentialityLabel.Restricted,
            Confidentiality.FromConfig("""{"format":"0.00","confidentiality":"restricted"}"""));
    }

    [Theory]
    [InlineData("не json вовсе")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"confidentiality":42}""")]
    public void Broken_config_reads_as_closed(string configJson) =>
        Assert.Equal(ConfidentialityLabel.Closed, Confidentiality.FromConfig(configJson));

    // ── Запись метки ──────────────────────────────────────────────────────────

    /// <summary>
    /// Метка живет рядом с настройками типа свойства, а не вместо них: запись метки не должна
    /// стирать формат числа, набор опций списка и все прочее.
    /// </summary>
    [Fact]
    public void Writing_a_label_keeps_the_other_settings()
    {
        const string config = """{"format":"0.00","options":["А","Б"]}""";

        var updated = Confidentiality.WriteToConfig(config, ConfidentialityLabel.Closed);

        using var document = JsonDocument.Parse(updated);
        Assert.Equal("0.00", document.RootElement.GetProperty("format").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("options").GetArrayLength());
        Assert.Equal(ConfidentialityLabel.Closed, Confidentiality.FromConfig(updated));
    }

    /// <summary>«Открыто» — не значение, а его отсутствие: ключ убирается.</summary>
    [Fact]
    public void Open_removes_the_key()
    {
        var updated = Confidentiality.WriteToConfig("""{"confidentiality":"closed","format":"0.00"}""",
                                                    ConfidentialityLabel.Open);

        using var document = JsonDocument.Parse(updated);
        Assert.False(document.RootElement.TryGetProperty(Confidentiality.Key, out _));
        Assert.Equal("0.00", document.RootElement.GetProperty("format").GetString());
    }

    [Fact]
    public void Label_written_into_an_empty_config_reads_back()
    {
        var updated = Confidentiality.WriteToConfig(null, ConfidentialityLabel.Restricted);

        Assert.Equal(ConfidentialityLabel.Restricted, Confidentiality.FromConfig(updated));
    }

    /// <summary>
    /// Сломанный конфиг переписывается целиком: восстановить из него нечего, а оставить поле
    /// без метки нельзя — метку ставят осознанно.
    /// </summary>
    [Fact]
    public void Broken_config_is_rewritten_from_scratch()
    {
        var updated = Confidentiality.WriteToConfig("не json вовсе", ConfidentialityLabel.Closed);

        Assert.Equal(ConfidentialityLabel.Closed, Confidentiality.FromConfig(updated));
    }

    // ── Подписи ───────────────────────────────────────────────────────────────

    [Fact]
    public void Titles_are_filled_for_every_label()
    {
        foreach (var label in Enum.GetValues<ConfidentialityLabel>())
            Assert.False(string.IsNullOrWhiteSpace(Confidentiality.Title(label)));
    }
}
