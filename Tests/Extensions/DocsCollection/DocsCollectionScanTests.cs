using SmartBase.Extensions.DocsCollection;

namespace SmartBase.Tests.Extensions.DocsCollection;

/// <summary>
/// Обход папки — первый из трех шагов расширения и единственный, которому не нужна БД: разбор
/// строки-фильтра расширений, отбор файлов и порядок, который станет ПОРЯДКОМ ЗАПИСЕЙ будущей
/// коллекции. Ошибка здесь тиха: коллекция создастся, просто не из тех файлов.
/// </summary>
public sealed class DocsCollectionScanTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly IReadOnlySet<string> Any = new HashSet<string>();

    // ── Фильтр расширений ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("pdf, docx",      "docx,pdf")]
    [InlineData("*.pdf; .xlsx",   "pdf,xlsx")]
    [InlineData("PDF",            "pdf")]
    [InlineData("pdf docx",       "docx,pdf")]
    public void Extension_filter_is_parsed_from_a_free_form_string(string raw, string expected)
    {
        var parsed = DocsCollectionBuilder.ParseExtensions(raw).OrderBy(s => s, StringComparer.Ordinal);

        Assert.Equal(expected, string.Join(',', parsed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , ; ")]
    public void Empty_filter_parses_to_an_empty_set(string? raw) =>
        Assert.Empty(DocsCollectionBuilder.ParseExtensions(raw));

    [Fact]
    public void Duplicate_extensions_collapse() =>
        Assert.Single(DocsCollectionBuilder.ParseExtensions("pdf, .pdf, *.PDF"));

    // ── Отбор файлов ──────────────────────────────────────────────────────────

    [Fact]
    public void Empty_filter_takes_every_file()
    {
        using var dir = new TempDir();
        dir.Touch("договор.pdf");
        dir.Touch("смета.xlsx");
        dir.Touch("записка.txt");

        Assert.Equal(3, DocsCollectionBuilder.Scan(dir.Path, recursive: false, Any, None).Count);
    }

    [Fact]
    public void Only_listed_extensions_are_taken()
    {
        using var dir = new TempDir();
        dir.Touch("договор.pdf");
        dir.Touch("записка.txt");

        var docs = DocsCollectionBuilder.Scan(dir.Path, recursive: false,
                                              DocsCollectionBuilder.ParseExtensions("pdf"), None);

        Assert.Equal("договор", Assert.Single(docs).Name);
    }

    [Fact]
    public void Subfolders_are_visited_only_when_recursive()
    {
        using var dir = new TempDir();
        dir.Touch("корневой.pdf");
        dir.Touch(Path.Combine("Договоры", "вложенный.pdf"));

        Assert.Single(DocsCollectionBuilder.Scan(dir.Path, recursive: false, Any, None));
        Assert.Equal(2, DocsCollectionBuilder.Scan(dir.Path, recursive: true, Any, None).Count);
    }

    /// <summary>Скрытые и системные файлы в коллекцию не попадают: в рабочих папках рядом с
    /// документами лежат служебные файлы вроде .DS_Store и Thumbs.db.</summary>
    [Fact]
    public void Hidden_files_are_skipped()
    {
        using var dir = new TempDir();
        dir.Touch("договор.pdf");
        var hidden = dir.Touch(".служебный.pdf");
        if (OperatingSystem.IsWindows()) File.SetAttributes(hidden, FileAttributes.Hidden);

        var docs = DocsCollectionBuilder.Scan(dir.Path, recursive: false, Any, None);

        Assert.Equal("договор", Assert.Single(docs).Name);
    }

    /// <summary>У файла вида «.gitignore» основы имени нет — берется имя целиком, иначе запись
    /// осталась бы без названия.
    ///
    /// Тест платформозависим, и это разное поведение ПЛАТФОРМ, а не расхождение логики: на Unix
    /// ведущая точка означает скрытый файл, и обход отсеивает его раньше (AttributesToSkip),
    /// на Windows атрибута нет и файл берется. Ветки прописаны явно, чтобы прогон был зеленым
    /// на всей матрице ОС.</summary>
    [Fact]
    public void File_without_a_stem_keeps_its_full_name()
    {
        using var dir = new TempDir();
        dir.Touch(".gitignore");

        var docs = DocsCollectionBuilder.Scan(dir.Path, recursive: false, Any, None);

        if (OperatingSystem.IsWindows())
        {
            var doc = Assert.Single(docs);
            Assert.Equal(".gitignore", doc.Name);
            Assert.Equal("gitignore", doc.Format);
        }
        else
        {
            Assert.Empty(docs);
        }
    }

    // ── Порядок и поля документа ──────────────────────────────────────────────

    /// <summary>Порядок обхода становится порядком записей в коллекции, поэтому он обязан быть
    /// осмысленным и устойчивым: сперва по папке, потом по наименованию.</summary>
    [Fact]
    public void Documents_are_sorted_by_folder_then_by_name()
    {
        using var dir = new TempDir();
        dir.Touch(Path.Combine("Договоры", "яблоко.pdf"));
        dir.Touch(Path.Combine("Договоры", "арбуз.pdf"));
        dir.Touch(Path.Combine("Акты", "зебра.pdf"));

        var docs = DocsCollectionBuilder.Scan(dir.Path, recursive: true, Any, None);

        Assert.Equal(["зебра", "арбуз", "яблоко"], docs.Select(d => d.Name));
        Assert.Equal(["Акты", "Договоры", "Договоры"], docs.Select(d => d.FolderName));
    }

    // В колонку «Название папки» идет непосредственный родитель, а не весь путь — иначе
    // колонка была бы нечитаемой на сетевых путях.
    [Fact]
    public void Folder_name_is_the_immediate_parent()
    {
        using var dir = new TempDir();
        dir.Touch(Path.Combine("Договоры", "2026", "акт.pdf"));

        var doc = Assert.Single(DocsCollectionBuilder.Scan(dir.Path, recursive: true, Any, None));

        Assert.Equal("2026", doc.FolderName);
        Assert.Equal("акт", doc.Name);
        Assert.Equal("pdf", doc.Format);
        Assert.True(Path.IsPathRooted(doc.FullPath));
    }

    // ── Ошибки и отмена ───────────────────────────────────────────────────────

    [Fact]
    public void Empty_folder_path_is_a_friendly_error()
    {
        var ex = Assert.Throws<DocsCollectionException>(
            () => DocsCollectionBuilder.Scan("  ", recursive: false, Any, None));

        Assert.Equal("Папка не указана.", ex.Message);
    }

    [Fact]
    public void Missing_folder_is_a_friendly_error()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"нет_такой_{Guid.NewGuid():N}");

        var ex = Assert.Throws<DocsCollectionException>(
            () => DocsCollectionBuilder.Scan(missing, recursive: false, Any, None));

        Assert.Equal("Папка не существует или недоступна.", ex.Message);
    }

    [Fact]
    public void Cancellation_stops_the_scan()
    {
        using var dir = new TempDir();
        for (var i = 0; i < 5; i++) dir.Touch($"документ{i}.pdf");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => DocsCollectionBuilder.Scan(dir.Path, recursive: false, Any, cts.Token));
    }

    // ── Имя коллекции по умолчанию ────────────────────────────────────────────

    [Fact]
    public void Default_collection_name_is_the_folder_name()
    {
        var root = Path.Combine(Path.GetTempPath(), "Договоры");

        Assert.Equal("Договоры", DocsCollectionBuilder.DefaultCollectionName(root));
        Assert.Equal("Договоры", DocsCollectionBuilder.DefaultCollectionName(root + Path.DirectorySeparatorChar));
    }

    // У корня диска имени папки нет — подставляем осмысленное, иначе коллекция будет безымянной.
    [Theory]
    [InlineData("")]
    [InlineData("/")]
    public void Root_folder_falls_back_to_a_default_name(string root) =>
        Assert.Equal("Документы", DocsCollectionBuilder.DefaultCollectionName(root));
}
