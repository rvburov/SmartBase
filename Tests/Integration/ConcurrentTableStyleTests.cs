using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

public sealed class ConcurrentTableStyleTests
{
    [Theory]
    [InlineData("sqlite", "cell")]
    [InlineData("sqlite-shared", "cell")]
    [InlineData("sqlite", "row")]
    [InlineData("sqlite-shared", "row")]
    [InlineData("sqlite", "column")]
    [InlineData("sqlite-shared", "column")]
    public async Task A_style_write_waits_its_turn_even_when_the_native_database_is_unlocked(string profile, string level)
    {
        using var fixture = new MultiUserTestDatabase(profile);
        var (collection, record, key) = Seed(fixture);
        var queue = WriteAdmission.ForDatabase("sqlite:" + System.IO.Path.GetFullPath(fixture.Path), 1);
        using var holder = queue.Enter(TimeSpan.Zero);
        var write = Task.Run(() =>
        {
            using var context = fixture.OpenContext();
            context.Database.SetCommandTimeout(5);
            var repo = new ConstructorRepository(context);
            if (level == "cell") repo.SetCellStyle(collection, [(record, key)], StyleAspect.Text, "#334455");
            else if (level == "row") repo.SetRowStyle(collection, [record], StyleAspect.Text, "#334455");
            else repo.SetColumnStyle(collection, [key], StyleAspect.Text, "#334455");
        });
        var queued = false;
        var untouched = false;
        try
        {
            // У держателя только очередь, не SQLite lock. Старый одиночный SaveChanges
            // мог записать ячейку сразу, минуя уже ожидающих пользователей.
            Assert.True(SpinWait.SpinUntil(() => queue.WaitingCount == 1 || write.IsCompleted,
                TimeSpan.FromSeconds(3)), "Запись не дошла ни до очереди, ни до завершения");
            queued = queue.WaitingCount == 1;
            using var check = fixture.OpenContext();
            untouched = !check.CellStyles.Any() && !check.RecordDisplay.Any()
                && !check.ColumnMeta.Any();
        }
        finally
        {
            holder.Dispose();
            await write.WaitAsync(TimeSpan.FromSeconds(8));
        }
        Assert.True(queued, "Одиночная запись оформления обошла очередь базы");
        Assert.True(untouched, "Оформление сохранилось до предоставления очереди записи");
        Assert.Equal(0, queue.WaitingCount);
        using var read = fixture.OpenContext();
        var values = level switch
        {
            "cell" => read.CellStyles.Select(style => style.Fg).ToList(),
            "row" => read.RecordDisplay.Select(style => style.Fg).ToList(),
            _ => read.ColumnMeta.Select(style => style.Fg).ToList(),
        };
        Assert.Equal("#334455", Assert.Single(values));
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void Row_column_and_cell_styles_join_the_caller_transaction_and_rollback_together(string profile)
    {
        using var fixture = new MultiUserTestDatabase(profile);
        var (collection, record, key) = Seed(fixture);
        using var context = fixture.OpenContext();
        var repo = new ConstructorRepository(context);
        using (var transaction = context.Database.BeginTransaction())
        {
            repo.SetColumnStyle(collection, [key], StyleAspect.Background, "#112233");
            repo.SetRowStyle(collection, [record], StyleAspect.Text, "#334455");
            repo.SetCellStyle(collection, [(record, key)], StyleAspect.HAlign, "right");
            Assert.Same(transaction, context.Database.CurrentTransaction);
            Assert.Equal("#112233", repo.GetColumnStyles(collection)[key].Bg);
            Assert.Equal("#334455", repo.GetRowStyles(collection)[record].Fg);
            Assert.Equal(CellHAlign.Right, repo.GetCellStylesBatch(collection, [record])[record][key].HAlign);
            transaction.Rollback();
        }
        using var check = fixture.OpenContext();
        Assert.Empty(check.CellStyles);
        Assert.Empty(check.RecordDisplay);
        Assert.Empty(check.ColumnMeta);
        Assert.True(check.DbRecords.Any(row => row.Id == record));
        MultiUserTestDatabase.AssertHealthy(check);
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void A_cell_cleared_by_another_context_can_be_edited_without_a_phantom_key(string profile)
    {
        using var fixture = new MultiUserTestDatabase(profile);
        var (collection, record, key) = Seed(fixture);
        fixture.Repo.SetCellStyle(collection, [(record, key)], StyleAspect.Background, "#112233");
        using (var neighbour = fixture.OpenContext())
            new ConstructorRepository(neighbour).SetColumnStyle(collection, [key], StyleAspect.Background, "#445566");

        // Тот же старый контекст, без Clear/Detach тестом: прежняя cell_style уже удалена.
        fixture.Repo.SetCellStyle(collection, [(record, key)], StyleAspect.Text, "#778899");
        using var check = fixture.OpenContext();
        var cell = Assert.Single(check.CellStyles.AsNoTracking());
        Assert.Equal(record, cell.RecordId);
        Assert.Equal(key, cell.Key);
        Assert.Null(cell.Bg);
        Assert.Equal("#778899", cell.Fg);
        Assert.Equal("#445566", Assert.Single(check.ColumnMeta.AsNoTracking()).Bg);
        MultiUserTestDatabase.AssertHealthy(check);
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void A_failed_cell_reset_rolls_back_the_column_change_and_the_context_can_retry(string profile)
    {
        using var fixture = new MultiUserTestDatabase(profile);
        var (collection, record, key) = Seed(fixture);
        fixture.Repo.SetColumnStyle(collection, [key], StyleAspect.Background, "#112233");
        fixture.Repo.SetCellStyle(collection, [(record, key)],
            [(StyleAspect.Background, "#445566"), (StyleAspect.Text, "#778899")]);
        var context = fixture.Manager.Context;
        if (context.IsPostgres)
            context.Database.ExecuteSqlRaw("""
                CREATE FUNCTION reject_cell_reset() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD.bg IS NOT NULL AND NEW.bg IS NULL THEN RAISE EXCEPTION 'blocked style reset'; END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER reject_cell_reset BEFORE UPDATE OF bg ON cell_style
                FOR EACH ROW EXECUTE FUNCTION reject_cell_reset();
                """);
        else
            context.Database.ExecuteSqlRaw("""
                CREATE TRIGGER reject_cell_reset BEFORE UPDATE OF Bg ON cell_style
                WHEN OLD.Bg IS NOT NULL AND NEW.Bg IS NULL
                BEGIN SELECT RAISE(ABORT, 'blocked style reset'); END;
                """);

        var error = Record.Exception(() => fixture.Repo.SetColumnStyle(collection, [key], StyleAspect.Background, "#99AABB"));
        Assert.NotNull(error);
        Assert.Contains("blocked style reset", error.GetBaseException().Message);
        using (var check = fixture.OpenContext())
        {
            Assert.Equal("#112233", Assert.Single(check.ColumnMeta.AsNoTracking()).Bg);
            var cell = Assert.Single(check.CellStyles.AsNoTracking());
            Assert.Equal("#445566", cell.Bg);
            Assert.Equal("#778899", cell.Fg);
        }
        context.Database.ExecuteSqlRaw(context.IsPostgres
            ? "DROP TRIGGER reject_cell_reset ON cell_style" : "DROP TRIGGER reject_cell_reset");
        fixture.Repo.SetColumnStyle(collection, [key], StyleAspect.Background, "#99AABB");
        using var after = fixture.OpenContext();
        Assert.Equal("#99AABB", Assert.Single(after.ColumnMeta.AsNoTracking()).Bg);
        var preserved = Assert.Single(after.CellStyles.AsNoTracking());
        Assert.Null(preserved.Bg);
        Assert.Equal("#778899", preserved.Fg);
        MultiUserTestDatabase.AssertHealthy(after);
    }

    private static (int Collection, int Record, string Key) Seed(MultiUserTestDatabase fixture)
    {
        var collection = SampleData.AddCollection(fixture.Repo, "Оформление").Id!.Value;
        var field = SampleData.AddField(fixture.Repo, collection, "Значение").Id!.Value;
        var record = SampleData.AddRecord(fixture.Repo, collection, "Запись").Id!.Value;
        return (collection, record, field.ToString());
    }
}
