using System.Threading.Channels;
using SmartBase.Extensions.PdfRename.Recognition;
using SmartBase.Extensions.PdfRename.Templates;

namespace SmartBase.Extensions.PdfRename.Rename;

/// <summary>Чем закончилась обработка файла.</summary>
internal enum FileStatus
{
    Renamed,
    /// <summary>Имя уже соответствует результату — переименовывать нечего.</summary>
    AlreadyNamed,
    Skipped,
    /// <summary>Сбой при обработке (не причина в данных, а ошибка) — тоже пропуск.</summary>
    Error,
}

/// <summary>Строка журнала: файл (путь от исходной папки), итог, новое имя, причина, шаблон.</summary>
internal sealed record JournalEntry(string RelativePath, FileStatus Status, string? NewName, string? Reason, string? Template);

/// <summary>Параметры запуска.</summary>
internal sealed record RunSettings(string TemplatesFolder, string SourceFolder, bool Recursive, int Workers = 0)
{
    /// <summary>Рабочих потоков по умолчанию: половина ядер, от 1 до 4 — у каждого свои
    /// движки (PaddleOCR — около 150–250 МБ), память ограничена.</summary>
    public int EffectiveWorkers => Workers > 0 ? Workers : Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
}

/// <summary>Куда прогон сообщает о ходе работы. Вызывается из фоновых потоков.</summary>
internal interface IRunSink
{
    /// <summary>Сообщение о ходе (шаблоны, найдено файлов, итог) — в журнал.</summary>
    void Message(string text);

    /// <summary>Файлов найдено — до начала обработки.</summary>
    void Found(int count);

    /// <summary>Файл обработан.</summary>
    void File(JournalEntry entry);
}

/// <summary>Распознавание одного документа; экземпляр — на рабочий поток.</summary>
internal interface IDocumentRecognizer : IDisposable
{
    DocumentResult Read(string path, CancellationToken ct);
}

/// <summary>Настоящее распознавание: шаблоны, совмещение, Tesseract и PaddleOCR.</summary>
internal sealed class OcrRecognizer(IReadOnlyList<DocTemplate> templates, OcrPair pair) : IDocumentRecognizer
{
    public DocumentResult Read(string path, CancellationToken ct) => DocumentReader.Read(path, templates, pair, ct);
    public void Dispose() => pair.Dispose();
}

/// <summary>Итог прогона.</summary>
internal sealed record RunSummary(int Found, int Processed, int Renamed, int AlreadyNamed, int Skipped, bool Stopped, string? Fatal)
{
    public int NotProcessed => Found - Processed;
}

/// <summary>
/// Прогон «по содержимому»: шаблоны → список файлов → распознавание в несколько потоков →
/// переименование по одному.
///
/// <para>Распознавание идет параллельно (<see cref="RunSettings.EffectiveWorkers"/> потоков, у
/// каждого своя пара движков), очередь между шагами ограничена — в памяти одновременно лишь
/// несколько документов. Переименование — в одном потоке: конфликт имен проверяется прямо перед
/// переименованием, и два документа с одним итоговым именем не могут «проскочить» вместе.</para>
/// <para>Остановка: новые файлы не берутся, документы в работе дочитываются и не
/// переименовываются; уже переименованные остаются. Переименование — одна атомарная операция
/// файловой системы, недоделанных файлов не бывает. Содержимое PDF не меняется никогда.</para>
/// </summary>
internal static class OcrRenameRun
{
    public static RunSummary Execute(RunSettings settings, Func<OcrPair> engines, IRunSink sink, CancellationToken ct)
    {
        if (!Directory.Exists(settings.TemplatesFolder))
            return Fatal(sink, "Папка шаблонов не найдена.");
        if (!Directory.Exists(settings.SourceFolder))
            return Fatal(sink, "Папка с PDF не найдена.");

        // Шаблоны — заново при каждом запуске.
        var loads = TemplateLibrary.LoadFolder(settings.TemplatesFolder, ct);
        var templates = loads.Where(l => l.Template is not null).Select(l => l.Template!).ToList();
        try
        {
            foreach (var load in loads)
                sink.Message(load.Template is { } t
                    ? $"Шаблон «{t.Name}»: {Describe(t)}"
                    : $"Шаблон {load.FileName} не используется: {load.Problem}");
            if (loads.Count == 0) return Fatal(sink, "В папке шаблонов нет PDF-файлов.");
            if (templates.Count == 0) return Fatal(sink, "Нет ни одного пригодного шаблона — исправьте разметку.");

            var files = ListSource(settings, loads.Select(l => l.FilePath));
            sink.Found(files.Count);
            if (files.Count == 0)
            {
                sink.Message("PDF-файлов для обработки не найдено.");
                return new RunSummary(0, 0, 0, 0, 0, false, null);
            }
            return ProcessFiles(settings, files, () => new OcrRecognizer(templates, engines()), sink, ct);
        }
        finally
        {
            foreach (var t in templates) t.Dispose();
        }
    }

    private static string Describe(DocTemplate t)
    {
        var size = $"{t.PageSize.WidthMm:F0}×{t.PageSize.HeightMm:F0} мм";
        var fields = t.HasCombined ? "общая область" : "обозначение и наименование";
        return $"{fields}, лист {size}" + (t.PageIndex > 0 ? $", стр. {t.PageIndex + 1}" : "");
    }

    private static RunSummary Fatal(IRunSink sink, string message)
    {
        sink.Message(message);
        return new RunSummary(0, 0, 0, 0, 0, false, message);
    }

    /// <summary>
    /// Файлы для обработки: *.pdf в папке (и подпапках), кроме папки шаблонов и самих
    /// файлов-шаблонов — даже если шаблоны лежат внутри обрабатываемого дерева. Недоступные
    /// папки пропускаются.
    /// </summary>
    public static List<string> ListSource(RunSettings settings, IEnumerable<string> templateFiles)
    {
        var comparison = FileNames.PathComparison;
        var templatesDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.TemplatesFolder)) + Path.DirectorySeparatorChar;
        var excluded = templateFiles.Select(Path.GetFullPath).ToHashSet(
            comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = settings.Recursive,
            IgnoreInaccessible = true,
            MatchType = MatchType.Simple,
            AttributesToSkip = FileAttributes.System,
        };
        return Directory.EnumerateFiles(settings.SourceFolder, "*", options)
            .Where(f => f.EndsWith(FileNames.Extension, StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFullPath)
            .Where(f => !f.StartsWith(templatesDir, comparison) && !excluded.Contains(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    private sealed record Planned(string Path, string Relative, long Length, DateTime Written,
        string? NewName, string? Problem, FileStatus? Status, string? Template);

    /// <summary>Обработать готовый список файлов: распознавание в
    /// <see cref="RunSettings.EffectiveWorkers"/> потоках (у каждого свой распознаватель),
    /// переименование — по одному в вызывающем потоке.</summary>
    internal static RunSummary ProcessFiles(RunSettings settings, IReadOnlyList<string> files,
        Func<IDocumentRecognizer> recognizers, IRunSink sink, CancellationToken ct)
    {
        // Движки создаются заранее: не поднялся хоть один — честно сообщаем, а не работаем
        // одним движком или меньшим числом потоков.
        var pairs = new List<IDocumentRecognizer>();
        try
        {
            for (var i = 0; i < Math.Min(settings.EffectiveWorkers, files.Count); i++) pairs.Add(recognizers());
        }
        catch (Exception ex)
        {
            foreach (var p in pairs) p.Dispose();
            return Fatal(sink, $"OCR не запускается: {Ocr.OcrComponents.Explain(ex)}");
        }

        var queue = Channel.CreateBounded<string>(new BoundedChannelOptions(settings.EffectiveWorkers * 2)
        {
            SingleWriter = true,
        });
        var planned = Channel.CreateBounded<Planned>(new BoundedChannelOptions(64) { SingleReader = true });
        int processed = 0, renamed = 0, already = 0, skipped = 0;

        var producer = Task.Run(async () =>
        {
            try
            {
                foreach (var f in files)
                {
                    if (ct.IsCancellationRequested) break;
                    await queue.Writer.WriteAsync(f, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            finally { queue.Writer.TryComplete(); }
        });

        var workers = pairs.Select(pair => Task.Run(async () =>
        {
            using (pair)
            {
                try
                {
                    await foreach (var path in queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    {
                        var item = Recognize(path, settings.SourceFolder, pair, ct);
                        if (item is null) break; // остановка посреди файла
                        await planned.Writer.WriteAsync(item, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
            }
        })).ToArray();

        var all = Task.WhenAll(workers).ContinueWith(_ => planned.Writer.TryComplete(), TaskScheduler.Default);

        // Переименование — в этом потоке, по одному.
        try
        {
            foreach (var item in planned.Reader.ReadAllAsync(CancellationToken.None).ToBlockingEnumerable())
            {
                if (ct.IsCancellationRequested) continue; // в работе, но не переименовывается
                var entry = Apply(item);
                processed++;
                switch (entry.Status)
                {
                    case FileStatus.Renamed: renamed++; break;
                    case FileStatus.AlreadyNamed: already++; break;
                    default: skipped++; break;
                }
                sink.File(entry);
            }
        }
        finally
        {
            all.Wait(CancellationToken.None);
            producer.Wait(CancellationToken.None);
        }

        return new RunSummary(files.Count, processed, renamed, already, skipped, ct.IsCancellationRequested, null);
    }

    /// <summary>Распознать файл и решить, как его назвать. null — остановлено посреди файла.</summary>
    private static Planned? Recognize(string path, string root, IDocumentRecognizer recognizer, CancellationToken ct)
    {
        var relative = Path.GetRelativePath(root, path);
        long length = 0;
        var written = DateTime.MinValue;
        try
        {
            var info = new FileInfo(path);
            length = info.Length;
            written = info.LastWriteTimeUtc;

            var result = recognizer.Read(path, ct);
            if (result.Values is null)
                return new Planned(path, relative, length, written, null, result.Problem, FileStatus.Skipped, result.Template);

            var (name, problem) = FileNames.Build(result.Values);
            return name is null
                ? new Planned(path, relative, length, written, null, problem, FileStatus.Skipped, result.Template)
                : new Planned(path, relative, length, written, name, null, null, result.Template);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Planned(path, relative, length, written, null, $"файл недоступен: {ex.Message}", FileStatus.Skipped, null);
        }
        catch (Exception ex)
        {
            return new Planned(path, relative, length, written, null, $"ошибка обработки: {ex.Message}", FileStatus.Error, null);
        }
    }

    /// <summary>Переименовать с проверками прямо перед операцией.</summary>
    private static JournalEntry Apply(Planned p)
    {
        if (p.NewName is null)
            return new JournalEntry(p.Relative, p.Status ?? FileStatus.Skipped, null, p.Problem, p.Template);

        var current = Path.GetFileName(p.Path);
        if (string.Equals(current, p.NewName, StringComparison.Ordinal))
            return new JournalEntry(p.Relative, FileStatus.AlreadyNamed, p.NewName, "Имя уже соответствует результату", p.Template);

        var dir = Path.GetDirectoryName(p.Path)!;
        var target = Path.Combine(dir, p.NewName);
        string? Check()
        {
            var info = new FileInfo(p.Path);
            if (!info.Exists) return "файл исчез до переименования";
            if (info.Length != p.Length || info.LastWriteTimeUtc != p.Written) return "файл изменился во время обработки";
            var sameFile = string.Equals(target, p.Path, FileNames.PathComparison);
            if (!sameFile && (File.Exists(target) || Directory.Exists(target)))
                return $"имя «{p.NewName}» уже занято другим файлом в этой папке";
            if (OperatingSystem.IsWindows() && target.Length >= 260)
                return $"полный путь к файлу с новым именем длиннее 259 символов ({target.Length})";
            return null;
        }

        try
        {
            var problem = Check();
            if (problem is not null) return new JournalEntry(p.Relative, FileStatus.Skipped, null, problem, p.Template);
            File.Move(p.Path, target, overwrite: false);
            return new JournalEntry(p.Relative, FileStatus.Renamed, p.NewName, null, p.Template);
        }
        catch (UnauthorizedAccessException)
        {
            return new JournalEntry(p.Relative, FileStatus.Skipped, null, "нет прав на переименование", p.Template);
        }
        catch (IOException ex)
        {
            var reason = File.Exists(target)
                ? $"имя «{p.NewName}» уже занято другим файлом в этой папке"
                : $"не удалось переименовать (файл занят?): {ex.Message}";
            return new JournalEntry(p.Relative, FileStatus.Skipped, null, reason, p.Template);
        }
    }
}
