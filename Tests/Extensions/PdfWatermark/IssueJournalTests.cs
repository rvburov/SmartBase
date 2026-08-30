using SmartBase.Extensions.PdfWatermark;

namespace SmartBase.Tests.Extensions.PdfWatermark;

/// <summary>Запись в журнал выдачи и подбор номера экземпляра — на настоящей базе.</summary>
public sealed class IssueJournalTests
{
    private const string Doc    = "АБВГ.301121.001 СБ";
    private const string Holder = "Иванов Иван Иванович";

    private static readonly DateTime Day = new(2026, 8, 29);

    // --- Запись строки ------------------------------------------------------------------------

    [Fact]
    public void Row_goes_to_the_end_with_all_three_values_in_place()
    {
        using var db = new TempDatabase("wm-journal");
        var shape = Stand(db, out _);

        IssueStand.AddRow(db.Constructor, shape, "Первый", 1);
        var issue = IssueJournal.Reserve(db.Constructor, shape, new IssueInput(Doc, Holder, Day));

        var titles = db.Constructor.GetRecordTitlesOrdered(shape.CollectionId);
        Assert.Equal(["Первый", Doc], titles.Select(t => t.Title));

        var values = db.Constructor.GetFieldValues(issue.RecordId);
        Assert.Equal("1",    values[shape.CopyFieldId]);
        Assert.Equal(Holder, values[shape.HolderFieldId]);
    }

    [Fact]
    public void Date_is_stored_as_a_whole_day_without_time()
    {
        using var db = new TempDatabase("wm-journal");
        var shape = Stand(db, out _);

        var issue = IssueJournal.Reserve(db.Constructor, shape,
                                         new IssueInput(Doc, Holder, new DateTime(2026, 8, 29, 14, 35, 0)));

        // Человек вводит ДАТУ выдачи, а не момент нажатия кнопки: время в журнале не значит
        // ничего и в канон попасть не должно.
        Assert.Equal("2026-08-29", db.Constructor.GetFieldValues(issue.RecordId)[shape.DateFieldId]);
    }

    // --- Подбор номера ------------------------------------------------------------------------

    [Fact]
    public void First_free_number_within_the_document_fills_the_hole()
    {
        using var db = new TempDatabase("wm-journal");
        var shape = Stand(db, out _);
        IssueStand.AddRow(db.Constructor, shape, Doc, 1);
        IssueStand.AddRow(db.Constructor, shape, Doc, 3);

        var issue = IssueJournal.Reserve(db.Constructor, shape, new IssueInput(Doc, Holder, Day));

        Assert.Equal(2, issue.Number);
    }

    [Fact]
    public void Numbering_is_separate_for_each_document()
    {
        // Это и есть смысл решения «номер уникален в пределах документа»: занятый экземпляр
        // сборочного чертежа никак не мешает выдать первый экземпляр спецификации.
        using var db = new TempDatabase("wm-journal");
        var shape = Stand(db, out _);
        IssueStand.AddRow(db.Constructor, shape, "Документ А", 1);
        IssueStand.AddRow(db.Constructor, shape, "Документ А", 2);

        var issue = IssueJournal.Reserve(db.Constructor, shape, new IssueInput("Документ Б", Holder, Day));

        Assert.Equal(1, issue.Number);
    }

    [Fact]
    public void Same_document_to_the_same_person_twice_is_normal()
    {
        // Требование пользователя: человек может получить один и тот же документ несколько раз,
        // и это не дубль — экземпляры различаются номером. Обе строки обязаны жить.
        using var db = new TempDatabase("wm-journal");
        var shape = Stand(db, out _);

        var first  = IssueJournal.Reserve(db.Constructor, shape, new IssueInput(Doc, Holder, Day));
        var second = IssueJournal.Reserve(db.Constructor, shape, new IssueInput(Doc, Holder, Day));

        Assert.Equal(1, first.Number);
        Assert.Equal(2, second.Number);
        Assert.Equal(2, db.Constructor.GetRecordTitlesOrdered(shape.CollectionId).Count);
    }

    [Fact]
    public void Document_names_are_compared_folded()
    {
        using var db = new TempDatabase("wm-journal");
        var shape = Stand(db, out _);
        IssueStand.AddRow(db.Constructor, shape, "Отчет", 1);

        var issue = IssueJournal.Reserve(db.Constructor, shape, new IssueInput("  отчёт ", Holder, Day));

        // Иначе тот же документ, записанный чуть иначе, начал бы нумерацию заново, и на двух
        // бумагах оказался бы «экз. 1».
        Assert.Equal(2, issue.Number);
    }

    [Fact]
    public void Rows_of_other_documents_do_not_occupy_numbers()
    {
        using var db = new TempDatabase("wm-journal");
        var shape = Stand(db, out _);
        IssueStand.AddRow(db.Constructor, shape, Doc,        1);
        IssueStand.AddRow(db.Constructor, shape, "Другой",   2);
        IssueStand.AddRow(db.Constructor, shape, Doc,        null);          // пустая ячейка
        IssueStand.AddRow(db.Constructor, shape, Doc,        null);

        Assert.Equal([1], IssueJournal.TakenFor(db.Constructor, shape, Doc));
    }

    [Fact]
    public void Taken_by_batch_and_one_by_one_agree()
    {
        using var db = new TempDatabase("wm-journal");
        var shape = Stand(db, out _);
        IssueStand.AddRow(db.Constructor, shape, "А", 1);
        IssueStand.AddRow(db.Constructor, shape, "А", 2);
        IssueStand.AddRow(db.Constructor, shape, "Б", 5);

        var batch = IssueJournal.Taken(db.Constructor, shape, ["А", "Б", "В"]);

        Assert.Equal([1, 2], batch[JournalSchema.Fold("А")]);
        Assert.Equal([5],    batch[JournalSchema.Fold("Б")]);
        Assert.Empty(batch[JournalSchema.Fold("В")]);
        Assert.Equal(batch[JournalSchema.Fold("А")], IssueJournal.TakenFor(db.Constructor, shape, "А"));
    }

    // --- Отказы и снятие резерва -----------------------------------------------------------------

    [Fact]
    public void Empty_holder_is_refused()
    {
        using var db = new TempDatabase("wm-journal");
        var shape = Stand(db, out _);

        var ex = Assert.Throws<PdfWatermarkException>(
            () => IssueJournal.Reserve(db.Constructor, shape, new IssueInput(Doc, "  ", Day)));

        Assert.Equal("не заполнено, кому выдан документ", ex.Message);
        Assert.Empty(db.Constructor.GetRecordTitlesOrdered(shape.CollectionId));
    }

    [Fact]
    public void Release_removes_the_reservation()
    {
        using var db = new TempDatabase("wm-journal");
        var shape = Stand(db, out _);
        var issue = IssueJournal.Reserve(db.Constructor, shape, new IssueInput(Doc, Holder, Day));

        Assert.True(IssueJournal.Release(db.Constructor, issue.RecordId));

        Assert.Empty(db.Constructor.GetRecordTitlesOrdered(shape.CollectionId));
        Assert.Empty(IssueJournal.TakenFor(db.Constructor, shape, Doc));
    }

    // --- Ведомость номеров: то, ради чего пачка в 20 000 документов вообще выполнима ----------

    [Fact]
    public void Ledger_hands_out_numbers_without_going_back_to_the_journal()
    {
        using var db = new TempDatabase("wm-journal");
        var shape = Stand(db, out _);
        IssueJournal.Reserve(db.Constructor, shape, new IssueInput(Doc, Holder, Day));

        var ledger = NumberLedger.Load(db.Constructor, shape, [Doc]);

        // Журнал вычищается ПОСЛЕ загрузки ведомости. Если бы подбор номера лез в базу, он
        // увидел бы пустую коллекцию и выдал 1 повторно — а он выдает 2, потому что читает
        // память. Ровно это и снимает квадратичный рост на больших пачках.
        foreach (var (id, _) in db.Constructor.GetRecordTitlesOrdered(shape.CollectionId))
            db.Constructor.DeleteRecord(id);

        var issue = IssueJournal.Reserve(db.Constructor, shape,
                                         new IssueInput(Doc, Holder, Day), ledger);

        Assert.Equal(2, issue.Number);
    }

    [Fact]
    public void Ledger_keeps_counting_within_a_batch()
    {
        using var db = new TempDatabase("wm-journal");
        var shape = Stand(db, out _);
        var ledger = NumberLedger.Load(db.Constructor, shape, [Doc, "спецификация"]);

        var mine   = Enumerable.Range(0, 5)
                               .Select(_ => IssueJournal.Reserve(db.Constructor, shape,
                                                                 new IssueInput(Doc, Holder, Day), ledger).Number)
                               .ToArray();
        var others = IssueJournal.Reserve(db.Constructor, shape,
                                          new IssueInput("спецификация", Holder, Day), ledger).Number;

        // Номера идут подряд внутри своего документа, и счет у каждого документа свой.
        Assert.Equal([1, 2, 3, 4, 5], mine);
        Assert.Equal(1, others);
    }

    [Fact]
    public void Released_number_returns_to_the_ledger()
    {
        using var db = new TempDatabase("wm-journal");
        var shape = Stand(db, out _);
        var ledger = NumberLedger.Load(db.Constructor, shape, [Doc]);

        var first = IssueJournal.Reserve(db.Constructor, shape, new IssueInput(Doc, Holder, Day), ledger);
        IssueJournal.Release(db.Constructor, first.RecordId);
        ledger.Free(Doc, first.Number);

        // Знак не лег, строка снята — номер обязан вернуться в оборот. Иначе после каждого
        // битого файла в нумерации оставалась бы дырка, которую никто не заполнит.
        Assert.Equal(1, IssueJournal.Reserve(db.Constructor, shape,
                                             new IssueInput(Doc, Holder, Day), ledger).Number);
    }

    [Fact]
    public void Document_missing_from_the_batch_starts_from_one()
    {
        using var db = new TempDatabase("wm-journal");
        var shape = Stand(db, out _);
        var ledger = NumberLedger.Load(db.Constructor, shape, [Doc]);

        // Имена в ведомость приходят из тех же файлов, что и пачка, но чужое имя не должно
        // ронять прогон — оно просто начинает свой счет.
        Assert.Empty(ledger.Taken("незнакомый документ"));
        Assert.Equal(1, IssueJournal.Reserve(db.Constructor, shape,
                                             new IssueInput("незнакомый документ", Holder, Day), ledger).Number);
    }

    private static JournalShape Stand(TempDatabase db, out int collectionId)
    {
        collectionId = IssueStand.Create(db.Constructor);
        return IssueStand.Shape(db.Constructor, collectionId);
    }
}
