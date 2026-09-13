using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Квитанция (<see cref="InboxReceipt"/>): что уходит отправителю и что читается обратно.
///
/// У папки-приемника нет обратной связи по своей природе, и квитанция — единственное место, где
/// написано, что стало с файлом. Она же служит памятью о принятых сообщениях, поэтому чтение
/// собственной квитанции проверяется наравне с записью.
/// </summary>
public sealed class InboxReceiptTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 10, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Accepted_receipt_carries_the_numbers_of_the_apply()
    {
        var receipt = InboxReceipt.Accept("заказы.json", "x1", "Заказы", 10,
            new BindingResult(7, 2, 0, 1, 0), At);

        Assert.True(receipt.Accepted);
        Assert.Equal(10, receipt.Received);
        Assert.Equal(7,  receipt.Created);
        Assert.Equal(2,  receipt.Updated);
        Assert.Equal(1,  receipt.Ambiguous);
    }

    [Fact]
    public void Rejected_receipt_names_the_reason()
    {
        var receipt = InboxReceipt.Rejected("заказы.json", "x1", "Заказы", 10,
            "коллекция «Заказы» не найдена", At);

        Assert.False(receipt.Accepted);
        Assert.Contains("не найдена", receipt.Summary(), StringComparison.Ordinal);
        Assert.Equal(0, receipt.Created);
    }

    /// <summary>Записанная квитанция читается обратно: она же и память о принятых сообщениях.</summary>
    [Fact]
    public void Receipt_survives_a_round_trip()
    {
        var written = InboxReceipt.Accept("заказы.json", "2026-09-10-0042", "Заказы", 3,
            new BindingResult(3, 0, 0, 0, 0), At);

        var read = InboxReceipt.TryParse(written.ToJson());

        Assert.NotNull(read);
        Assert.Equal(written, read);
    }

    /// <summary>
    /// Имя коллекции и причина отказа читаются человеком, поэтому кириллица в квитанции остается
    /// кириллицей, а не превращается в набор escape-последовательностей.
    /// </summary>
    [Fact]
    public void Cyrillic_stays_readable_in_the_file()
    {
        var json = InboxReceipt.Rejected("заказы.json", "x1", "Заказы", 0,
            "коллекция не найдена", At).ToJson();

        Assert.Contains("Заказы", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0417", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Битая квитанция НЕ считается отсутствующей: она все равно означает, что сообщение с таким
    /// идентификатором уже приходило. Обработать его второй раз хуже, чем не понять подробности
    /// прошлого раза, — поэтому наличие файла проверяет приемник, а не разбор.
    /// </summary>
    [Fact]
    public void Broken_receipt_reads_as_nothing_but_still_exists()
    {
        Assert.Null(InboxReceipt.TryParse("{ это не json"));
        Assert.Null(InboxReceipt.TryParse(""));
    }

    [Fact]
    public void Summary_says_nothing_changed_when_nothing_did()
    {
        var receipt = InboxReceipt.Accept("заказы.json", "x1", "Заказы", 0,
            BindingResult.Nothing, At);

        Assert.Contains("изменений нет", receipt.Summary(), StringComparison.Ordinal);
    }

    /// <summary>Квитанция удаления: «удалено» — своим полем, ненайденное — в «пропущено»,
    /// и старый разбор без поля «deleted» ее все равно прочитает (умолчание — ноль).</summary>
    [Fact]
    public void Erased_receipt_counts_deletions_and_survives_a_round_trip()
    {
        var receipt = InboxReceipt.Erased("чистка.json", "x9", "Заказы", 5,
                                          deleted: 3, missing: 1, ambiguous: 1, empty: 0, At);

        Assert.True(receipt.Accepted);
        Assert.Equal(3, receipt.Deleted);
        Assert.Equal(1, receipt.Skipped);
        Assert.Contains("удалено 3", receipt.Summary(), StringComparison.Ordinal);
        Assert.Contains("\"deleted\"", receipt.ToJson(), StringComparison.Ordinal);
        Assert.Equal(receipt, InboxReceipt.TryParse(receipt.ToJson()));
    }

    /// <summary>Квитанция подготовки коллекции: записей нет, вся суть — словами.</summary>
    [Fact]
    public void Ensured_receipt_speaks_in_words()
    {
        var receipt = InboxReceipt.Ensured("схема.json", "x2", "Заказы",
                                           "коллекция «Заказы» создана, полей добавлено: 2", At);

        Assert.True(receipt.Accepted);
        Assert.Equal(0, receipt.Received);
        Assert.Equal("схема.json: коллекция «Заказы» создана, полей добавлено: 2",
                     receipt.Summary());
    }

    [Fact]
    public void Time_is_written_in_a_machine_readable_form()
    {
        var receipt = InboxReceipt.Accept("заказы.json", "x1", "Заказы", 1,
            new BindingResult(1, 0, 0, 0, 0), At);

        Assert.True(DateTimeOffset.TryParse(receipt.At,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed));

        Assert.Equal(At, parsed);
    }
}
