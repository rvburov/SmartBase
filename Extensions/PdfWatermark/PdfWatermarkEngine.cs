using SmartBase.Database;
using SmartBase.Database.Repositories;
using Microsoft.EntityFrameworkCore;

namespace SmartBase.Extensions.PdfWatermark;

/// <summary>Чем кончился один файл.</summary>
public enum StampStatus
{
    /// <summary>Знак наложен, строка в журнале заведена.</summary>
    Done,

    /// <summary>Файл не наш — защищен паролем или поврежден. Штатная ситуация пакетного прогона.</summary>
    Skipped,

    /// <summary>Файл наш, но что-то помешало. Это уже повод разбираться.</summary>
    Failed,
}

/// <summary>Итог по одному файлу — строка отчета в журнале окна.</summary>
public sealed record StampItem(
    string FileName,
    StampStatus Status,
    int Number = 0,
    int Pages = 0,
    string? OutputName = null,
    string? Note = null);

public sealed record StampProgress(int Current, int Total, string FileName, double Fraction);

/// <summary>
/// Все, что нужно прогону. Номера здесь НЕТ: он подбирается сам каждому файлу по своему
/// документу. Ручного ввода номера в окне тоже нет — поправить его человек может прямо в
/// коллекции журнала, а поле в окне только сбивало бы с толку на пачке, где документ у каждого
/// файла свой.
/// </summary>
public sealed record StampRequest(
    string Source,
    bool Recursive,
    string OutputFolder,
    string Holder,
    DateTime Date,
    string Warning,
    string DbPath,
    JournalShape Shape);

public sealed record StampSummary(
    int Total, int Done, int Skipped, int Failed, bool Cancelled,
    IReadOnlyList<StampItem> Items);

/// <summary>
/// Прогон: обход источника и наложение знака на каждый найденный PDF с записью в журнал выдачи.
///
/// Единица отказа — ФАЙЛ, а не прогон: защищенный паролем или битый документ становится строкой
/// отчета, и пачка идет дальше. Обрывают прогон только отмена и отказы уровня прогона (шрифт,
/// недоступный источник, база).
///
/// Работает на СВОЕМ подключении к базе (<see cref="StampRequest.DbPath"/>): общий
/// <c>AppDbContext</c> принадлежит UI-потоку и в фон не отдается никогда.
/// </summary>
public static class PdfWatermarkEngine
{
    public const string Extension = ".pdf";

    /// <summary>
    /// Список PDF: либо один выбранный файл, либо обход папки.
    ///
    /// Скрытые и системные файлы пропускаются, а недоступная подпапка обход не роняет — на
    /// сетевой шаре одна закрытая папка не должна отменять работу по остальным.
    /// </summary>
    /// <exception cref="PdfWatermarkException">Источника не существует.</exception>
    public static List<string> ListPdfFiles(string source, bool recursive, CancellationToken ct)
    {
        if (File.Exists(source))
            return IsPdf(source) ? [source] : [];

        if (!Directory.Exists(source))
            throw new PdfWatermarkException($"Не найден файл или папка «{source}».");

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible    = true,
            AttributesToSkip      = FileAttributes.Hidden | FileAttributes.System,
        };

        var files = new List<string>();
        foreach (var path in Directory.EnumerateFiles(source, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (IsPdf(path)) files.Add(path);
        }

        // Порядок ordinal, а не культурный: прогон должен идти одинаково на всех ОС, а по нему
        // же раздаются номера экземпляров.
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    /// <summary>
    /// Прогон по готовому списку.
    ///
    /// Порядок на один файл: <b>предполет — резерв — знак</b>. Сначала документ открывается
    /// (не открылся — журнал не тронут вовсе), затем заводится строка журнала, и только потом
    /// накладывается знак. Именно так, а не наоборот: исход «на бумаге написано „экз. 3“, а в
    /// журнале его нет» опасен — номер выдадут второму человеку, и два экземпляра будут носить
    /// один номер, чего никто не заметит. Обратный исход «строка есть, файла нет» — сгоревший
    /// номер, и расхождение видно глазами.
    ///
    /// Отмена посреди пачки оставляет сделанное как есть: обработанный файл — законченная
    /// единица, по его экземпляру могли уже отдать бумагу.
    /// </summary>
    public static StampSummary Run(StampRequest request, IReadOnlyList<string> files,
                                   IProgress<StampProgress>? progress, CancellationToken ct)
    {
        // Шрифт распаковывается ЗДЕСЬ, а не на первом файле: если встроенный ресурс пропал,
        // прогон обязан упасть сразу, а не после сотни заведенных строк журнала.
        _ = WatermarkFont.Typeface;

        if (!Directory.Exists(request.OutputFolder))
            throw new PdfWatermarkException($"Не найдена папка для сохранения результата «{request.OutputFolder}».");

        // Свое подключение: фоновая работа с базой идет на свежем контексте, а не на общем.
        using var ctx = new AppDbContext(request.DbPath);
        ctx.ApplyPerformancePragmas();
        // SQLite data_version надежен только на одном физическом подключении. Держим его
        // открытым до конца прогона; между резервами транзакции и блокировки не удерживаются.
        ctx.Database.OpenConnection();
        var repo = new ConstructorRepository(ctx);

        // Базовый снимок — два запроса на пачку. Повторяем его только после чужой записи
        // в SQLite; у PG без такого счетчика резерв заново проверяет текущий документ.
        var ledger = NumberLedger.LoadPinned(repo, request.Shape,
                                       files.Select(CopyNumber.DocumentOf).ToHashSet(StringComparer.Ordinal));

        var items     = new List<StampItem>(files.Count);
        var cancelled = false;

        for (var i = 0; i < files.Count; i++)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }

            var path = files[i];
            var name = Path.GetFileName(path);

            progress?.Report(new StampProgress(i + 1, files.Count, name, (double)(i + 1) / files.Count));

            try
            {
                items.Add(Process(repo, request, path, ledger, ct));

                // Трекер EF чистится ПОСЛЕ КАЖДОГО файла, и это не осторожность, а условие
                // выполнимости больших пачек: SaveChanges в проекте проходит по всем
                // отслеживаемым записям (аудит новых строк в AppDbContext), поэтому с ростом
                // трекера каждая следующая вставка дорожает. На тысяче файлов это уже видно
                // глазом, на двадцати тысячах прогон встал бы на квадрате.
                ctx.ChangeTracker.Clear();
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            catch (PdfWatermarkException ex)
            {
                items.Add(new StampItem(name, StampStatus.Failed, Note: ex.Message));
            }
        }

        return new StampSummary(
            files.Count,
            items.Count(i => i.Status == StampStatus.Done),
            items.Count(i => i.Status == StampStatus.Skipped),
            items.Count(i => i.Status == StampStatus.Failed),
            cancelled,
            items);
    }

    private static StampItem Process(ConstructorRepository repo, StampRequest request,
                                     string path, NumberLedger ledger, CancellationToken ct)
    {
        var name     = Path.GetFileName(path);
        var document = CopyNumber.DocumentOf(path);

        // Предполет. Файл не наш — уходим до того, как тронули журнал.
        PdfSharp.Pdf.PdfDocument doc;
        try
        {
            doc = WatermarkStamp.Open(path);
        }
        catch (PdfWatermarkException ex)
        {
            return new StampItem(name, StampStatus.Skipped, Note: ex.Message);
        }

        using (doc)
        {
            // Резерв. Номер подбирается здесь, прямо перед вставкой.
            var issue = IssueJournal.Reserve(repo, request.Shape,
                                             new IssueInput(document, request.Holder, request.Date),
                                             ledger);

            var output = CopyNumber.UniquePath(request.OutputFolder,
                                               CopyNumber.FileNameFor(document, issue.Number),
                                               forbidden: path);
            try
            {
                var lines = CopyNumber.StampText(request.Holder, issue.Number, request.Date, request.Warning);
                var pages = WatermarkStamp.StampAndSave(path, doc, output, lines, ct);

                return new StampItem(name, StampStatus.Done, issue.Number, pages, Path.GetFileName(output));
            }
            catch (OperationCanceledException)
            {
                // Знак не лег — резерв снимаем и на отмене тоже, иначе номер сгорел бы впустую.
                if (IssueJournal.Release(repo, issue.RecordId)) ledger.Free(document, issue.Number);
                throw;
            }
            catch (Exception ex)
            {
                // Не снялся — говорим об этом громко: номер останется занятым, и строку
                // придется убрать руками.
                var released = IssueJournal.Release(repo, issue.RecordId);
                var note     = ex is PdfWatermarkException ? ex.Message : "не удалось записать результат";

                // Номер возвращается в оборот только если строка ДЕЙСТВИТЕЛЬНО удалена: иначе в
                // базе осталась запись с этим номером, и выдать его второй раз нельзя.
                if (released) ledger.Free(document, issue.Number);

                return new StampItem(name, StampStatus.Failed, issue.Number, Note: released
                    ? note
                    : $"{note}; экземпляр {issue.Number} остался занятым в журнале, строку нужно удалить вручную");
            }
        }
    }

    private static bool IsPdf(string path) =>
        Path.GetExtension(path).Equals(Extension, StringComparison.OrdinalIgnoreCase);
}
