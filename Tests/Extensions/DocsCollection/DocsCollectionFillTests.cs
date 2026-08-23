using SmartBase.Extensions.DocsCollection;

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
}
