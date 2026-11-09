using OpenCvSharp;
using SmartBase.Extensions.PdfRename.Imaging;
using SmartBase.Tests.Extensions.PdfRename.Samples;

namespace SmartBase.Tests.Extensions.PdfRename.Templates;

/// <summary>
/// Группы ТЗ 1–4: обнаружение синей, зеленой и красно-оранжевой рамок с обычными отклонениями
/// оттенков и после JPEG; роль — по цвету, а не по порядку; отсутствующие, лишние,
/// перекрывающиеся и неоднозначные рамки; нормализованные координаты при разном разрешении.
/// </summary>
public sealed class FrameDetectionTests
{
    // Цвета в RGB, как их выбирает человек в редакторе; в OpenCV — BGR.
    private static Scalar Rgb(int r, int g, int b) => new(b, g, r);

    /// <summary>Белый лист A4 при заданном разрешении с «текстом» (черные штрихи).</summary>
    private static Mat Page(double scale = 1.0)
    {
        var page = new Mat((int)(1400 * scale), (int)(1000 * scale), MatType.CV_8UC3, Scalar.All(255));
        for (var y = 100; y < 1300; y += 60)
            Cv2.PutText(page, "TEXT 123 ABC", new Point(80 * scale, y * scale), HersheyFonts.HersheySimplex, 1.2 * scale, Scalar.All(0), Math.Max(1, (int)(2 * scale)));
        return page;
    }

    private static void Frame(Mat page, Rect r, Scalar color, int thickness = 4) =>
        Cv2.Rectangle(page, r, color, thickness);

    private static Mat Jpeg(Mat page, int quality)
    {
        Cv2.ImEncode(".jpg", page, out var bytes, new ImageEncodingParam(ImwriteFlags.JpegQuality, quality));
        return Cv2.ImDecode(bytes, ImreadModes.Color);
    }

    // Рамки ставятся в пустые места листа (между строками «текста» по высоте 60 точек).
    private static readonly Rect Upper = new(560, 1105, 380, 40);
    private static readonly Rect Lower = new(560, 1225, 380, 40);

    public static TheoryData<int, int, int> Blues => new() { { 0, 0, 255 }, { 30, 60, 230 }, { 0, 0, 139 }, { 100, 149, 237 }, { 65, 105, 225 }, { 0, 120, 215 } };
    public static TheoryData<int, int, int> Greens => new() { { 0, 170, 0 }, { 0, 128, 0 }, { 60, 179, 113 }, { 34, 139, 34 }, { 50, 205, 50 } };
    public static TheoryData<int, int, int> Reds => new() { { 255, 0, 0 }, { 255, 69, 0 }, { 255, 140, 0 }, { 220, 20, 60 }, { 240, 80, 42 }, { 200, 30, 30 } };

    [OcrTheory]
    [MemberData(nameof(Blues))]
    public void Blue_shades_mean_designation(int r, int g, int b)
    {
        using var page = Page();
        Frame(page, Upper, Rgb(r, g, b));
        Frame(page, Lower, Rgb(0, 170, 0));
        using var jpeg = Jpeg(page, 75);

        var layout = ColorFrames.Detect(jpeg);

        Assert.True(layout.IsValid, layout.Problem);
        var designation = Assert.Single(layout.Frames, f => f.Role == FieldRole.Designation);
        Assert.InRange(designation.Outer.Y, Upper.Y - 3, Upper.Y + 3);
    }

    [OcrTheory]
    [MemberData(nameof(Greens))]
    public void Green_shades_mean_name(int r, int g, int b)
    {
        using var page = Page();
        Frame(page, Upper, Rgb(0, 0, 255));
        Frame(page, Lower, Rgb(r, g, b));
        using var jpeg = Jpeg(page, 75);

        var layout = ColorFrames.Detect(jpeg);

        Assert.True(layout.IsValid, layout.Problem);
        Assert.Single(layout.Frames, f => f.Role == FieldRole.Name && Math.Abs(f.Outer.Y - Lower.Y) <= 3);
    }

    [OcrTheory]
    [MemberData(nameof(Reds))]
    public void Red_orange_shades_mean_both_fields(int r, int g, int b)
    {
        using var page = Page();
        Frame(page, new Rect(560, 1105, 380, 160), Rgb(r, g, b));
        using var jpeg = Jpeg(page, 70);

        var layout = ColorFrames.Detect(jpeg);

        Assert.True(layout.IsValid, layout.Problem);
        Assert.Equal(FieldRole.Combined, Assert.Single(layout.Frames).Role);
    }

    [OcrFact]
    public void Role_comes_from_color_not_from_order_or_size()
    {
        // Зеленая (наименование) — выше и меньше синей: роли все равно по цвету.
        using var page = Page();
        Frame(page, new Rect(560, 1105, 200, 40), Rgb(0, 160, 0));
        Frame(page, new Rect(420, 1225, 520, 40), Rgb(0, 0, 255));

        var layout = ColorFrames.Detect(page);

        Assert.True(layout.IsValid, layout.Problem);
        Assert.True(layout.Frames.Single(f => f.Role == FieldRole.Name).Outer.Y < 1150);
        Assert.True(layout.Frames.Single(f => f.Role == FieldRole.Designation).Outer.Y > 1200);
    }

    [OcrFact]
    public void Inner_rect_excludes_the_frame_line()
    {
        using var page = Page();
        Frame(page, Upper, Rgb(0, 0, 255), thickness: 6);
        Frame(page, Lower, Rgb(0, 170, 0), thickness: 6);

        var frame = ColorFrames.Detect(page).Frames.Single(f => f.Role == FieldRole.Designation);
        var inner = frame.Inner.ToPixels(page.Width, page.Height);

        Assert.True(inner.X > Upper.X + 1 && inner.X < Upper.X + 8);
        Assert.True(inner.X + inner.Width < Upper.Right - 1);
    }

    [OcrFact]
    public void No_frames_is_reported()
    {
        using var page = Page();
        var layout = ColorFrames.Detect(page);
        Assert.False(layout.IsValid);
        Assert.Equal(ColorFrames.NoFrames, layout.Problem);
    }

    [OcrFact]
    public void Blue_without_green_is_reported()
    {
        using var page = Page();
        Frame(page, Upper, Rgb(0, 0, 255));
        Assert.Contains("нет зеленой", ColorFrames.Detect(page).Problem);
    }

    [OcrFact]
    public void Green_without_blue_is_reported()
    {
        using var page = Page();
        Frame(page, Lower, Rgb(0, 170, 0));
        Assert.Contains("нет синей", ColorFrames.Detect(page).Problem);
    }

    [OcrFact]
    public void Two_blue_frames_are_ambiguous()
    {
        using var page = Page();
        Frame(page, Upper, Rgb(0, 0, 255));
        Frame(page, new Rect(430, 1105, 110, 40), Rgb(0, 0, 255));
        Frame(page, Lower, Rgb(0, 170, 0));
        Assert.Contains("синих рамок 2", ColorFrames.Detect(page).Problem);
    }

    [OcrFact]
    public void Red_with_blue_is_ambiguous_marking()
    {
        using var page = Page();
        Frame(page, Upper, Rgb(0, 0, 255));
        Frame(page, new Rect(430, 1225, 110, 40), Rgb(255, 40, 0));
        Assert.Contains("непонятно, какую разметку", ColorFrames.Detect(page).Problem);
    }

    [OcrFact]
    public void Overlapping_frames_are_reported()
    {
        using var page = Page();
        Frame(page, new Rect(560, 1105, 380, 100), Rgb(0, 0, 255));
        Frame(page, new Rect(700, 1150, 240, 110), Rgb(0, 170, 0));
        var problem = ColorFrames.Detect(page).Problem;
        Assert.NotNull(problem);
    }

    [OcrFact]
    public void Unknown_color_frame_is_not_guessed()
    {
        using var page = Page();
        Frame(page, Upper, Rgb(0, 0, 255));
        Frame(page, Lower, Rgb(0, 170, 0));
        Frame(page, new Rect(430, 1165, 110, 30), Rgb(160, 32, 240));   // фиолетовая
        Assert.Contains("непонятного цвета", ColorFrames.Detect(page).Problem);
    }

    [OcrFact]
    public void Filled_box_is_rejected_as_covering_text()
    {
        using var page = Page();
        Cv2.Rectangle(page, new Rect(560, 1100, 380, 50), Rgb(0, 0, 255), -1);
        Frame(page, Lower, Rgb(0, 170, 0));
        Assert.Contains("закрашенная", ColorFrames.Detect(page).Problem);
    }

    [OcrFact]
    public void Frame_crossing_text_is_rejected()
    {
        using var page = Page();
        // Левая сторона рамки идет поперек строки «текста» (буквы y 1095..1120, x 80..390).
        Frame(page, new Rect(118, 1080, 410, 70), Rgb(0, 0, 255));
        Frame(page, Lower, Rgb(0, 170, 0));
        Assert.Contains("проходит по тексту", ColorFrames.Detect(page).Problem);
    }

    [OcrFact]
    public void Blue_signature_and_round_stamp_are_ignored()
    {
        using var page = Page();
        Frame(page, Upper, Rgb(0, 0, 255));
        Frame(page, Lower, Rgb(0, 170, 0));
        // Подпись синей ручкой и круглая синяя печать — не рамки.
        Cv2.Polylines(page, [[new Point(100, 1180), new Point(140, 1150), new Point(170, 1190), new Point(220, 1140)]], false, Rgb(20, 40, 200), 3);
        Cv2.Circle(page, new Point(300, 900), 70, Rgb(40, 60, 190), 4);

        var layout = ColorFrames.Detect(page);

        Assert.True(layout.IsValid, layout.Problem);
        Assert.Equal(2, layout.Frames.Count);
    }

    [OcrFact]
    public void Normalized_coordinates_do_not_depend_on_resolution()
    {
        NormRect Detect(double scale)
        {
            using var page = Page(scale);
            Frame(page, new Rect((int)(560 * scale), (int)(1105 * scale), (int)(380 * scale), (int)(40 * scale)), Rgb(0, 0, 255), (int)Math.Max(2, 4 * scale));
            Frame(page, new Rect((int)(560 * scale), (int)(1225 * scale), (int)(380 * scale), (int)(40 * scale)), Rgb(0, 170, 0), (int)Math.Max(2, 4 * scale));
            var layout = ColorFrames.Detect(page);
            Assert.True(layout.IsValid, layout.Problem);
            return layout.Frames.Single(f => f.Role == FieldRole.Designation).Inner;
        }

        var low = Detect(1.0);
        var high = Detect(2.5);

        Assert.Equal(low.X, high.X, 0.006);
        Assert.Equal(low.Y, high.Y, 0.006);
        Assert.Equal(low.Width, high.Width, 0.006);
        Assert.Equal(low.Height, high.Height, 0.006);
    }

    [OcrTheory]
    [InlineData(10, "Combined")]
    [InlineData(30, null)]      // желтый
    [InlineData(60, "Name")]
    [InlineData(90, null)]      // бирюзовый
    [InlineData(110, "Designation")]
    [InlineData(150, null)]     // фиолетовый
    [InlineData(178, "Combined")]
    public void Hue_ranges(int hue, string? expected) =>
        Assert.Equal(expected, ColorFrames.RoleOfHue(hue)?.ToString());
}
