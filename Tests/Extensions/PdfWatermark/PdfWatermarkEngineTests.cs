using SmartBase.Extensions.PdfWatermark;

namespace SmartBase.Tests.Extensions.PdfWatermark;

/// <summary>Прогон целиком: обход источника, знак на диске и строки в журнале.</summary>
public sealed class PdfWatermarkEngineTests
{
    private const string Holder  = "Иванов Иван Иванович";
    private const string Warning = "Передача третьим лицам запрещена";

    private static readonly DateTime Day = new(2026, 8, 29);

    // --- Обход источника ------------------------------------------------------------------------

    [Fact]
    public void Single_file_gives_exactly_itself()
    {
        using var dir = new TempDir("wm-engine");
        var pdf = Path.Combine(dir.Path, "чертеж.pdf");
        PdfStand.WriteA4(pdf);

        Assert.Equal([pdf], PdfWatermarkEngine.ListPdfFiles(pdf, recursive: false, CancellationToken.None));
    }

    [Fact]
    public void Single_file_that_is_not_pdf_gives_nothing()
    {
        using var dir = new TempDir("wm-engine");
        var txt = dir.Touch("заметка.txt");

        Assert.Empty(PdfWatermarkEngine.ListPdfFiles(txt, recursive: false, CancellationToken.None));
    }

    [Fact]
    public void Folder_walk_takes_only_pdf_and_honors_recursion()
    {
        using var dir = new TempDir("wm-engine");
        PdfStand.WriteA4(Path.Combine(dir.Path, "верхний.pdf"));
        dir.Touch("чужой.txt");
        Directory.CreateDirectory(Path.Combine(dir.Path, "вложенная"));
        PdfStand.WriteA4(Path.Combine(dir.Path, "вложенная", "нижний.pdf"));

        var flat = PdfWatermarkEngine.ListPdfFiles(dir.Path, recursive: false, CancellationToken.None);
        var deep = PdfWatermarkEngine.ListPdfFiles(dir.Path, recursive: true,  CancellationToken.None);

        Assert.Equal(["верхний.pdf"], flat.Select(Path.GetFileName));
        Assert.Equal(2, deep.Count);
    }

    [Fact]
    public void Missing_source_is_a_readable_refusal()
    {
        var ex = Assert.Throws<PdfWatermarkException>(
            () => PdfWatermarkEngine.ListPdfFiles("/нет/такой/папки", false, CancellationToken.None));

        Assert.Contains("Не найден файл или папка", ex.Message);
    }

    // --- Прогон ----------------------------------------------------------------------------------

    [Fact]
    public void Each_document_starts_its_own_numbering_at_one()
    {
        using var db  = new TempDatabase("wm-engine");
        using var src = new TempDir("wm-src");
        using var dst = new TempDir("wm-dst");

        foreach (var name in new[] { "Первый", "Второй", "Третий" })
            PdfStand.WriteA4(Path.Combine(src.Path, $"{name}.pdf"));

        var summary = Run(db, src.Path, dst.Path, recursive: false);

        Assert.Equal(3, summary.Done);
        Assert.Equal(0, summary.Failed + summary.Skipped);
        Assert.All(summary.Items, i => Assert.Equal(1, i.Number));
        Assert.Equal(["Второй экз. 1.pdf", "Первый экз. 1.pdf", "Третий экз. 1.pdf"], dst.Names());
    }

    [Fact]
    public void Same_name_in_two_folders_is_one_document_with_two_copies()
    {
        // Документ — это ИМЯ файла, а не путь. Два файла с одним именем в разных подпапках
        // считаются одним документом, и второй обязан получить экземпляр 2.
        using var db  = new TempDatabase("wm-engine");
        using var src = new TempDir("wm-src");
        using var dst = new TempDir("wm-dst");

        Directory.CreateDirectory(Path.Combine(src.Path, "копия"));
        PdfStand.WriteA4(Path.Combine(src.Path, "чертеж.pdf"));
        PdfStand.WriteA4(Path.Combine(src.Path, "копия", "чертеж.pdf"));

        var summary = Run(db, src.Path, dst.Path, recursive: true);

        Assert.Equal(2, summary.Done);
        Assert.Equal([1, 2], summary.Items.Select(i => i.Number).OrderBy(n => n));
        Assert.Equal(["чертеж экз. 1.pdf", "чертеж экз. 2.pdf"], dst.Names());
    }

    [Fact]
    public void Batch_numbers_run_without_gaps_or_repeats()
    {
        // Пачка проверяется на числе, при котором ошибка нумерации уже видна: номера обязаны
        // идти подряд и без повторов, а журнал при этом читается ОДИН раз на прогон (ведомость
        // номеров). Без нее подбор лез бы в базу на каждый файл и рос бы квадратом — на
        // двадцати тысячах документов это разница между четвертью часа и часами.
        using var db  = new TempDatabase("wm-engine");
        using var src = new TempDir("wm-src");
        using var dst = new TempDir("wm-dst");

        Directory.CreateDirectory(Path.Combine(src.Path, "второй ряд"));
        for (var i = 0; i < 30; i++)
        {
            PdfStand.WriteA4(Path.Combine(src.Path, $"чертеж {i}.pdf"));
            PdfStand.WriteA4(Path.Combine(src.Path, "второй ряд", $"чертеж {i}.pdf"));
        }

        var shape = RunWithShape(db, src.Path, dst.Path, recursive: true, out var summary);

        Assert.Equal(60, summary.Done);

        // 30 документов по два экземпляра: у каждого имени свой счет — 1 и 2.
        Assert.Equal([1, 2], summary.Items.Select(i => i.Number).Distinct().Order());
        Assert.Equal(60, db.Constructor.GetRecordTitlesOrdered(shape.CollectionId).Count);
        Assert.Equal(60, dst.Names().Length);
    }

    [Fact]
    public void Journal_gets_a_row_per_file_with_holder_and_date()
    {
        using var db  = new TempDatabase("wm-engine");
        using var src = new TempDir("wm-src");
        using var dst = new TempDir("wm-dst");
        PdfStand.WriteA4(Path.Combine(src.Path, "чертеж.pdf"));

        var shape = RunWithShape(db, src.Path, dst.Path, recursive: false, out _);

        var rows = db.Constructor.GetRecordTitlesOrdered(shape.CollectionId);
        Assert.Equal(["чертеж"], rows.Select(r => r.Title));

        var values = db.Constructor.GetFieldValues(rows[0].Id);
        Assert.Equal("1",          values[shape.CopyFieldId]);
        Assert.Equal(Holder,       values[shape.HolderFieldId]);
        Assert.Equal("2026-08-29", values[shape.DateFieldId]);
    }

    [Fact]
    public void Stamp_really_lands_in_the_output_file()
    {
        using var db  = new TempDatabase("wm-engine");
        using var src = new TempDir("wm-src");
        using var dst = new TempDir("wm-dst");
        PdfStand.Write(Path.Combine(src.Path, "чертеж.pdf"), PdfStand.A4, PdfStand.A3);

        var summary = Run(db, src.Path, dst.Path, recursive: false);

        Assert.Equal(2, summary.Items[0].Pages);
        var output = Path.Combine(dst.Path, "чертеж экз. 1.pdf");
        var source = Path.Combine(src.Path, "чертеж.pdf");

        // Знак впечатан в пиксели, поэтому проверяется он расхождением с исходной страницей.
        // Текста в выданном документе не остается вовсе — ни знака, ни документа.
        using var clean   = PdfStand.Render(source);
        using var stamped = PdfStand.Render(output);

        Assert.True(PdfStand.DiffShare(stamped, clean) > 1, "знака нет на странице результата");
        Assert.Equal("", PdfStand.TextOf(output));
    }

    [Fact]
    public void Existing_output_name_gets_a_counter()
    {
        using var db  = new TempDatabase("wm-engine");
        using var src = new TempDir("wm-src");
        using var dst = new TempDir("wm-dst");
        PdfStand.WriteA4(Path.Combine(src.Path, "чертеж.pdf"));
        dst.Touch("чертеж экз. 1.pdf");           // остался от прежнего прогона

        var summary = Run(db, src.Path, dst.Path, recursive: false);

        Assert.Equal("чертеж экз. 1 (2).pdf", summary.Items[0].OutputName);
        Assert.Equal(1, summary.Done);
    }

    // --- Отказы и отмена ---------------------------------------------------------------------------

    [Fact]
    public void Broken_file_in_the_middle_does_not_stop_the_batch()
    {
        using var db  = new TempDatabase("wm-engine");
        using var src = new TempDir("wm-src");
        using var dst = new TempDir("wm-dst");

        PdfStand.WriteA4(Path.Combine(src.Path, "1 хороший.pdf"));
        PdfStand.WriteBroken(Path.Combine(src.Path, "2 битый.pdf"));
        PdfStand.WriteA4(Path.Combine(src.Path, "3 хороший.pdf"));

        var summary = Run(db, src.Path, dst.Path, recursive: false);

        Assert.Equal(2, summary.Done);
        Assert.Equal(1, summary.Skipped);
        Assert.Equal(["1 хороший экз. 1.pdf", "3 хороший экз. 1.pdf"], dst.Names());
    }

    [Fact]
    public void Protected_file_leaves_no_row_in_the_journal()
    {
        // Предполет ради этого и стоит перед резервом: пачка защищенных документов не должна
        // породить столько же создаваемых-и-удаляемых записей и строк журнала изменений.
        using var db  = new TempDatabase("wm-engine");
        using var src = new TempDir("wm-src");
        using var dst = new TempDir("wm-dst");
        PdfStand.WriteProtected(Path.Combine(src.Path, "секретный.pdf"));

        var shape = RunWithShape(db, src.Path, dst.Path, recursive: false, out var summary);

        Assert.Equal(1, summary.Skipped);
        Assert.Equal("документ защищен паролем", summary.Items[0].Note);
        Assert.Empty(db.Constructor.GetRecordTitlesOrdered(shape.CollectionId));
        Assert.Empty(dst.Names());
    }

    [Fact]
    public void Cancellation_keeps_what_is_already_done_and_stops_there()
    {
        using var db  = new TempDatabase("wm-engine");
        using var src = new TempDir("wm-src");
        using var dst = new TempDir("wm-dst");
        PdfStand.WriteA4(Path.Combine(src.Path, "1 первый.pdf"));
        PdfStand.WriteA4(Path.Combine(src.Path, "2 второй.pdf"));

        var collectionId = IssueStand.Create(db.Constructor);
        var shape        = IssueStand.Shape(db.Constructor, collectionId);
        var files        = PdfWatermarkEngine.ListPdfFiles(src.Path, false, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<StampProgress>(p => { if (p.Current == 2) cts.Cancel(); });

        var summary = PdfWatermarkEngine.Run(Request(src.Path, dst.Path, false, db.Path, shape),
                                             files, progress, cts.Token);

        Assert.True(summary.Cancelled);
        Assert.Equal(1, summary.Done);

        // Обработанный файл — законченная единица: его знак и его строка журнала остаются.
        Assert.Equal(["1 первый экз. 1.pdf"], dst.Names());
        Assert.Equal(["1 первый"], db.Constructor.GetRecordTitlesOrdered(shape.CollectionId).Select(r => r.Title));
    }

    [Fact]
    public void Missing_output_folder_stops_the_run_before_it_starts()
    {
        using var db  = new TempDatabase("wm-engine");
        using var src = new TempDir("wm-src");
        PdfStand.WriteA4(Path.Combine(src.Path, "чертеж.pdf"));

        var collectionId = IssueStand.Create(db.Constructor);
        var shape        = IssueStand.Shape(db.Constructor, collectionId);
        var files        = PdfWatermarkEngine.ListPdfFiles(src.Path, false, CancellationToken.None);

        var ex = Assert.Throws<PdfWatermarkException>(
            () => PdfWatermarkEngine.Run(Request(src.Path, "/нет/такой/папки", false, db.Path, shape),
                                         files, null, CancellationToken.None));

        Assert.Contains("Не найдена папка для сохранения результата", ex.Message);
        Assert.Empty(db.Constructor.GetRecordTitlesOrdered(shape.CollectionId));
    }

    // --- Обвязка -------------------------------------------------------------------------------------

    private static StampRequest Request(string source, string output, bool recursive,
                                        string dbPath, JournalShape shape) =>
        new(source, recursive, output, Holder, Day, Warning, dbPath, shape);

    private static StampSummary Run(TempDatabase db, string source, string output, bool recursive)
    {
        RunWithShape(db, source, output, recursive, out var summary);
        return summary;
    }

    private static JournalShape RunWithShape(TempDatabase db, string source, string output,
                                             bool recursive, out StampSummary summary)
    {
        var collectionId = IssueStand.Create(db.Constructor);
        var shape        = IssueStand.Shape(db.Constructor, collectionId);
        var files        = PdfWatermarkEngine.ListPdfFiles(source, recursive, CancellationToken.None);

        summary = PdfWatermarkEngine.Run(Request(source, output, recursive, db.Path, shape),
                                         files, null, CancellationToken.None);
        return shape;
    }
}
