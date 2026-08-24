using System.IO;
using ClosedXML.Excel;
using ClosedXML.Parser;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Serilog;

namespace SmartBase.Extensions;

/// <summary>Книгу Excel открыть не удалось; текст объясняет причину и годится для показа
/// пользователю как есть. Расширения ловят это и оборачивают в свое исключение, добавив
/// имя файла и то, чем этот файл был для прогона (шаблон, источник).</summary>
public sealed class ExcelOpenException(string message) : Exception(message);

/// <summary>
/// Открытая книга Excel и, если ради нее пришлось делать временную копию, владение этой
/// копией. Копия появляется только на пути ремонта (см. <see cref="Open"/>), и жить она
/// обязана до конца работы с книгой: ClosedXML при <c>SaveAs</c> копирует ИСХОДНЫЙ файл —
/// удали его раньше, и сохранение упадет <c>FileNotFoundException</c>. Поэтому книга
/// отдается не голым <see cref="XLWorkbook"/>, а этой оберткой.
/// </summary>
public sealed class ExcelBook : IDisposable
{
    private readonly string? _tempDir;

    private ExcelBook(XLWorkbook workbook, string? tempDir)
    {
        Workbook = workbook;
        _tempDir = tempDir;
    }

    /// <summary>Сама книга. Исходный файл на диске не тронут в любом случае.</summary>
    public XLWorkbook Workbook { get; }

    public void Dispose()
    {
        Workbook.Dispose();
        if (_tempDir is null) return;
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* временная папка — best-effort, чистка не должна ронять прогон */ }
    }

    /// <summary>
    /// Открыть книгу для расширения. Обычный путь — <c>new XLWorkbook(path)</c> один в один,
    /// без копий и накладных расходов.
    ///
    /// Отдельно разбирается один класс поломки, из-за которого ClosedXML не открывает книгу
    /// ЦЕЛИКОМ: непарсящееся определенное имя (<c>definedNames</c> в <c>xl/workbook.xml</c>).
    /// Такие имена плодятся сами — например, при копировании листа между книгами остается имя
    /// со ссылкой на внешнюю книгу вида <c>[2]Лист!$A$1</c>, а если в имени листа есть пробелы
    /// и апострофов вокруг нет, ClosedXML.Parser обрывается и загрузка падает. В Excel такая
    /// книга открывается нормально, и пользователю неоткуда узнать, что не так: имена с
    /// <c>Visible = False</c> не видны даже в Диспетчере имен.
    ///
    /// Формулы ячеек и проверку данных ClosedXML при загрузке не разбирает — они такой книгу
    /// не роняют, лечить нужно только имена.
    ///
    /// Лечение идет на временной КОПИИ (оригинал — общий файл на сетевой шаре, его не трогаем):
    /// из копии выбрасываются имена, которые не разбираются, и открывается уже она. Терять там
    /// нечего — без этого книга не открывается вообще. Макросы не страдают: правится только
    /// список имен, остальные части файла (включая <c>vbaProject</c>) остаются на месте.
    /// </summary>
    /// <exception cref="ExcelOpenException">Открыть не удалось — текст готов к показу.</exception>
    public static ExcelBook Open(string path)
    {
        try
        {
            return new ExcelBook(new XLWorkbook(path), null);
        }
        catch (Exception ex)
        {
            var parsing = FindParsing(ex);
            if (parsing is null) throw new ExcelOpenException(Explain(ex));
            return Repair(path, parsing);
        }
    }

    // ── Ремонт ────────────────────────────────────────────────────────────────

    private static ExcelBook Repair(string path, ParsingException parsing)
    {
        var name    = Path.GetFileName(path);
        var tempDir = Path.Combine(Path.GetTempPath(), $"smartbase_xl_{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(tempDir);
            var copy = Path.Combine(tempDir, name);
            File.Copy(path, copy);

            var dropped = DropUnparsableNames(copy);
            if (dropped.Count == 0)
            {
                // Разбор оборвался не на имени — чинить нечем, отдаем исходную причину.
                Cleanup(tempDir);
                throw new ExcelOpenException(Explain(parsing));
            }

            var book = new ExcelBook(new XLWorkbook(copy), tempDir);
            Log.Warning("Книга {File:l} открыта после ремонта: выброшены неразбираемые имена {Names}",
                        name, dropped);
            return book;
        }
        catch (ExcelOpenException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Ремонт не удался (нет места под копию, книга сломана глубже) — пользователю
            // важна первая причина, а не то, обо что споткнулась попытка ее обойти.
            Cleanup(tempDir);
            Log.Warning(ex, "Не удалось починить книгу {File:l}", name);
            throw new ExcelOpenException(Explain(parsing));
        }
    }

    /// <summary>Выбросить из книги определенные имена, чьи ссылки не разбираются. Возвращает
    /// описания выброшенных — для лога, чтобы потерю было видно, а не только угадывалось.</summary>
    private static IReadOnlyList<string> DropUnparsableNames(string path)
    {
        using var doc = SpreadsheetDocument.Open(path, isEditable: true);

        var workbook = doc.WorkbookPart?.Workbook;
        var names    = workbook?.DefinedNames;
        if (workbook is null || names is null) return [];

        var bad = names.Elements<DefinedName>().Where(n => !Parses(n.Text)).ToList();
        if (bad.Count == 0) return [];

        foreach (var n in bad) n.Remove();
        workbook.Save();

        return bad.Select(n => $"«{n.Name?.Value}» → {n.Text}").ToList();
    }

    /// <summary>Разберет ли ссылку тот же парсер, что валит загрузку. <see cref="FormulaConverter"/>
    /// — самый дешевый способ спросить: он проходит формулу целиком и падает ровно там же,
    /// где загрузка книги (проверено на ссылках с <c>[n]</c>, <c>#REF!</c>, диапазонах колонок
    /// и функциях вроде <c>OFFSET</c>).
    ///
    /// Пустую ссылку он встречает не разбором, а <see cref="ArgumentException"/> — выпусти ее
    /// наружу, и одно пустое имя сорвало бы ремонт всей книги. Для нас это тот же ответ «нет».</summary>
    private static bool Parses(string refersTo)
    {
        if (string.IsNullOrWhiteSpace(refersTo)) return false;

        try
        {
            FormulaConverter.ToR1C1(refersTo, 1, 1);
            return true;
        }
        catch (ParsingException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort */ }
    }

    // ── Объяснение причины ────────────────────────────────────────────────────

    /// <summary>Ошибка разбора приходит и вложенной — ищем ее по всей цепочке.</summary>
    private static ParsingException? FindParsing(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is ParsingException parsing) return parsing;
        return null;
    }

    private static string Explain(Exception ex) => ex switch
    {
        ParsingException =>
            "В книге есть определенное имя со ссылкой, которую не понимает Excel-движок программы, " +
            "и из-за него книга не читается целиком. Обычно это остаток связи с другой книгой " +
            "(в ссылке виден префикс вида «[2]»). Ищите в Диспетчере имен (Ctrl+F3) и в «Данные → " +
            "Изменить связи»; часть таких имен скрыта и видна только из макроса. Разбор оборвался здесь:\n" +
            ex.Message,

        UnauthorizedAccessException =>
            "Нет прав доступа к файлу.",

        IOException io when (io.HResult & 0xFFFF) is 0x20 or 0x21 =>
            "Файл занят другой программой — закройте его в Excel и повторите.",

        _ => $"{ex.Message}\nПроверьте, что это корректный файл Excel и он не открыт в другой программе.",
    };
}
