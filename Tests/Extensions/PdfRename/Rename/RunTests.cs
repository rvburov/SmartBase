using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using SmartBase.Extensions.PdfRename.Recognition;
using SmartBase.Extensions.PdfRename.Rename;

namespace SmartBase.Tests.Extensions.PdfRename.Rename;

/// <summary>
/// Группы ТЗ 11, 12, 14, 15 — прогон с подменным распознаванием (быстро и детерминированно):
/// переименование на месте, занятое имя, совпадение текущего имени с итоговым, одинаковые
/// итоговые имена, рекурсивный поиск и одинаковые имена в разных подпапках, исключение
/// шаблонов, неизменность содержимого, изменение и исчезновение файла во время обработки,
/// остановка, большой пакет. Качество OCR здесь НЕ проверяется — это делают тесты с
/// настоящими движками.
/// </summary>
public sealed class RunTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sb-pdfrename-run-" + Guid.NewGuid().ToString("N")[..8]);
    private string Source => Path.Combine(_root, "Сканы");
    private string Templates => Path.Combine(_root, "Сканы", "Шаблоны");

    public RunTests()
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Templates);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    /// <summary>Файл-«скан»: что «распознает» подменный распознаватель, записано внутри.</summary>
    private string Doc(string relative, string designation, string name)
    {
        var path = Path.Combine(Source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"%PDF-1.4\nok|{designation}|{name}\n{Guid.NewGuid()}");
        return path;
    }

    private string Doubtful(string relative, string reason)
    {
        var path = Path.Combine(Source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"%PDF-1.4\nskip|{reason}");
        return path;
    }

    private sealed class FakeRecognizer(Action<string>? during = null) : IDocumentRecognizer
    {
        public DocumentResult Read(string path, CancellationToken ct)
        {
            var line = File.ReadAllLines(path)[1].Split('|');
            during?.Invoke(path);
            if (line[0] == "throw") throw new InvalidOperationException("сбой декодера");
            return line[0] == "ok"
                ? new DocumentResult(new FieldValues(line[1], line[2]), null, "Тест")
                : DocumentResult.Fail(line[1]);
        }

        public void Dispose() { }
    }

    private sealed class Sink : IRunSink
    {
        public readonly ConcurrentQueue<JournalEntry> Entries = new();
        public readonly ConcurrentQueue<string> Messages = new();
        public int FoundCount;
        public int Threads;
        private readonly ConcurrentDictionary<int, bool> _threads = new();

        public void Message(string text) => Messages.Enqueue(text);
        public void Found(int count) => FoundCount = count;

        public void File(JournalEntry entry)
        {
            _threads[Environment.CurrentManagedThreadId] = true;
            Threads = _threads.Count;
            Entries.Enqueue(entry);
        }

        public JournalEntry Of(string relative) => Entries.Single(e => e.RelativePath == relative);
    }

    private RunSummary Run(Sink sink, bool recursive = true, Func<IDocumentRecognizer>? recognizers = null,
        CancellationToken ct = default, int workers = 2)
    {
        var settings = new RunSettings(Templates, Source, recursive, workers);
        var files = OcrRenameRun.ListSource(settings, Directory.GetFiles(Templates));
        sink.Found(files.Count);
        return OcrRenameRun.ProcessFiles(settings, files, recognizers ?? (() => new FakeRecognizer()), sink, ct);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void Renames_in_place_without_copies_and_keeps_content()
    {
        var path = Doc("a.pdf", "АБВГ.123456.001", "Корпус");
        var hash = Hash(path);
        var sink = new Sink();

        var summary = Run(sink);

        var renamed = Path.Combine(Source, "АБВГ.123456.001 Корпус.pdf");
        Assert.Equal(1, summary.Renamed);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(renamed));
        Assert.Equal(hash, Hash(renamed));   // содержимое PDF не изменилось ни на байт
        Assert.Single(Directory.GetFiles(Source));
        Assert.Equal(FileStatus.Renamed, sink.Of("a.pdf").Status);
    }

    [Fact]
    public void Current_name_equal_to_result_is_not_a_conflict()
    {
        Doc("АБВГ.123456.001 Корпус.pdf", "АБВГ.123456.001", "Корпус");
        var sink = new Sink();

        var summary = Run(sink);

        var entry = sink.Of("АБВГ.123456.001 Корпус.pdf");
        Assert.Equal(FileStatus.AlreadyNamed, entry.Status);
        Assert.Equal("Имя уже соответствует результату", entry.Reason);
        Assert.Equal(1, summary.AlreadyNamed);
        Assert.Equal(0, summary.Skipped);
    }

    [Fact]
    public void Occupied_target_is_skipped_never_overwritten()
    {
        var occupant = Path.Combine(Source, "АБВГ.123456.001 Корпус.pdf");
        File.WriteAllText(occupant, "%PDF-1.4\nзанято другим документом");
        var occupantHash = Hash(occupant);
        var path = Doc("a.pdf", "АБВГ.123456.001", "Корпус");
        var hash = Hash(path);
        var sink = new Sink();

        Run(sink, recognizers: () => new FakeRecognizer(p =>
        {
            // «Занимающий» файл распознается как другой документ — его не трогаем.
        }));

        var entry = sink.Of("a.pdf");
        Assert.Equal(FileStatus.Skipped, entry.Status);
        Assert.Contains("уже занято", entry.Reason);
        Assert.Equal(hash, Hash(path));
        Assert.Equal(occupantHash, Hash(occupant));
        Assert.DoesNotContain(Directory.GetFiles(Source), f => f.Contains("(1)") || f.Contains("_1"));   // никаких суффиксов
    }

    [Fact]
    public void Two_documents_with_one_result_conflict_at_rename_time()
    {
        Doc("a.pdf", "АБВГ.123456.001", "Корпус");
        Doc("b.pdf", "АБВГ.123456.001", "Корпус");
        var sink = new Sink();

        var summary = Run(sink);

        Assert.Equal(1, summary.Renamed);
        Assert.Equal(1, summary.Skipped);
        Assert.Contains(sink.Entries, e => e.Status == FileStatus.Skipped && e.Reason!.Contains("уже занято"));
        Assert.Equal(2, Directory.GetFiles(Source).Length);
    }

    [Fact]
    public void Same_names_in_different_subfolders_stay_in_their_folders()
    {
        Doc(Path.Combine("Проект 1", "скан.pdf"), "АБВГ.123456.001", "Корпус");
        Doc(Path.Combine("Проект 2", "скан.pdf"), "АБВГ.123456.002", "Крышка");
        var sink = new Sink();

        Run(sink);

        Assert.True(File.Exists(Path.Combine(Source, "Проект 1", "АБВГ.123456.001 Корпус.pdf")));
        Assert.True(File.Exists(Path.Combine(Source, "Проект 2", "АБВГ.123456.002 Крышка.pdf")));
        // В журнале — путь от исходной папки: одинаковые имена различимы.
        Assert.Contains(sink.Entries, e => e.RelativePath == Path.Combine("Проект 1", "скан.pdf"));
        Assert.Contains(sink.Entries, e => e.RelativePath == Path.Combine("Проект 2", "скан.pdf"));
    }

    [Fact]
    public void Subfolders_only_with_recursive_search()
    {
        Doc("top.pdf", "АБВГ.123456.001", "Корпус");
        var nested = Doc(Path.Combine("Вложенная", "nested.pdf"), "АБВГ.123456.002", "Крышка");
        var sink = new Sink();

        Run(sink, recursive: false);

        Assert.Equal(1, sink.FoundCount);
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void Templates_inside_source_tree_are_never_processed()
    {
        var template = Path.Combine(Templates, "Спецификация.pdf");
        File.WriteAllText(template, "%PDF-1.4\nok|АБВГ.000000.000|Образец");
        var nestedTemplate = Path.Combine(Templates, "Архив", "Старый.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(nestedTemplate)!);
        File.WriteAllText(nestedTemplate, "%PDF-1.4\nok|АБВГ.000000.001|Образец");
        Doc("a.pdf", "АБВГ.123456.001", "Корпус");
        var sink = new Sink();

        Run(sink);

        Assert.Equal(1, sink.FoundCount);
        Assert.True(File.Exists(template));
        Assert.True(File.Exists(nestedTemplate));
    }

    [Fact]
    public void Doubtful_and_failing_files_are_skipped_unchanged()
    {
        var doubtful = Doubtful("сомнительный.pdf", "обозначение прочитано по-разному");
        var broken = Path.Combine(Source, "сбой.pdf");
        File.WriteAllText(broken, "%PDF-1.4\nthrow|");
        Doc("a.pdf", "АБВГ.123456.001", "Корпус");
        var sink = new Sink();

        var summary = Run(sink);

        Assert.True(File.Exists(doubtful));
        Assert.True(File.Exists(broken));
        Assert.Equal("обозначение прочитано по-разному", sink.Of("сомнительный.pdf").Reason);
        Assert.Equal(FileStatus.Error, sink.Of("сбой.pdf").Status);
        Assert.Equal(1, summary.Renamed);   // ошибка одного файла не мешает остальным
        Assert.Equal(2, summary.Skipped);
    }

    [Fact]
    public void File_changed_during_processing_is_not_renamed()
    {
        var path = Doc("a.pdf", "АБВГ.123456.001", "Корпус");
        var sink = new Sink();

        Run(sink, recognizers: () => new FakeRecognizer(p => File.AppendAllText(p, "\nдописано")));

        Assert.Contains("изменился", sink.Of("a.pdf").Reason);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void File_removed_during_processing_is_reported()
    {
        Doc("a.pdf", "АБВГ.123456.001", "Корпус");
        var sink = new Sink();

        Run(sink, recognizers: () => new FakeRecognizer(File.Delete));

        Assert.Contains("исчез", sink.Of("a.pdf").Reason);
    }

    [Fact]
    public void Stop_keeps_done_renames_and_leaves_the_rest_untouched()
    {
        for (var i = 0; i < 200; i++) Doc($"f{i:D3}.pdf", $"АБВГ.123456.{i:D3}", "Деталь");
        using var cts = new CancellationTokenSource();
        var sink = new Sink();
        var seen = 0;

        var summary = Run(sink, ct: cts.Token, recognizers: () => new FakeRecognizer(_ =>
        {
            if (Interlocked.Increment(ref seen) == 50) cts.Cancel();
            Thread.Sleep(2);
        }));

        Assert.True(summary.Stopped);
        Assert.InRange(summary.Renamed, 1, 199);
        var files = Directory.GetFiles(Source).Select(Path.GetFileName).ToList();
        Assert.Equal(200, files.Count);                                        // ничего не потеряно
        Assert.Equal(summary.Renamed, files.Count(f => f!.Contains("Деталь")));  // ровно сделанные
        Assert.Equal(summary.Renamed, sink.Entries.Count(e => e.Status == FileStatus.Renamed));
        Assert.Equal(200 - summary.Renamed, files.Count(f => f!.StartsWith('f')));
    }

    [Fact]
    public void Large_batch_of_8000_files_is_complete_and_bounded()
    {
        const int count = 8000;
        for (var i = 0; i < count; i++)
        {
            var folder = $"Папка {i % 40:D2}";
            if (i % 10 == 9) Doubtful(Path.Combine(folder, $"doc{i:D5}.pdf"), "подходящий шаблон не найден");
            else Doc(Path.Combine(folder, $"doc{i:D5}.pdf"), $"АБВГ.{i:D6}.001", "Деталь");
        }
        GC.Collect();
        var before = GC.GetTotalMemory(true);
        var sink = new Sink();
        var watch = Stopwatch.StartNew();

        var summary = Run(sink, workers: 4);

        watch.Stop();
        var after = GC.GetTotalMemory(true);
        Assert.Equal(count, sink.FoundCount);
        Assert.Equal(count, summary.Processed);
        Assert.Equal(count, sink.Entries.Count);                    // журнал полный: строка на каждый файл
        Assert.Equal(count / 10 * 9, summary.Renamed);
        Assert.Equal(count / 10, summary.Skipped);
        Assert.Equal(count, sink.Entries.Select(e => e.RelativePath).Distinct().Count());
        Assert.False(summary.Stopped);
        Assert.True(after - before < 64L * 1024 * 1024, $"память выросла на {(after - before) / 1024 / 1024} МБ");
        Assert.True(watch.Elapsed < TimeSpan.FromMinutes(2), $"очередь и переименование: {watch.Elapsed}");
    }
}
