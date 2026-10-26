using SmartBase.Core.Documents;
using Microsoft.EntityFrameworkCore;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Extensions.PdfWatermark;

namespace SmartBase.Tests.Extensions.PdfWatermark;

public sealed class ConcurrentEngineTests
{
    [Fact]
    public void Eight_stamp_jobs_keep_pdf_bytes_isolated_and_write_their_own_journals()
    {
        var paths = ConcurrentTasks.Run(8, index => new Fixture(index), (f, index, ct) =>
        {
            var request = new StampRequest(f.Source, false, f.Output, $"Получатель {index} Тестовый Пользователь",
                new DateTime(2026, 10, 26), "Передача тестовой копии третьим лицам запрещена", f.Database.Path, f.Shape);
            var files = PdfWatermarkEngine.ListPdfFiles(f.Source, false, ct);
            var result = PdfWatermarkEngine.Run(request, files, null, ct);
            Assert.Equal((1, 1, 0, 0), (result.Total, result.Done, result.Skipped, result.Failed));
            Assert.False(result.Cancelled);
            var item = Assert.Single(result.Items);
            Assert.Equal(1, item.Number);
            Assert.Equal(1, item.Pages);
            var output = Assert.Single(Directory.GetFiles(f.Output));
            Assert.Equal($"Чертеж {index} экз. 1.pdf", Path.GetFileName(output));
            Assert.Equal(1, PdfStand.PageCount(output));
            Assert.Equal(f.Hash, ConcurrentTasks.Fingerprint(f.Source));
            // Рендер проверки тоже проходит штатную общую очередь PDFium.
            PdfGate.Run(() =>
            {
                using var clean = PdfStand.Render(f.Source);
                using var stamped = PdfStand.Render(output);
                Assert.True(PdfStand.DiffShare(stamped, clean) > 1, "На готовой странице нет видимого знака");
            });
            using var check = new AppDbContext(f.Database.Path);
            var repo = new ConstructorRepository(check);
            var record = Assert.Single(repo.GetRecords(f.Shape.CollectionId));
            Assert.Equal($"Чертеж {index}", record.Title);
            var values = repo.GetFieldValues(record.Id!.Value);
            Assert.Equal("1", values[f.Shape.CopyFieldId]);
            Assert.Equal(request.Holder, values[f.Shape.HolderFieldId]);
            Assert.Equal("2026-10-26", values[f.Shape.DateFieldId]);
            return output;
        }, TimeSpan.FromMinutes(3));
        Assert.Equal(8, paths.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Eight_issuers_of_one_shared_document_publish_distinct_copies_and_journal_rows()
    {
        var journal = new SharedIssueJournal();
        ConcurrentTasks.Run(8, index => new SharedPdfJob(index), (job, index, ct) =>
        {
            var request = new StampRequest(job.Source, false, job.Output, $"Получатель {index} Общего Документа",
                new DateTime(2026, 10, 26), "Передача тестовой копии третьим лицам запрещена", journal.Database.Path, journal.Shape);
            var result = PdfWatermarkEngine.Run(request,
                PdfWatermarkEngine.ListPdfFiles(job.Source, false, ct), null, ct);
            Assert.Equal((1, 1, 0, 0), (result.Total, result.Done, result.Skipped, result.Failed));
            Assert.False(result.Cancelled);
            var item = Assert.Single(result.Items);
            var output = Assert.Single(Directory.GetFiles(job.Output));
            Assert.Equal(item.OutputName, Path.GetFileName(output));
            Assert.Equal(1, PdfStand.PageCount(output));
            Assert.Equal(job.SourceHash, ConcurrentTasks.Fingerprint(job.Source));
            PdfGate.Run(() =>
            {
                using var clean = PdfStand.Render(job.Source);
                using var stamped = PdfStand.Render(output);
                Assert.True(PdfStand.DiffShare(stamped, clean) > 1, "В выданной копии нет видимого знака");
            });
            return new PublishedCopy(index, item.Number, output, request.Holder);
        }, TimeSpan.FromMinutes(3), verify: copies =>
        {
            Assert.All(copies, copy => Assert.NotNull(copy));
            Assert.Equal(Enumerable.Range(1, 8).ToArray(), copies.Select(c => c.Number).Order().ToArray());
            Assert.Equal(8, copies.Select(c => c.Path).Distinct(StringComparer.Ordinal).Count());
            using var check = new AppDbContext(journal.Database.Path);
            var repo = new ConstructorRepository(check);
            var records = repo.GetRecords(journal.Shape.CollectionId);
            Assert.Equal(8, records.Count);
            Assert.All(records, row => Assert.Equal("Общий чертеж", row.Title));
            var values = repo.GetFieldValuesBatch(records.Select(r => r.Id!.Value).ToList());
            var numbers = records.Select(r => int.Parse(values[r.Id!.Value][journal.Shape.CopyFieldId])).Order().ToArray();
            Assert.Equal(Enumerable.Range(1, 8).ToArray(), numbers);
            foreach (var copy in copies)
            {
                var row = Assert.Single(records, r => values[r.Id!.Value][journal.Shape.HolderFieldId] == copy.Holder);
                Assert.Equal(copy.Number.ToString(), values[row.Id!.Value][journal.Shape.CopyFieldId]);
                Assert.Equal("2026-10-26", values[row.Id.Value][journal.Shape.DateFieldId]);
                Assert.Equal($"Общий чертеж экз. {copy.Number}.pdf", Path.GetFileName(copy.Path));
            }
            Assert.Equal(0, check.DbFieldValues.Count(v => !check.DbRecords.Any(r => r.Id == v.RecordId)));
        }, shared: journal);
    }

    [Fact]
    public void Stale_ledgers_of_one_shared_document_cannot_reserve_the_same_copy_number()
    {
        var journal = new SharedIssueJournal();
        // Все ведомости загружаются ДО общего старта: это допустимый публичным API снимок
        // долгого прогона, устаревший из-за другого выдающего. Гонка воспроизводится без
        // случайных задержек и без собственного повтора операций стендом.
        ConcurrentTasks.Run(8, _ => new NumberJob(journal), (job, index, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            using var context = new AppDbContext(journal.Database.Path);
            return IssueJournal.Reserve(new ConstructorRepository(context), journal.Shape,
                new IssueInput("Общий чертеж", $"Получатель {index}", new DateTime(2026, 10, 26)), job.Ledger);
        }, verify: issued =>
        {
            Assert.All(issued, issue => Assert.NotNull(issue));
            using var context = new AppDbContext(journal.Database.Path);
            var repo = new ConstructorRepository(context);
            var records = repo.GetRecords(journal.Shape.CollectionId);
            Assert.Equal(8, records.Count);
            var values = repo.GetFieldValuesBatch(records.Select(r => r.Id!.Value).ToList());
            var numbers = records.Select(r => int.Parse(values[r.Id!.Value][journal.Shape.CopyFieldId])).Order().ToArray();
            Assert.Equal(Enumerable.Range(1, 8).ToArray(), numbers);
            Assert.Equal(Enumerable.Range(1, 8).ToArray(), issued.Select(i => i.Number).Order().ToArray());
        }, shared: journal);
    }

    private sealed record PublishedCopy(int Index, int Number, string Path, string Holder);

    private sealed class SharedIssueJournal : IDisposable
    {
        public TempDatabase Database { get; } = new("shared-issuance");
        public JournalShape Shape { get; }
        public SharedIssueJournal()
        {
            var collection = IssueStand.Create(Database.Constructor);
            Shape = IssueStand.Shape(Database.Constructor, collection);
        }
        public void Dispose() => Database.Dispose();
    }

    private sealed class SharedPdfJob : IDisposable
    {
        private readonly TempDir _dir = new("shared-issuance");
        public string Source { get; }
        public string Output { get; }
        public string SourceHash { get; }
        public SharedPdfJob(int index)
        {
            Source = Path.Combine(_dir.Path, "Общий чертеж.pdf");
            Output = Directory.CreateDirectory(Path.Combine(_dir.Path, $"получатель-{index}")).FullName;
            PdfStand.WriteA4(Source);
            SourceHash = ConcurrentTasks.Fingerprint(Source);
        }
        public void Dispose() => _dir.Dispose();
    }

    private sealed class NumberJob : IDisposable
    {
        public NumberLedger Ledger { get; }
        public NumberJob(SharedIssueJournal journal)
        {
            using var context = new AppDbContext(journal.Database.Path);
            Ledger = NumberLedger.For(new ConstructorRepository(context), journal.Shape, "Общий чертеж");
        }
        public void Dispose() { }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDir _dir = new("concurrent-watermark");
        public TempDatabase Database { get; } = new("concurrent-watermark");
        public string Source { get; }
        public string Output { get; }
        public JournalShape Shape { get; }
        public string Hash { get; }
        public Fixture(int index)
        {
            Source = Path.Combine(_dir.Path, $"Чертеж {index}.pdf");
            Output = Directory.CreateDirectory(Path.Combine(_dir.Path, "результат")).FullName;
            PdfStand.WriteA4(Source);
            Hash = ConcurrentTasks.Fingerprint(Source);
            var collection = IssueStand.Create(Database.Constructor, $"Выдачи {index}");
            Shape = IssueStand.Shape(Database.Constructor, collection, $"Выдачи {index}");
        }
        public void Dispose() { Database.Dispose(); _dir.Dispose(); }
    }
}
