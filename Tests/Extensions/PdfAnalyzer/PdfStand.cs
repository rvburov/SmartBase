using UglyToad.PdfPig.Writer;

namespace SmartBase.Tests.Extensions.PdfAnalyzer;

/// <summary>
/// Настоящие PDF для сквозных тестов. PdfPig умеет не только читать, но и писать
/// (<see cref="PdfDocumentBuilder"/>), поэтому стенд собирается ТЕМ ЖЕ пакетом, что и разбор —
/// без бинарных фикстур в репозитории и без новых зависимостей.
///
/// Размеры страниц задаются в миллиметрах и переводятся в пункты той же формулой, которой
/// движок переводит их обратно (мм = пункты × 25.4 / 72). Содержимого у страниц нет — движку
/// от файла нужны только геометрия страниц, размер и имя.
/// </summary>
internal static class PdfStand
{
    // Форматы по ГОСТ 2.301 в миллиметрах (ширина × высота).
    public static readonly (double W, double H) A4   = (210, 297);
    public static readonly (double W, double H) A3   = (297, 420);
    public static readonly (double W, double H) A2   = (420, 594);
    public static readonly (double W, double H) A1   = (594, 841);
    public static readonly (double W, double H) A0   = (841, 1189);
    public static readonly (double W, double H) A4x3 = (297, 630);   // производный формат
    public static readonly (double W, double H) A5   = (148, 210);   // не базовый — уйдёт в «Доп. форматы»
    public static readonly (double W, double H) Odd  = (500, 700);   // нестандартный

    private const double MmToPt = 72.0 / 25.4;

    /// <summary>Записать PDF с заданными страницами.</summary>
    public static void Write(string path, params (double W, double H)[] pages)
    {
        using var builder = new PdfDocumentBuilder();
        foreach (var (w, h) in pages)
            builder.AddPage(w * MmToPt, h * MmToPt);
        File.WriteAllBytes(path, builder.Build());
    }

    /// <summary>Одностраничный A4 — самый частый случай в тестах.</summary>
    public static void WriteA4(string path) => Write(path, A4);

    /// <summary>Файл с расширением .pdf, который PDF не является: движок обязан записать его
    /// в ошибки и продолжить, а не оборвать прогон.</summary>
    public static void WriteBroken(string path) => File.WriteAllText(path, "%PDF-1.4 это не документ");
}
