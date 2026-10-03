using System.Globalization;
using System.IO;
using System.Text;

namespace SmartBase.Tests.Integration;

/// <summary>
/// PDF, написанный руками, объект за объектом: у листа поворот <c>/Rotate</c>, обрезка и поток
/// содержимого как есть.
///
/// Сборщик PdfPig поворот листа ставит, но повернуть само содержимое не умеет — матрицы
/// <c>cm</c> у него нет. А обычный альбомный лист, сохраненный с поворотом, — это ровно поворот
/// листа ПЛЮС содержимое, повернутое навстречу: на экране текст стоит прямо. Без него проверялся
/// бы только лист, где текст лежит боком, то есть не тот, что встречается чаще. Файл выходит
/// крошечным, и смещения таблицы xref считаются здесь же.
///
/// Текст пишется латиницей шрифтом Helvetica из стандартных четырнадцати: кириллице понадобился
/// бы вложенный шрифт, а проверяется здесь место и целость слова, а не алфавит.
/// </summary>
internal static class HandPdf
{
    /// <summary>Обрезка листа в пунктах неповернутого листа.</summary>
    public readonly record struct Crop(double Left, double Bottom, double Right, double Top);

    /// <summary>Лист А4 целиком — так выглядит лист без обрезки.</summary>
    public static readonly Crop Full = new(0, 0, 595, 842);

    /// <summary>Лист: поток содержимого, поворот и обрезка (<c>null</c> — без обрезки).</summary>
    public sealed record Sheet(string Content, int Rotate = 0, Crop? Cropped = null);

    /// <summary>Записать документ из листов А4 и вернуть путь к нему.</summary>
    public static string Write(string dir, string name, params Sheet[] sheets)
    {
        // Номера объектов: 1 — каталог, 2 — дерево листов, у листа i — сам лист (3 + 2i) и его
        // содержимое (4 + 2i), последним — шрифт.
        var font    = 3 + 2 * sheets.Length;
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", sheets.Select((_, i) => $"{3 + 2 * i} 0 R"))}] /Count {sheets.Length} >>",
        };
        for (var i = 0; i < sheets.Length; i++)
        {
            var sheet = sheets[i];
            var crop  = sheet.Cropped is { } c ? Inv($" /CropBox [{c.Left} {c.Bottom} {c.Right} {c.Top}]") : "";
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842]{crop} /Rotate {sheet.Rotate} " +
                        $"/Resources << /Font << /F1 {font} 0 R >> >> /Contents {4 + 2 * i} 0 R >>");
            objects.Add($"<< /Length {sheet.Content.Length} >>\nstream\n{sheet.Content}\nendstream");
        }
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        // Файл целиком ASCII: знак — это байт, и смещение объекта — длина текста перед ним.
        var pdf     = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(pdf.ToString()));
        return path;
    }

    /// <summary>
    /// Текст, который на ПОКАЗАННОМ листе стоит прямо: строка начинается в
    /// <paramref name="x"/> пунктах от левого края и в <paramref name="fromTop"/> от верхнего.
    /// Содержимое повернуто навстречу листу — так выглядит обычный альбомный лист, сохраненный с
    /// поворотом.
    /// </summary>
    public static string Upright(int rotate, Crop crop, double x, double fromTop, string text, double size = 14)
    {
        // Где эта точка показанного листа лежит на неповернутом, и куда смотрят оси строки:
        // ось строки должна выйти на экране вправо, верх букв — вверх.
        var (ux, uy, a, b, c, d) = rotate switch
        {
            90  => (crop.Left  + fromTop, crop.Bottom + x,        0,  1, -1,  0),
            180 => (crop.Right - x,       crop.Bottom + fromTop, -1,  0,  0, -1),
            270 => (crop.Right - fromTop, crop.Top    - x,        0, -1,  1,  0),
            _   => (crop.Left  + x,       crop.Top    - fromTop,  1,  0,  0,  1),
        };
        return Inv($"q {a} {b} {c} {d} {ux} {uy} cm BT /F1 {size} Tf 0 0 Td ({text}) Tj ET Q");
    }

    /// <summary>
    /// Текст, написанный как на неповернутом листе, в точке (<paramref name="x"/>,
    /// <paramref name="y"/>) его собственных координат. На повернутом листе он идет боком или
    /// вверх ногами — так выглядит лист, повернутый без поправки содержимого.
    /// </summary>
    public static string Plain(double x, double y, string text, double size = 14) =>
        Inv($"BT /F1 {size} Tf {x} {y} Td ({text}) Tj ET");

    /// <summary>Текст, идущий снизу вверх, — как надписи штампа на полях чертежа.</summary>
    public static string Rising(double x, double y, string text, double size = 12) =>
        Inv($"q 0 1 -1 0 {x} {y} cm BT /F1 {size} Tf 0 0 Td ({text}) Tj ET Q");

    /// <summary>Текст наискось, под углом <paramref name="degrees"/>, — как водяной знак поперек листа.</summary>
    public static string Slanted(double x, double y, double degrees, string text, double size = 24)
    {
        var (sin, cos) = Math.SinCos(degrees * Math.PI / 180);
        return Inv($"q {cos:0.####} {sin:0.####} {-sin:0.####} {cos:0.####} {x} {y} cm BT /F1 {size} Tf 0 0 Td ({text}) Tj ET Q");
    }

    // Числа в PDF пишутся с точкой, что бы ни стояло в языке системы.
    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
