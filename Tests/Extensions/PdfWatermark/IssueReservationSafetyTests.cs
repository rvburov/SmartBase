using Microsoft.EntityFrameworkCore;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Extensions.PdfWatermark;

namespace SmartBase.Tests.Extensions.PdfWatermark;

public sealed class IssueReservationSafetyTests
{
    [Fact]
    public void Pinned_batch_sees_a_foreign_reservation_between_two_files()
    {
        using var database = new TempDatabase("issue-pinned");
        var collection = IssueStand.Create(database.Constructor);
        var shape = IssueStand.Shape(database.Constructor, collection);
        using var source = new TempDir("issue-source");
        using var output = new TempDir("issue-output");
        var files = new[] { "первый", "второй" }.Select(folder =>
        {
            var directory = Directory.CreateDirectory(Path.Combine(source.Path, folder));
            var path = Path.Combine(directory.FullName, "Общий документ.pdf");
            PdfStand.WriteA4(path);
            return path;
        }).ToArray();
        Issue? foreign = null;
        var request = new StampRequest(source.Path, true, output.Path, "Первый получатель",
            new DateTime(2026, 10, 26), "Тестовая выдача", database.Path, shape);
        var progress = new ImmediateProgress(update =>
        {
            if (update.Current != 2) return;
            using var context = new AppDbContext(database.Path);
            foreign = IssueJournal.Reserve(new ConstructorRepository(context), shape,
                new IssueInput("Общий документ", "Другой получатель", request.Date));
        });

        var result = PdfWatermarkEngine.Run(request, files, progress, CancellationToken.None);

        Assert.Equal((2, 0, 0), (result.Done, result.Failed, result.Skipped));
        Assert.NotNull(foreign);
        Assert.Equal(2, foreign.Number);
        Assert.Equal([1, 3], result.Items.Select(item => item.Number));
        Assert.Equal(3, database.Constructor.GetRecords(collection).Count);
        Assert.Equal([1, 2, 3], IssueJournal.TakenFor(database.Constructor, shape, "Общий документ"));
        Assert.All(result.Items, item => Assert.Equal(1,
            PdfStand.PageCount(Path.Combine(output.Path, item.OutputName!))));
    }

    [Fact]
    public void A_late_field_failure_rolls_back_the_reservation_and_does_not_occupy_its_number()
    {
        using var database = new TempDatabase("issue-rollback");
        var collection = IssueStand.Create(database.Constructor);
        var shape = IssueStand.Shape(database.Constructor, collection);
        var book = NumberLedger.For(database.Constructor, shape, "Документ");
        var logBefore = database.Context.ChangeLog.AsNoTracking().Count();
        // Последнее поле падает после записи заголовка, номера и получателя. ID из своей
        // схемы — целое число, не ввод человека; параметры в CREATE TRIGGER не разрешены.
        database.Context.Database.ExecuteSqlRaw($"""
            CREATE TRIGGER issue_field_fault BEFORE INSERT ON db_field_values
            WHEN NEW.FieldId = {shape.DateFieldId}
            BEGIN SELECT RAISE(ABORT, 'controlled issue field fault'); END;
            """);
        try
        {
            var error = Assert.ThrowsAny<Exception>(() => IssueJournal.Reserve(database.Constructor, shape,
                new IssueInput("Документ", "Получатель", new DateTime(2026, 10, 26)), book));
            Assert.Contains("controlled issue field fault", error.ToString());
        }
        finally { database.Context.Database.ExecuteSqlRaw("DROP TRIGGER issue_field_fault"); }

        Assert.Empty(database.Constructor.GetRecords(collection));
        Assert.Empty(database.Context.DbFieldValues.AsNoTracking());
        Assert.Equal(logBefore, database.Context.ChangeLog.AsNoTracking().Count());
        Assert.Empty(book.Taken("Документ"));
        var next = IssueJournal.Reserve(database.Constructor, shape,
            new IssueInput("Документ", "Получатель", new DateTime(2026, 10, 26)), book);
        Assert.Equal(1, next.Number);
        Assert.Single(database.Constructor.GetRecords(collection));
    }

    private sealed class ImmediateProgress(Action<StampProgress> receive) : IProgress<StampProgress>
    {
        public void Report(StampProgress value) => receive(value);
    }
}
