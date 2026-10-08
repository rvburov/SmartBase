using Microsoft.Data.Sqlite;
using SmartBase.Core.Models;
using SmartBase.Database;

namespace SmartBase.Tests.Integration;

/// <summary>
/// След копирования значений таблицы в буфер обмена (<see cref="CopyTrace"/>): строка в журнале
/// безопасности, а не в журнале изменений. Проверяется, что в строке есть все, что нужно
/// проверяющему (столбцы, объем, отпечаток текста, метка), и нет значений, и что без журнала
/// след не ложится — по этому ответу таблица не кладет текст в буфер.
///
/// Набор идет в общей очереди шлюза: журнал безопасности один на приложение.
/// </summary>
[Collection(EgressCollection.Name)]
public sealed class CopyTraceTests : IDisposable
{
    private readonly string _journal =
        Path.Combine(Path.GetTempPath(), $"sbcopy_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SecurityJournal.Attach(null);
        try { SqliteGate.ClearAllPools(); } catch { /* best-effort */ }

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(_journal + suffix)) File.Delete(_journal + suffix); }
            catch { /* временный файл */ }
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private SecurityJournal Attach()
    {
        var journal = new SecurityJournal(_journal);
        SecurityJournal.Attach(journal);
        return journal;
    }

    [Fact]
    public void Copy_leaves_one_row_without_values()
    {
        var journal = Attach();
        const string text = "Заказ 1\t100\nЗаказ 2\t250";

        Assert.True(CopyTrace.Note(3, "Заказы", ["Название", "Сумма"], rows: 2, cells: 4,
                                   text, ConfidentialityLabel.Open, Now));

        var row = journal.Recent().Single();
        Assert.Equal(SecurityEvent.Copy, row.Event);
        Assert.True(row.Allowed);
        Assert.Equal("буфер обмена", row.Address);
        Assert.Equal("Название, Сумма", row.Fields);
        Assert.Equal(2, row.Rows);
        Assert.Equal(3, row.DbId);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(text), row.Bytes);
        Assert.Equal(SecurityJournal.Sha256(text), row.BodyHash);
        Assert.Contains("«Заказы»", row.Explanation);
        Assert.Contains("ячеек: 4", row.Explanation);
        // Значений в журнале нет — только имена и отпечаток.
        Assert.DoesNotContain("250", row.Explanation + row.Fields + row.Address);
        Assert.Equal(0, journal.Verify());
    }

    /// <summary>Метка закрытой базы стоит в пояснении первой — ее ищут взглядом.</summary>
    [Fact]
    public void Strict_label_is_named_in_the_explanation()
    {
        var journal = Attach();

        CopyTrace.Note(1, "Кадры", ["ФИО"], 1, 1, "Иванов", ConfidentialityLabel.Closed, Now);

        Assert.StartsWith(Confidentiality.Title(ConfidentialityLabel.Closed), journal.Recent().Single().Explanation);
    }

    /// <summary>Журнал не открылся — следа нет, и таблица по этому ответу не копирует.</summary>
    [Fact]
    public void Without_a_journal_nothing_is_noted()
    {
        SecurityJournal.Attach(null);

        Assert.False(CopyTrace.Ready);
        Assert.False(CopyTrace.Note(1, "Заказы", ["Название"], 1, 1, "x", ConfidentialityLabel.Open, Now));
    }
}
