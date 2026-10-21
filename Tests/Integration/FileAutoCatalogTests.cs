using System.Collections;
using SmartBase.Core.Models;
using SmartBase.Database;

namespace SmartBase.Tests.Integration;

/// <summary>Настоящий дисковый каталог: буквальное сопоставление, потоковый ввод, отмена
/// и отказ слишком широкому результату вместо молчаливого усечения.</summary>
public sealed class FileAutoCatalogTests
{
    private static FileAutoRule Rule(bool partial = false, string format = "pdf") =>
        new(FileAutoRule.TitleSource, "/synthetic", format, Recursive: true, PartialMatch: partial);

    private static string PathOf(string name, string folder = "synthetic") => Path.Combine(folder, name);

    [Fact]
    public void Exact_lookup_keeps_first_enumerated_path_and_literal_case_and_format()
    {
        var first = PathOf("Отчет.pdf", "first");
        var second = PathOf("Отчет.pdf", "second");
        var source = new SinglePassPaths([first, second, PathOf("Отчет.xlsx"), PathOf("Отчет.PDF")]);
        using var catalog = FileAutoCatalog.Build(Rule(), source);

        Assert.Equal(1, source.Enumerations);
        Assert.Equal(4, catalog.ScannedCount);
        Assert.Equal(2, catalog.MatchedFileCount);
        Assert.Equal(first, catalog.Match(Rule(), "Отчет"));
        Assert.Equal("", catalog.Match(Rule(), "отчет"));
        Assert.Equal("", catalog.Match(Rule(), ""));
        Assert.Equal("", catalog.Match(Rule(), "Нет"));
        Assert.False(catalog.HasSubstringIndex);
    }

    [Fact]
    public void Partial_lookup_keeps_duplicate_names_in_different_folders_and_removes_duplicate_paths()
    {
        var a = PathOf("Отчет, март.pdf", "z-folder");
        var b = PathOf("Отчет, март.pdf", "a-folder");
        var c = PathOf("Отчет апрель.pdf");
        using var catalog = FileAutoCatalog.Build(Rule(true),
            [a, b, a, c, PathOf("Отчет.docx"), PathOf("Иной.pdf", "Отчет")]);

        Assert.True(catalog.HasSubstringIndex); // сборка программы действительно поддерживает trigram
        Assert.Equal(new[] { a, b, c }.Order(StringComparer.Ordinal), FileValue.ReadPaths(catalog.Match(Rule(true), "Отчет")));
        Assert.Equal("", catalog.Match(Rule(true), "pdf"));
        Assert.Equal("", catalog.Match(Rule(true), " \t "));
        Assert.Equal("", catalog.Match(Rule(true), "отчет"));
    }

    [Theory]
    [InlineData("Отчет")]
    [InlineData("100%_готово")]
    [InlineData("a\" OR \"b")]
    [InlineData("a*b?c")]
    [InlineData("a[b]c")]
    [InlineData("😀🧪🚀")]
    [InlineData("e\u0301x")]
    [InlineData("ab")]
    [InlineData("😀🧪")]
    public void Substring_is_literal_for_fts_punctuation_unicode_and_short_fallback(string source)
    {
        var expected = PathOf($"prefix-{source}-suffix.tar.gz");
        var rule = Rule(true, "tar.gz");
        using var catalog = FileAutoCatalog.Build(rule,
            [expected, PathOf("Иной.tar.gz", source), PathOf($"prefix-{source}-suffix.TAR.GZ")]);

        Assert.True(catalog.HasSubstringIndex);
        Assert.Equal(expected, catalog.Match(rule, source));
        Assert.Equal("", catalog.Match(rule, "tar.gz"));
    }

    [Fact]
    public void Exact_only_catalog_can_answer_partial_query_without_an_fts_index()
    {
        var path = PathOf("pre-Отчет-end.pdf");
        using var catalog = FileAutoCatalog.Build(Rule(), [path]);
        Assert.False(catalog.HasSubstringIndex);
        Assert.Equal(path, catalog.Match(Rule(true), "Отчет"));
    }

    [Fact]
    public void A_surrogate_half_uses_ordinal_fallback_instead_of_an_invalid_fts_expression()
    {
        var path = PathOf("before-😀-after.pdf");
        using var catalog = FileAutoCatalog.Build(Rule(true), [path]);
        Assert.Equal(path, catalog.Match(Rule(true), "\uD83D"));
        Assert.Equal("", catalog.Match(Rule(true), "x\0y"));
    }

    [Fact]
    public void Cancellation_before_build_does_not_even_open_the_input_enumerator()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var source = new SinglePassPaths([PathOf("Отчет.pdf")]);
        Assert.ThrowsAny<OperationCanceledException>(() => FileAutoCatalog.Build(Rule(), source, cancellation.Token));
        Assert.Equal(0, source.Enumerations);
        Assert.Equal(0, source.Read);
    }

    [Fact]
    public void Cancellation_during_a_streaming_build_stops_without_reading_the_rest()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new SinglePassPaths(Enumerable.Range(0, 20_000).Select(i => PathOf($"Документ-{i:D5}.pdf")));
        Assert.ThrowsAny<OperationCanceledException>(() => FileAutoCatalog.Build(Rule(), source, cancellation.Token,
            n => { if (n >= 4096) cancellation.Cancel(); }));
        Assert.Equal(1, source.Enumerations);
        Assert.Equal(4096, source.Read);
    }

    [Fact]
    public void Enumeration_error_is_not_turned_into_a_successful_partial_catalog()
    {
        IEnumerable<string> Source()
        {
            yield return PathOf("Отчет.pdf");
            throw new IOException("Папка стала недоступна во время обхода.");
        }
        Assert.Throws<IOException>(() => FileAutoCatalog.Build(Rule(), Source()));
    }

    [Fact]
    public void A_cancelled_query_does_not_damage_the_catalog_for_the_next_query()
    {
        var path = PathOf("Отчет.pdf");
        using var catalog = FileAutoCatalog.Build(Rule(true), [path]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => catalog.Match(Rule(true), "Отчет", cancellation.Token));
        Assert.Equal(path, catalog.Match(Rule(true), "Отчет"));
    }

    [Fact]
    public void Too_many_matching_files_fail_instead_of_returning_a_truncated_value()
    {
        using var catalog = FileAutoCatalog.Build(Rule(true), Enumerable.Range(0, FileAutoCatalog.MaxMatches + 1)
            .Select(i => PathOf($"match-{i:D5}.pdf")));
        Assert.Throws<FileAutoMatchLimitException>(() => catalog.Match(Rule(true), "match"));
        Assert.Equal(PathOf("match-00001.pdf"), catalog.Match(Rule(true), "match-00001"));
    }

    [Fact]
    public void Combined_path_size_is_bounded_even_before_the_match_count_limit()
    {
        var folder = new string('x', 4096);
        using var catalog = FileAutoCatalog.Build(Rule(true), Enumerable.Range(0, 1024)
            .Select(i => PathOf($"match-{i:D5}.pdf", folder)));
        Assert.Throws<FileAutoMatchLimitException>(() => catalog.Match(Rule(true), "match"));
        Assert.Single(FileValue.ReadPaths(catalog.Match(Rule(true), "match-00001")));
    }

    [Fact]
    public void Json_escaping_cannot_bypass_the_stored_value_size_limit()
    {
        // Настоящие файлы не создаются. Кириллица в длинном пути помещается в сырой предел,
        // но стандартное JSON-экранирование раздувает значение до нескольких таких пределов.
        var folder = string.Join(Path.DirectorySeparatorChar.ToString(),
            Enumerable.Repeat(new string('Ж', 100), 10));
        var paths = Enumerable.Range(0, 1000).Select(i => PathOf($"match-{i:D5}.pdf", folder)).ToArray();
        Assert.True(paths.Length < FileAutoCatalog.MaxMatches);
        Assert.True(paths.Sum(path => (long)path.Length) < FileAutoCatalog.MaxCombinedPathChars);
        Assert.True(FileValue.Serialize(paths).Length > FileAutoCatalog.MaxCombinedPathChars);

        using var catalog = FileAutoCatalog.Build(Rule(true), paths);
        Assert.Throws<FileAutoMatchLimitException>(() => catalog.Match(Rule(true), "match"));
        Assert.Equal(paths[1], catalog.Match(Rule(true), "match-00001"));
    }

    [Fact]
    public void Folder_enumeration_respects_recursive_choice_and_keeps_complete_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"smartbase-catalog-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Вложенная"));
        try
        {
            var a = Path.Combine(root, "Отчет.pdf");
            var b = Path.Combine(root, "Вложенная", "Отчет приложение.pdf");
            File.WriteAllText(a, "x");
            File.WriteAllText(b, "x");
            File.WriteAllText(Path.Combine(root, "Отчет.xlsx"), "x");
            var topRule = Rule(true) with { Folder = root, Recursive = false };
            var recursiveRule = topRule with { Recursive = true };
            using var top = FileAutoCatalog.CreateFromFolder(topRule);
            using var recursive = FileAutoCatalog.CreateFromFolder(recursiveRule);
            Assert.Equal(a, top.Match(topRule, "Отчет"));
            Assert.Equal(new[] { a, b }.Order(StringComparer.Ordinal), FileValue.ReadPaths(recursive.Match(recursiveRule, "Отчет")));
            Assert.Equal(2, top.ScannedCount);
            Assert.Equal(3, recursive.ScannedCount);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_missing_root_fails_instead_of_publishing_an_empty_snapshot()
    {
        var rule = Rule() with { Folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) };
        Assert.Throws<DirectoryNotFoundException>(() => FileAutoCatalog.CreateFromFolder(rule));
    }

    [Fact]
    public void Dispose_closes_the_connection_deletes_only_its_own_file_and_is_idempotent()
    {
        using var first = FileAutoCatalog.Build(Rule(), [PathOf("Отчет.pdf")]);
        using var second = FileAutoCatalog.Build(Rule(), [PathOf("Смета.pdf")]);
        var path = first.StoragePath;
        Assert.True(File.Exists(path));
        Assert.NotEqual(path, second.StoragePath);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        first.Dispose();
        first.Dispose();
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(second.StoragePath));
        Assert.Throws<ObjectDisposedException>(() => first.Match(Rule(), "Отчет"));
        Assert.Equal(PathOf("Смета.pdf"), second.Match(Rule(), "Смета"));
    }

    private sealed class SinglePassPaths(IEnumerable<string> paths) : IEnumerable<string>
    {
        public int Enumerations { get; private set; }
        public int Read { get; private set; }
        public IEnumerator<string> GetEnumerator()
        {
            if (++Enumerations != 1) throw new InvalidOperationException("Повторный обход источника.");
            return Enumerate().GetEnumerator();
        }
        private IEnumerable<string> Enumerate()
        {
            foreach (var path in paths) { Read++; yield return path; }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
