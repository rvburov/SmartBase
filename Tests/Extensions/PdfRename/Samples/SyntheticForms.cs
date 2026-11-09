using OpenCvSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SkiaSharp;

namespace SmartBase.Tests.Extensions.PdfRename.Samples;

/// <summary>Вид синтетического бланка.</summary>
public enum FormKind
{
    /// <summary>Чертеж A4, основная надпись по форме 1 (185×55 мм).</summary>
    Drawing,
    /// <summary>Спецификация A4, таблица и основная надпись по форме 2 (185×40 мм).</summary>
    Specification,
    /// <summary>Чертеж A3 альбомный, основная надпись по форме 1.</summary>
    DrawingA3,
}

/// <summary>Прямоугольник в миллиметрах от левого верхнего угла листа.</summary>
public readonly record struct RectMm(double X, double Y, double Width, double Height)
{
    public RectMm Inflate(double d) => new(X - d, Y - d, Width + 2 * d, Height + 2 * d);
}

/// <summary>Где на бланке поля обозначения и наименования.</summary>
public sealed record FormFields(double PageWidthMm, double PageHeightMm, RectMm Designation, RectMm Name)
{
    /// <summary>Обе графы вместе (для красно-оранжевой рамки): столбец шириной с графу
    /// наименования — на бланке A3 обозначение написано над наименованием в этом столбце.</summary>
    public RectMm Both => new(Designation.X, Designation.Y, Name.Width,
        Name.Y + Name.Height - Designation.Y);
}

/// <summary>Как испортить «скан» входного документа.</summary>
public sealed record ScanOptions
{
    public int Dpi { get; init; } = 200;
    public int QuarterTurns { get; init; }
    public double SkewDegrees { get; init; }
    public double Scale { get; init; } = 1.0;
    public double ShiftXmm { get; init; }
    public double ShiftYmm { get; init; }
    public double Noise { get; init; }
    public int JpegQuality { get; init; } = 85;
    public double Blur { get; init; }
}

/// <summary>
/// Синтетические сканы конструкторских документов для тестов: лист с рамкой, основной
/// надписью (графы и подписи как в ЕСКД), таблицей спецификации или «геометрией» чертежа.
/// Приватных документов в тестах нет — только это.
/// </summary>
public static class SyntheticForms
{
    // Шрифт синтетических сканов — DejaVu Sans на всех ОС (на нем замерены движки): системный
    // на Linux, на Windows CI — файл из SB_TEST_FONT (Scripts/ci/ocr-test-deps.sh).
    private static readonly Lazy<SKTypeface> Font = new(() =>
        Environment.GetEnvironmentVariable("SB_TEST_FONT") is { Length: > 0 } file && File.Exists(file)
            ? SKTypeface.FromFile(file)
            : SKTypeface.FromFamilyName("DejaVu Sans") is { } t && t.FamilyName == "DejaVu Sans"
                ? t
                : SKFontManager.Default.MatchCharacter('Ж') ?? SKTypeface.Default);

    public static FormFields Fields(FormKind kind)
    {
        var (w, h) = PageSizeMm(kind);
        var tbH = kind == FormKind.Specification ? 40.0 : 55.0;
        double x0 = w - 5 - 185, y0 = h - 5 - tbH;
        return new FormFields(w, h, new RectMm(x0 + 65, y0, 120, 15), new RectMm(x0 + 65, y0 + 15, 70, 25));
    }

    public static (double W, double H) PageSizeMm(FormKind kind) => kind == FormKind.DrawingA3 ? (420, 297) : (210, 297);

    /// <summary>Нарисовать лист: бланк, содержимое, обозначение и наименование.</summary>
    public static Mat Render(FormKind kind, string designation, string name, int dpi, int seed = 1,
        string organization = "АО \"Пример\"")
    {
        var (wMm, hMm) = PageSizeMm(kind);
        var px = dpi / 25.4;
        int w = (int)Math.Round(wMm * px), h = (int)Math.Round(hMm * px);
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        c.Clear(SKColors.White);
        var rnd = new Random(seed);

        using var thick = new SKPaint { Color = SKColors.Black, StrokeWidth = (float)(0.6 * px), Style = SKPaintStyle.Stroke, IsAntialias = true };
        using var thin = new SKPaint { Color = SKColors.Black, StrokeWidth = (float)(0.25 * px), Style = SKPaintStyle.Stroke, IsAntialias = true };

        void Line(double x1, double y1, double x2, double y2, SKPaint p) =>
            c.DrawLine((float)(x1 * px), (float)(y1 * px), (float)(x2 * px), (float)(y2 * px), p);
        void Text(string s, double x, double y, double capMm, bool center = false, double? boxW = null)
        {
            using var paint = new SKPaint
            {
                Color = SKColors.Black, IsAntialias = true, Typeface = Font.Value, TextSize = (float)(capMm / 0.72 * px),
            };
            var tx = x * px;
            if (center && boxW is { } bw) tx = (x + bw / 2) * px - paint.MeasureText(s) / 2;
            c.DrawText(s, (float)tx, (float)(y * px), paint);
        }

        // Рамка листа.
        c.DrawRect((float)(20 * px), (float)(5 * px), (float)((wMm - 25) * px), (float)((hMm - 10) * px), thick);

        var f = Fields(kind);
        var tbH = kind == FormKind.Specification ? 40.0 : 55.0;
        double x0 = wMm - 5 - 185, y0 = hMm - 5 - tbH;

        // Основная надпись: контур, левая часть (графы изменений и подписей), правая часть.
        c.DrawRect((float)(x0 * px), (float)(y0 * px), (float)(185 * px), (float)(tbH * px), thick);
        double[] cols = [7, 17, 40, 55];
        foreach (var cx in cols) Line(x0 + cx, y0, x0 + cx, y0 + tbH, thin);
        Line(x0 + 65, y0, x0 + 65, y0 + tbH, thick);
        for (var r = 5.0; r < tbH; r += 5) Line(x0, y0 + r, x0 + 65, y0 + r, thin);
        var labelsRow = tbH - 30;
        string[] heads = ["Изм.", "Лист", "№ докум.", "Подп.", "Дата"];
        double[] starts = [0, 7, 17, 40, 55];
        double[] widths = [7, 10, 23, 15, 10];
        for (var i = 0; i < heads.Length; i++) Text(heads[i], x0 + starts[i], y0 + labelsRow - 1.2, 1.8, true, widths[i]);
        string[] roles = ["Разраб.", "Пров.", "Т.контр.", "", "Н.контр.", "Утв."];
        for (var i = 0; i < roles.Length && labelsRow + 5 * (i + 1) <= tbH; i++)
            Text(roles[i], x0 + 0.8, y0 + labelsRow + 5 * (i + 1) - 1.2, 1.8);
        string[] people = ["Иванов", "Петров", "Сидоров", "", "Козлов", "Орлов"];
        for (var i = 0; i < people.Length && labelsRow + 5 * (i + 1) <= tbH; i++)
            Text(people[i], x0 + 17.8, y0 + labelsRow + 5 * (i + 1) - 1.2, 1.8);

        // Правая часть: обозначение (вверху), наименование, литера/масса/масштаб или лист/листов, организация.
        Line(x0 + 65, y0 + 15, x0 + 185, y0 + 15, thick);
        Line(x0 + 135, y0 + 15, x0 + 135, y0 + tbH, thick);
        if (kind == FormKind.Specification)
        {
            Line(x0 + 135, y0 + 20, x0 + 185, y0 + 20, thin);
            Line(x0 + 135, y0 + 25, x0 + 185, y0 + 25, thick);
            Line(x0 + 150, y0 + 15, x0 + 150, y0 + 25, thin);
            Line(x0 + 165, y0 + 15, x0 + 165, y0 + 25, thin);
            Text("Лит.", x0 + 135, y0 + 18.8, 1.8, true, 15);
            Text("Лист", x0 + 150, y0 + 18.8, 1.8, true, 15);
            Text("Листов", x0 + 165, y0 + 18.8, 1.8, true, 20);
            Text("1", x0 + 150, y0 + 23.8, 2.5, true, 15);
            Text("3", x0 + 165, y0 + 23.8, 2.5, true, 20);
            Text(organization, x0 + 135, y0 + 34, 2.5, true, 50);
        }
        else
        {
            Line(x0 + 135, y0 + 20, x0 + 185, y0 + 20, thin);
            Line(x0 + 135, y0 + 35, x0 + 185, y0 + 35, thick);
            Line(x0 + 135, y0 + 40, x0 + 185, y0 + 40, thick);
            Line(x0 + 65, y0 + 40, x0 + 135, y0 + 40, thick);
            Line(x0 + 150, y0 + 15, x0 + 150, y0 + 35, thin);
            Line(x0 + 167, y0 + 15, x0 + 167, y0 + 35, thin);
            Line(x0 + 155, y0 + 35, x0 + 155, y0 + 40, thin);
            Text("Лит.", x0 + 135, y0 + 18.8, 1.8, true, 15);
            Text("Масса", x0 + 150, y0 + 18.8, 1.8, true, 17);
            Text("Масштаб", x0 + 167, y0 + 18.8, 1.8, true, 18);
            Text("1:1", x0 + 167, y0 + 29, 3.5, true, 18);
            Text("Лист", x0 + 136, y0 + 38.8, 1.8);
            Text("Листов 1", x0 + 157, y0 + 38.8, 1.8);
            Text("Сталь 20 ГОСТ 1050-2013", x0 + 65, y0 + 49, 3.5, true, 70);
            Text(organization, x0 + 135, y0 + 49, 2.5, true, 50);
        }

        // Значения полей.
        // На A3 обозначение стоит над наименованием (в его столбце) — для общей рамки.
        var designationWidth = kind == FormKind.DrawingA3 ? f.Name.Width : f.Designation.Width;
        Text(designation, f.Designation.X, f.Designation.Y + 10, 5, true, designationWidth);
        var nameLines = name.Split('\n');
        var nameTop = f.Name.Y + f.Name.Height / 2 - (nameLines.Length - 1) * 3.5;
        for (var i = 0; i < nameLines.Length; i++)
            Text(nameLines[i], f.Name.X, nameTop + i * 7 + 2.5, 4.2, true, f.Name.Width);

        if (kind == FormKind.Specification) DrawSpecTable(c, px, y0, rnd, Text, Line, thick, thin);
        else DrawGeometry(c, px, wMm, y0, rnd, thick, thin);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var bgra = Cv2.ImDecode(data.ToArray(), ImreadModes.Color);
        return bgra;
    }

    private static void DrawSpecTable(SKCanvas c, double px, double tableBottom, Random rnd,
        Action<string, double, double, double, bool, double?> text, Action<double, double, double, double, SKPaint> line,
        SKPaint thick, SKPaint thin)
    {
        double[] widths = [6, 6, 8, 70, 63, 10, 22];
        string[] heads = ["Формат", "Зона", "Поз.", "Обозначение", "Наименование", "Кол.", "Примечание"];
        double x = 20, top = 5, headH = 15;
        line(20, top + headH, 205, top + headH, thick);
        var cx = x;
        for (var i = 0; i < widths.Length; i++)
        {
            text(heads[i], cx, top + 9, i < 3 ? 1.6 : 2.5, true, widths[i]);
            cx += widths[i];
            if (i < widths.Length - 1) line(cx, top, cx, tableBottom, thick);
        }
        string[] items = ["Сборочный чертеж", "Корпус", "Крышка", "Винт М4х10", "Шайба 4", "Плата", "Кронштейн", "Гайка М4"];
        var row = 0;
        for (var y = top + headH + 8; y <= tableBottom; y += 8, row++)
        {
            line(20, y, 205, y, thin);
            if (row < 9 && row % 2 == 0)
            {
                var item = items[rnd.Next(items.Length)];
                text((row + 1).ToString(), 32, y - 2.5, 2.5, false, null);
                text($"ДЕМО.{rnd.Next(100000, 999999)}.{rnd.Next(100, 999)}", 41, y - 2.5, 2.5, false, null);
                text(item, 111, y - 2.5, 2.5, false, null);
                text(rnd.Next(1, 9).ToString(), 175, y - 2.5, 2.5, false, null);
            }
        }
    }

    private static void DrawGeometry(SKCanvas c, double px, double wMm, double tbTop, Random rnd, SKPaint thick, SKPaint thin)
    {
        // Деталь: несколько контуров, окружностей и размерных линий — у каждого документа свои.
        var area = new SKRect((float)(30 * px), (float)(15 * px), (float)((wMm - 15) * px), (float)((tbTop - 10) * px));
        for (var i = 0; i < 4; i++)
        {
            var cx = area.Left + (float)rnd.NextDouble() * area.Width;
            var cy = area.Top + (float)rnd.NextDouble() * area.Height;
            var rw = (float)((10 + rnd.NextDouble() * 50) * px);
            var rh = (float)((10 + rnd.NextDouble() * 40) * px);
            c.DrawRect(Math.Max(area.Left, cx - rw / 2), Math.Max(area.Top, cy - rh / 2), rw, rh, thick);
            c.DrawCircle(cx, cy, (float)((3 + rnd.NextDouble() * 10) * px), thick);
            c.DrawLine(cx - rw / 2, cy + rh / 2 + (float)(6 * px), cx + rw / 2, cy + rh / 2 + (float)(6 * px), thin);
        }
    }

    /// <summary>Нарисовать цветную рамку (как в редакторе картинок) вокруг области.</summary>
    public static void DrawFrame(Mat page, RectMm rect, Scalar bgr, int dpi, double strokeMm = 0.6)
    {
        var px = dpi / 25.4;
        Cv2.Rectangle(page,
            new OpenCvSharp.Point((int)(rect.X * px), (int)(rect.Y * px)),
            new OpenCvSharp.Point((int)((rect.X + rect.Width) * px), (int)((rect.Y + rect.Height) * px)),
            bgr, Math.Max(1, (int)Math.Round(strokeMm * px)), LineTypes.Link8);
    }

    /// <summary>«Отсканировать» лист: поворот, наклон, масштаб, сдвиг, шум, размытие.</summary>
    public static Mat Scan(Mat page, ScanOptions o, double pageWidthMm)
    {
        var src = page;
        var px = page.Width / pageWidthMm;
        var center = new Point2f(page.Width / 2f, page.Height / 2f);
        using var rot = Cv2.GetRotationMatrix2D(center, o.SkewDegrees, o.Scale);
        rot.Set(0, 2, rot.At<double>(0, 2) + o.ShiftXmm * px);
        rot.Set(1, 2, rot.At<double>(1, 2) + o.ShiftYmm * px);
        var warped = new Mat();
        Cv2.WarpAffine(src, warped, rot, page.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(255));

        for (var i = 0; i < ((o.QuarterTurns % 4) + 4) % 4; i++)
        {
            var turned = new Mat();
            Cv2.Rotate(warped, turned, RotateFlags.Rotate90Clockwise);
            warped.Dispose();
            warped = turned;
        }

        if (o.Blur > 0)
            Cv2.GaussianBlur(warped, warped, new OpenCvSharp.Size(0, 0), o.Blur);
        if (o.Noise > 0)
        {
            using var noise = new Mat(warped.Size(), MatType.CV_8UC3);
            Cv2.Randn(noise, Scalar.All(0), Scalar.All(o.Noise * 255));
            Cv2.Add(warped, noise, warped);
            using var noise2 = new Mat(warped.Size(), MatType.CV_8UC3);
            Cv2.Randn(noise2, Scalar.All(0), Scalar.All(o.Noise * 255));
            Cv2.Subtract(warped, noise2, warped);
        }
        return warped;
    }

    /// <summary>Сохранить картинку как одностраничный PDF-«скан» (JPEG внутри). Размер листа —
    /// по картинке и <paramref name="dpi"/>.</summary>
    public static void SavePdf(string path, IReadOnlyList<Mat> pages, int dpi, int jpegQuality = 85,
        Action<XGraphics, PdfPage, int>? overlay = null, int rotate = 0)
    {
        using var doc = new PdfSharp.Pdf.PdfDocument();
        for (var i = 0; i < pages.Count; i++)
        {
            var mat = pages[i];
            Cv2.ImEncode(".jpg", mat, out var jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, jpegQuality));
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(mat.Width * 72.0 / dpi);
            page.Height = XUnit.FromPoint(mat.Height * 72.0 / dpi);
            using (var gfx = XGraphics.FromPdfPage(page))
            {
                using var ms = new MemoryStream(jpeg);
                using var img = XImage.FromStream(ms);
                gfx.DrawImage(img, 0, 0, page.Width.Point, page.Height.Point);
                overlay?.Invoke(gfx, page, i);
            }
            if (rotate != 0) page.Rotate = rotate;
        }
        doc.Save(path);
    }
}
