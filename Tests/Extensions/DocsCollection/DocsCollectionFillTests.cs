using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartBase.Database;
using SmartBase.Extensions.DocsCollection;
using SmartBase.Utils;

namespace SmartBase.Tests.Extensions.DocsCollection;

/// <summary>
/// Наполнение коллекции записями — третий шаг расширения. Он идет в фоне на СВОЕМ подключении
/// к тому же файлу базы, а не на общем контексте приложения (общий AppDbContext однопоточный).
/// Отсюда и главная проверка файла: записи, сделанные вторым подключением, обязаны быть видны
/// через основной контекст — ровно поэтому стенду нужна файловая БД, а не in-memory.
/// </summary>
public sealed class DocsCollectionFillTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly IReadOnlySet<string> Any = new HashSet<string>();

    private static List<DocEntry> Scan(TempDir dir) =>
        DocsCollectionBuilder.Scan(dir.Path, recursive: true, Any, None);

    [Fact]
    public void Every_document_becomes_a_record()
    {
        using var db  = new TempDatabase();
        using var dir = new TempDir();
        dir.Touch("первый.pdf");
        dir.Touch("второй.pdf");
        var shell = DocsCollectionBuilder.CreateShell(db.Constructor, "Документы");

        var created = DocsCollectionBuilder.FillRecords(db.Path, shell, Scan(dir), null, None);

        Assert.Equal(2, created);
        Assert.Equal(2, db.Constructor.CountRecords(shell.CollectionId));
    }

    [Fact]
    public void Folder_format_and_path_land_in_their_fields()
    {
        using var db  = new TempDatabase();
        using var dir = new TempDir();
        var full = dir.Touch(Path.Combine("Договоры", "акт.pdf"));
        var shell = DocsCollectionBuilder.CreateShell(db.Constructor, "Документы");

        DocsCollectionBuilder.FillRecords(db.Path, shell, Scan(dir), null, None);

        var record = Assert.Single(db.Constructor.GetRecords(shell.CollectionId));
        var values = db.Constructor.GetFieldValues(record.Id!.Value);

        Assert.Equal("акт", record.Title);
        Assert.Equal("Договоры", values[shell.FolderFieldId]);
        Assert.Equal("pdf",      values[shell.FormatFieldId]);
        Assert.Equal(full,       values[shell.FileFieldId]);
    }

    /// <summary>Пустые «папка» и «формат» не пишутся вовсе: пустое значение поля и отсутствие
    /// значения — разные вещи, и таблица не должна разрастаться пустыми строками.</summary>
    [Fact]
    public void Empty_folder_name_and_format_leave_the_field_empty()
    {
        using var db  = new TempDatabase();
        using var dir = new TempDir();
        dir.Touch("безформата");
        var shell = DocsCollectionBuilder.CreateShell(db.Constructor, "Документы");

        DocsCollectionBuilder.FillRecords(db.Path, shell, Scan(dir), null, None);

        var record = Assert.Single(db.Constructor.GetRecords(shell.CollectionId));
        var values = db.Constructor.GetFieldValues(record.Id!.Value);

        Assert.False(values.ContainsKey(shell.FormatFieldId));   // расширения у файла нет
        Assert.True(values.ContainsKey(shell.FileFieldId));      // путь пишется всегда
    }

    // Шаг 1000 оставляет место, чтобы пользователь мог вставлять свои строки между документами.
    [Fact]
    public void Positions_go_up_in_steps_of_a_thousand()
    {
        using var db  = new TempDatabase();
        using var dir = new TempDir();
        dir.Touch("1первый.pdf");
        dir.Touch("2второй.pdf");
        dir.Touch("3третий.pdf");
        var shell = DocsCollectionBuilder.CreateShell(db.Constructor, "Документы");

        DocsCollectionBuilder.FillRecords(db.Path, shell, Scan(dir), null, None);

        var positions = db.Constructor.GetRecords(shell.CollectionId).Select(r => r.Position).ToList();
        Assert.Equal([1000, 2000, 3000], positions);
    }

    [Fact]
    public void Progress_ends_on_the_last_document()
    {
        using var db  = new TempDatabase();
        using var dir = new TempDir();
        dir.Touch("1первый.pdf");
        dir.Touch("2последний.pdf");
        var shell = DocsCollectionBuilder.CreateShell(db.Constructor, "Документы");
        var reported = new List<DocsProgress>();

        DocsCollectionBuilder.FillRecords(db.Path, shell, Scan(dir),
                                          new SyncProgress<DocsProgress>(reported.Add), None);

        var last = reported[^1];
        Assert.Equal(last.Total, last.Current);
        Assert.Equal("2последний", last.File);
    }

    /// <summary>Прогресс постится в UI-поток, поэтому шлется не на каждую запись: на тысячах
    /// документов поток сообщений тормозит диспетчер сильнее самой вставки.</summary>
    [Fact]
    public void Progress_is_not_reported_for_every_record()
    {
        using var db  = new TempDatabase();
        using var dir = new TempDir();
        for (var i = 0; i < 100; i++) dir.Touch($"документ{i:000}.pdf");
        var shell = DocsCollectionBuilder.CreateShell(db.Constructor, "Документы");
        var reported = new List<DocsProgress>();

        DocsCollectionBuilder.FillRecords(db.Path, shell, Scan(dir),
                                          new SyncProgress<DocsProgress>(reported.Add), None);

        Assert.True(reported.Count < 20, $"отчетов {reported.Count} на 100 документов — шаг прогресса потерян");
        Assert.NotEmpty(reported);
    }

    /// <summary>Отмена бросает и ОСТАВЛЯЕТ каркас: убрать недособранную коллекцию — забота
    /// вызывающего (окно зовет TryDelete). Проверяем именно этот контракт, а не «за собой
    /// убрано»: иначе окно удаляло бы уже удаленное.</summary>
    [Fact]
    public void Cancellation_throws_and_leaves_the_shell_behind()
    {
        using var db  = new TempDatabase();
        using var dir = new TempDir();
        dir.Touch("первый.pdf");
        var shell = DocsCollectionBuilder.CreateShell(db.Constructor, "Документы");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => DocsCollectionBuilder.FillRecords(db.Path, shell, Scan(dir), null, cts.Token));

        Assert.Contains(db.Constructor.GetAllCollections(), c => c.Id == shell.CollectionId);
    }

    /// <summary>Записи пишутся вторым подключением к тому же файлу базы — основной контекст
    /// обязан их видеть. Это и есть причина, по которой стенд держит БД файлом: у in-memory
    /// второго подключения к тем же данным не бывает.</summary>
    [Fact]
    public void Records_written_on_the_own_connection_are_visible_through_the_main_one()
    {
        using var db  = new TempDatabase();
        using var dir = new TempDir();
        dir.Touch("договор.pdf");
        var shell = DocsCollectionBuilder.CreateShell(db.Constructor, "Документы");

        DocsCollectionBuilder.FillRecords(db.Path, shell, Scan(dir), null, None);

        var record = Assert.Single(db.Constructor.GetRecords(shell.CollectionId));
        Assert.Equal("договор", record.Title);
    }

    /// <summary>Защита от возврата к поштучной записи: история и оповещения разделов не
    /// должны расти на каждое поле. Проверяем число событий, а не скорость машины.</summary>
    [Fact]
    public void Fill_writes_one_summary_and_one_change_notification()
    {
        using var db = new TempDatabase();
        var shell = DocsCollectionBuilder.CreateShell(db.Constructor, "Документы");
        var docs = Documents(100);
        var lastLog = db.Context.ChangeLog.Max(l => l.Id);
        var notifications = 0;
        void OnCommitted(string path, IReadOnlyList<ChangeBus.Change> changes)
        {
            if (path == db.Path) notifications++;
        }

        ChangeBus.Committed += OnCommitted;
        try { DocsCollectionBuilder.FillRecords(db.Path, shell, docs, null, None); }
        finally { ChangeBus.Committed -= OnCommitted; }

        var log = Assert.Single(db.Context.ChangeLog.AsNoTracking().Where(l => l.Id > lastLog));
        Assert.Equal("create", log.Action);
        Assert.Equal("record", log.TargetType);
        Assert.Equal(shell.CollectionName, log.CollectionName);
        Assert.Equal(docs.Count, log.Count);
        Assert.Equal(1, notifications);
        var record = db.Context.DbRecords.AsNoTracking().First(r => r.CollectionId == shell.CollectionId);
        Assert.Equal(CurrentUser.DisplayName, record.CreatedBy);
        Assert.Equal(record.CreatedBy, record.UpdatedBy);
        Assert.False(string.IsNullOrEmpty(record.CreatedAt));
        Assert.Equal(record.CreatedAt, record.UpdatedAt);
    }

    /// <summary>Отмена после коммита, в том числе последнего неполного пакета: окно получает
    /// исключение и может убрать ВСЕ уже записанные строки и значения вместе с каркасом.</summary>
    [Theory]
    [InlineData(8000)]
    [InlineData(10001)]
    public void Cancellation_after_a_committed_batch_allows_complete_cleanup(int count)
    {
        using var db = new TempDatabase();
        var shell = DocsCollectionBuilder.CreateShell(db.Constructor, "Документы");
        using var cts = new CancellationTokenSource();
        var reported = new List<DocsProgress>();
        var progress = new SyncProgress<DocsProgress>(p => { reported.Add(p); cts.Cancel(); });

        Assert.Throws<OperationCanceledException>(() =>
            DocsCollectionBuilder.FillRecords(db.Path, shell, Documents(count), progress, cts.Token));

        var first = Assert.Single(reported);
        Assert.Equal(Math.Min(count, 10_000), first.Current);
        Assert.Equal(first.Current, db.Constructor.CountRecords(shell.CollectionId));
        Assert.Equal(count, first.Total);
        DocsCollectionBuilder.TryDelete(db.Constructor, shell.CollectionId);
        AssertClean(db, shell);
    }

    [Fact]
    public void Failure_in_the_next_batch_allows_cleanup_of_the_committed_batch()
    {
        using var db = new TempDatabase();
        var shell = DocsCollectionBuilder.CreateShell(db.Constructor, "Документы");
        var docs = Documents(10_001);
        docs[^1] = docs[^1] with { Name = "Отказ" };
        db.Context.Database.ExecuteSqlRaw("""
            CREATE TRIGGER docs_fail BEFORE INSERT ON db_records
            WHEN NEW.Title = 'Отказ'
            BEGIN SELECT RAISE(ABORT, 'test failure'); END;
            """);

        Assert.Throws<SqliteException>(() => DocsCollectionBuilder.FillRecords(db.Path, shell, docs, null, None));
        Assert.Equal(10_000, db.Constructor.CountRecords(shell.CollectionId));

        DocsCollectionBuilder.TryDelete(db.Constructor, shell.CollectionId);
        AssertClean(db, shell);
    }

    private static List<DocEntry> Documents(int count) => Enumerable.Range(0, count)
        .Select(i => new DocEntry($"Документ{i:00000}", "Папка", "pdf",
                                 Path.GetFullPath($"Папка/Документ{i:00000}.pdf"))).ToList();

    private static void AssertClean(TempDatabase db, DocsShell shell)
    {
        Assert.DoesNotContain(db.Constructor.GetAllCollections(), c => c.Id == shell.CollectionId);
        Assert.False(db.Context.DbRecords.AsNoTracking().Any(r => r.CollectionId == shell.CollectionId));
        Assert.False(db.Context.DbFields.AsNoTracking().Any(f => f.CollectionId == shell.CollectionId));
        Assert.False(db.Context.DbFieldValues.AsNoTracking().Any());
    }
}
