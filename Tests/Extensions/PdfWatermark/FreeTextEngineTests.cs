using SmartBase.Core.Documents;
using SmartBase.Extensions.PdfWatermark;

namespace SmartBase.Tests.Extensions.PdfWatermark;

/// <summary>Свободный текст: настоящие PDF, отсутствие обращений к базе и сохранность файлов.</summary>
public sealed class FreeTextEngineTests
{
    private const string Text = "Для внутреннего использования\nОтдел разработки";

    [Fact]
    public void Free_text_works_without_a_database_and_is_baked_into_every_page()
    {
        using var src = new TempDir("wm-free-src");
        using var dst = new TempDir("wm-free-dst");
        var source = Path.Combine(src.Path, "чертеж.pdf");
        PdfStand.Write(source, PdfStand.A4, PdfStand.A4Land);
        var original = File.ReadAllBytes(source);

        // Нулевой символ делает путь непригодным для открытия: режим не может даже
        // попытаться создать контекст, а не только отказаться от записи в готовую базу.
        var request = Request(source, dst.Path) with { DbPath = "\0не база", Shape = null };
        var summary = PdfWatermarkEngine.Run(request, [source], null, CancellationToken.None);

        var item = Assert.Single(summary.Items);
        Assert.Equal(1, summary.Done);
        Assert.Equal(0, summary.Failed + summary.Skipped);
        Assert.Equal(0, item.Number);
        Assert.Equal(2, item.Pages);
        Assert.Equal("чертеж водяной знак.pdf", item.OutputName);
        Assert.Equal(original, File.ReadAllBytes(source));

        var output = Path.Combine(dst.Path, item.OutputName!);
        Assert.Equal(2, PdfStand.PageCount(output));
        for (var page = 1; page <= 2; page++)
        {
            using var clean = PdfStand.Render(source, page);
            using var stamped = PdfStand.Render(output, page);
            Assert.True(PdfStand.DiffShare(stamped, clean) > 0.25, $"на странице {page} нет надписи");
            Assert.Equal("", PdfStand.TextOf(output, page));
        }
    }

    [Fact]
    public void Free_text_does_not_create_a_database_file()
    {
        using var src = new TempDir("wm-free-src");
        using var dst = new TempDir("wm-free-dst");
        var source = Path.Combine(src.Path, "чертеж.pdf");
        PdfStand.WriteA4(source);
        var dbPath = Path.Combine(dst.Path, "не создавать.sqlite");

        var summary = PdfWatermarkEngine.Run(Request(source, dst.Path) with { DbPath = dbPath },
                                             [source], null, CancellationToken.None);

        Assert.Equal(1, summary.Done);
        Assert.False(File.Exists(dbPath));
        Assert.Equal(["чертеж водяной знак.pdf"], dst.Names());
    }

    [Fact]
    public void Free_text_keeps_an_existing_journal_unchanged_even_when_it_is_supplied()
    {
        using var db = new TempDatabase("wm-free");
        using var src = new TempDir("wm-free-src");
        using var dst = new TempDir("wm-free-dst");
        var source = Path.Combine(src.Path, "чертеж.pdf");
        PdfStand.WriteA4(source);
        var collection = IssueStand.Create(db.Constructor);
        var shape = IssueStand.Shape(db.Constructor, collection);
        var row = IssueStand.AddRow(db.Constructor, shape, "чертеж", 1,
                                   "Первый получатель", new DateTime(2026, 11, 9));
        var before = db.Constructor.GetFieldValues(row).OrderBy(p => p.Key).ToArray();

        var request = Request(source, dst.Path) with { DbPath = db.Path, Shape = shape };
        var summary = PdfWatermarkEngine.Run(request, [source], null, CancellationToken.None);

        Assert.Equal(1, summary.Done);
        Assert.Equal(0, summary.Items[0].Number);
        var remaining = Assert.Single(db.Constructor.GetRecordTitlesOrdered(collection));
        Assert.Equal(row, remaining.Id);
        Assert.Equal("чертеж", remaining.Title);
        Assert.Equal(before, db.Constructor.GetFieldValues(row).OrderBy(p => p.Key).ToArray());
    }

    [Fact]
    public void Free_text_skips_broken_files_and_continues_without_issuing_numbers()
    {
        using var src = new TempDir("wm-free-src");
        using var dst = new TempDir("wm-free-dst");
        PdfStand.WriteA4(Path.Combine(src.Path, "1 хороший.pdf"));
        PdfStand.WriteBroken(Path.Combine(src.Path, "2 битый.pdf"));
        PdfStand.WriteProtected(Path.Combine(src.Path, "3 закрытый.pdf"));
        PdfStand.WriteA4(Path.Combine(src.Path, "4 хороший.pdf"));
        var files = PdfWatermarkEngine.ListPdfFiles(src.Path, false, CancellationToken.None);

        var summary = PdfWatermarkEngine.Run(Request(src.Path, dst.Path), files, null, CancellationToken.None);

        Assert.Equal(2, summary.Done);
        Assert.Equal(2, summary.Skipped);
        Assert.Equal(0, summary.Failed);
        Assert.All(summary.Items, item => Assert.Equal(0, item.Number));
        Assert.Equal(["1 хороший водяной знак.pdf", "4 хороший водяной знак.pdf"], dst.Names());
    }

    [Fact]
    public void Free_text_cancellation_keeps_completed_files_only()
    {
        using var src = new TempDir("wm-free-src");
        using var dst = new TempDir("wm-free-dst");
        PdfStand.WriteA4(Path.Combine(src.Path, "1 первый.pdf"));
        PdfStand.WriteA4(Path.Combine(src.Path, "2 второй.pdf"));
        var files = PdfWatermarkEngine.ListPdfFiles(src.Path, false, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<StampProgress>(p => { if (p.Current == 2) cts.Cancel(); });

        var summary = PdfWatermarkEngine.Run(Request(src.Path, dst.Path), files, progress, cts.Token);

        Assert.True(summary.Cancelled);
        Assert.Equal(1, summary.Done);
        Assert.Equal(["1 первый водяной знак.pdf"], dst.Names());
    }

    [Fact]
    public void Free_text_preserves_source_and_existing_output_in_the_same_folder()
    {
        using var dir = new TempDir("wm-free");
        var source = Path.Combine(dir.Path, "чертеж.pdf");
        PdfStand.WriteA4(source);
        var before = File.ReadAllBytes(source);
        var occupied = dir.Touch("чертеж водяной знак.pdf");
        var occupiedBefore = File.ReadAllBytes(occupied);

        var summary = PdfWatermarkEngine.Run(Request(source, dir.Path), [source], null, CancellationToken.None);

        Assert.Equal(1, summary.Done);
        Assert.Equal("чертеж водяной знак (2).pdf", summary.Items[0].OutputName);
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Equal(occupiedBefore, File.ReadAllBytes(occupied));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t ")]
    public void Blank_free_text_is_rejected_before_creating_a_database(string text)
    {
        using var dst = new TempDir("wm-free-dst");
        var request = Request("не нужен.pdf", dst.Path) with { FreeText = text, DbPath = "\0не база" };

        var error = Assert.Throws<PdfWatermarkException>(
            () => PdfWatermarkEngine.Run(request, [], null, CancellationToken.None));

        Assert.Equal("Введите текст водяного знака.", error.Message);
        Assert.Empty(dst.Names());
    }

    [Fact]
    public void Excessive_free_text_is_rejected_before_the_run()
    {
        using var dst = new TempDir("wm-free-dst");
        var request = Request("не нужен.pdf", dst.Path) with
        {
            FreeText = new string('А', PdfWatermarkEngine.MaxFreeTextLength + 1),
        };

        var error = Assert.Throws<PdfWatermarkException>(
            () => PdfWatermarkEngine.Run(request, [], null, CancellationToken.None));

        Assert.Contains(PdfWatermarkEngine.MaxFreeTextLength.ToString(), error.Message);
        Assert.Empty(dst.Names());
    }

    [Fact]
    public void Four_free_text_jobs_publish_distinct_files_in_one_shared_output_folder()
    {
        var shared = new SharedFreeSource();
        ConcurrentTasks.Run(4, index => new FreeTextJob($"Надпись независимого задания {index}"),
            (job, _, ct) =>
            {
                var progress = new SyncProgress<StampProgress>(_ =>
                {
                    if (!shared.Start.SignalAndWait(TimeSpan.FromSeconds(30), ct))
                        throw new TimeoutException("Свободные обработки не достигли общего старта");
                });
                var request = Request(shared.Source, shared.Output) with
                {
                    FreeText = job.Text,
                    DbPath = shared.UnusedDatabase,
                };
                var summary = PdfWatermarkEngine.Run(request, [shared.Source], progress, ct);
                Assert.Equal((1, 1, 0, 0), (summary.Total, summary.Done, summary.Skipped, summary.Failed));
                Assert.False(summary.Cancelled);
                var item = Assert.Single(summary.Items);
                Assert.Equal(0, item.Number);
                Assert.Equal(SharedFreeSource.PageCount, item.Pages);
                return Path.Combine(shared.Output, item.OutputName!);
            }, verify: paths =>
            {
                // Общий источник и выходная папка намеренны: четыре параллельные надписи
                // обязаны опубликовать четыре результата, даже если базовое имя совпало.
                Assert.Equal(4, paths.Distinct(StringComparer.Ordinal).Count());
                Assert.Equal(4, Directory.GetFiles(shared.Output, "*.pdf").Length);
                Assert.Equal(shared.SourceHash, ConcurrentTasks.Fingerprint(shared.Source));
                Assert.False(File.Exists(shared.UnusedDatabase));
                foreach (var path in paths)
                {
                    Assert.Equal(SharedFreeSource.PageCount, PdfStand.PageCount(path));
                    PdfGate.Run(() =>
                    {
                        using var clean = PdfStand.Render(shared.Source);
                        using var stamped = PdfStand.Render(path);
                        Assert.True(PdfStand.DiffShare(stamped, clean) > 0.25,
                                    "готовый файл не содержит видимой свободной надписи");
                    });
                    Assert.Equal("", PdfStand.TextOf(path));
                }
            }, shared: shared);
    }

    [Fact]
    public void Changing_free_text_changes_the_rendered_watermark()
    {
        using var src = new TempDir("wm-free-src");
        using var dst = new TempDir("wm-free-dst");
        var source = Path.Combine(src.Path, "чертеж.pdf");
        PdfStand.WriteA4(source);
        var first = PdfWatermarkEngine.Run(Request(source, dst.Path) with { FreeText = "ПЕРВАЯ НАДПИСЬ" },
                                           [source], null, CancellationToken.None);
        var second = PdfWatermarkEngine.Run(Request(source, dst.Path) with { FreeText = "ДРУГОЙ ТЕКСТ" },
                                            [source], null, CancellationToken.None);
        using var a = PdfStand.Render(Path.Combine(dst.Path, first.Items[0].OutputName!));
        using var b = PdfStand.Render(Path.Combine(dst.Path, second.Items[0].OutputName!));

        Assert.True(PdfStand.DiffShare(a, b) > 0.1, "результат не зависит от введенного текста");
    }

    [Fact]
    public void Free_text_passes_the_selected_layout_to_every_page()
    {
        using var src = new TempDir("wm-free-src");
        using var dst = new TempDir("wm-free-dst");
        var source = Path.Combine(src.Path, "чертеж.pdf");
        PdfStand.WriteA4(source);
        var first = PdfWatermarkEngine.Run(Request(source, dst.Path) with
                                           { Layout = new StampLayout(StampPlacement.Center) },
                                           [source], null, CancellationToken.None);
        var second = PdfWatermarkEngine.Run(Request(source, dst.Path) with
                                            { Layout = new StampLayout(StampPlacement.Tiled) },
                                            [source], null, CancellationToken.None);
        using var a = PdfStand.Render(Path.Combine(dst.Path, first.Items[0].OutputName!));
        using var b = PdfStand.Render(Path.Combine(dst.Path, second.Items[0].OutputName!));

        Assert.True(PdfStand.DiffShare(a, b) > 0.25, "выбор размещения не изменил готовый PDF");
    }

    private static StampRequest Request(string source, string output) =>
        new(source, false, output, "", default, "", "", null, StampMode.FreeText, Text);

    private sealed class SharedFreeSource : IDisposable
    {
        public const int PageCount = 10;
        private readonly TempDir _dir = new("wm-free-shared");
        public Barrier Start { get; } = new(4);
        public string Source { get; }
        public string Output { get; }
        public string SourceHash { get; }
        public string UnusedDatabase => Path.Combine(Output, "не создавать.sqlite");

        public SharedFreeSource()
        {
            Source = Path.Combine(_dir.Path, "Общий чертеж.pdf");
            Output = Directory.CreateDirectory(Path.Combine(_dir.Path, "результат")).FullName;
            PdfStand.Write(Source, Enumerable.Repeat(PdfStand.A4, PageCount).ToArray());
            SourceHash = ConcurrentTasks.Fingerprint(Source);
        }

        public void Dispose() { Start.Dispose(); _dir.Dispose(); }
    }

    private sealed record FreeTextJob(string Text) : IDisposable
    {
        public void Dispose() { }
    }
}
