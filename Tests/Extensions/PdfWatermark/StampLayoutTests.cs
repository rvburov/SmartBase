using SkiaSharp;
using SmartBase.Extensions.PdfWatermark;

namespace SmartBase.Tests.Extensions.PdfWatermark;

/// <summary>Диагональная сетка до краев листа, переносы и наклон настоящих пикселей.</summary>
public sealed class StampLayoutTests
{
    private static readonly IReadOnlyList<string> ApprovedLines = CopyNumber.StampText(
        "Иванов Иван Иванович", 1, new DateTime(2026, 11, 10), "Передача третьим лицам запрещена");

    [Theory]
    [InlineData(210, 297)]
    [InlineData(297, 210)]
    [InlineData(297, 420)]
    [InlineData(594, 841)]
    [InlineData(100, 120)]
    public void Repeating_grid_continues_beyond_all_four_page_edges(double width, double height)
    {
        var grid = new StampLayout(StampPlacement.Tiled).Plan(width * 4, height * 4, 4);
        var radians = grid.Angle * Math.PI / 180;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var corners = grid.Centers.SelectMany(center =>
            from dx in new[] { -grid.Width / 2, grid.Width / 2 }
            from dy in new[] { -grid.Height / 2, grid.Height / 2 }
            select (X: width * 2 + cos * (center.X + dx) - sin * (center.Y + dy),
                    Y: height * 2 + sin * (center.X + dx) + cos * (center.Y + dy))).ToArray();
        Assert.Contains(corners, point => point.X < 0);
        Assert.Contains(corners, point => point.X > width * 4);
        Assert.Contains(corners, point => point.Y < 0);
        Assert.Contains(corners, point => point.Y > height * 4);
        Assert.True(grid.Centers.Count > 1, "крайние части блоков не должны отбрасываться целиком");
    }

    [Fact]
    public void Rows_follow_the_approved_diagonal_and_measured_text_cells_do_not_overlap()
    {
        const double textWidth = 120 * 4;
        const double textHeight = 97 * 4;
        var settings = new StampLayout(StampPlacement.Tiled);
        var grid = settings.Plan(210 * 4, 297 * 4, 4, textWidth, textHeight);
        var neighbor = grid.Centers.Where(point => Math.Abs(point.Y) < 0.01 && point.X > 0)
                                   .OrderBy(point => point.X).First();
        var nextRow = grid.Centers.Where(point => Math.Abs(point.X) < 0.01 && point.Y > 0)
                                  .OrderBy(point => point.Y).First();
        var radians = grid.Angle * Math.PI / 180;
        var dx = Math.Cos(radians) * neighbor.X;
        var dy = Math.Sin(radians) * neighbor.X;
        Assert.True(dx > 0 && dy < 0, "ряд должен идти снизу слева вверх направо");
        Assert.Equal(-35, Math.Atan2(dy, dx) * 180 / Math.PI, 6);
        Assert.Equal(textWidth + 6 * 4, neighbor.X, 4);
        Assert.Equal(textHeight + 7 * 4, nextRow.Y, 4);
        foreach (var first in grid.Centers)
        foreach (var second in grid.Centers.Where(other => other != first))
            Assert.True(Math.Abs(first.X - second.X) >= textWidth - 0.001 ||
                        Math.Abs(first.Y - second.Y) >= textHeight - 0.001);
    }

    [Fact]
    public void Center_is_one_block_and_dpi_does_not_change_millimeters()
    {
        var low = StampLayout.Default.Plan(210 * 3, 297 * 3, 3);
        var high = StampLayout.Default.Plan(210 * 6, 297 * 6, 6);
        Assert.Equal(new StampCenter(0, 0), Assert.Single(low.Centers));
        Assert.Equal(130 * 3, low.Width);
        Assert.Equal(115 * 3, low.Height);
        Assert.Equal(low.Width * 2, high.Width);
        Assert.Equal(low.Height * 2, high.Height);
        Assert.Equal(low.Angle, high.Angle);
    }

    [Fact]
    public void Tiled_text_box_keeps_its_physical_size_even_on_a_small_sheet()
    {
        var a4 = new StampLayout(StampPlacement.Tiled).Plan(210 * 4, 297 * 4, 4);
        var small = new StampLayout(StampPlacement.Tiled).Plan(60 * 4, 80 * 4, 4);
        Assert.Equal(a4.Width, small.Width);
        Assert.Equal(a4.Height, small.Height);
        var center = StampLayout.Default.Plan(60 * 4, 80 * 4, 4);
        Assert.True(center.Width < small.Width && center.Height < small.Height,
                    "одиночный центральный блок должен вписываться в маленький лист");
    }

    [Fact]
    public void Larger_gaps_reduce_the_number_of_repeated_blocks()
    {
        var dense = new StampLayout(StampPlacement.Tiled).Plan(210 * 4, 297 * 4, 4, 200, 160);
        var sparse = new StampLayout(StampPlacement.Tiled, GapXmm: 100, GapYmm: 100)
            .Plan(210 * 4, 297 * 4, 4, 200, 160);
        Assert.True(dense.Centers.Count > sparse.Centers.Count);
    }

    [Fact]
    public void Huge_finite_media_box_is_rejected_before_integer_grid_overflow() =>
        Assert.Throws<PdfWatermarkException>(() =>
            new StampLayout(StampPlacement.Tiled).Plan(5000, 5000, 1e-20));

    [Theory]
    [InlineData(double.NaN, 50, 0, 0, 8)]
    [InlineData(double.PositiveInfinity, 50, 0, 0, 8)]
    [InlineData(0, 50, 0, 0, 8)]
    [InlineData(75, -1, 0, 0, 8)]
    [InlineData(75, 50, -1, 0, 8)]
    [InlineData(75, 50, 0, -1, 8)]
    [InlineData(75, 50, 0, 0, 51)]
    public void Invalid_geometry_is_refused(double w, double h, double x, double y, double margin) =>
        Assert.Throws<PdfWatermarkException>(() => new StampLayout(StampPlacement.Tiled, w, h, x, y, margin).Validate());

    [Fact]
    public void Overflow_is_reported_instead_of_dropping_lines_or_shrinking_to_pixels()
    {
        var error = Assert.Throws<PdfWatermarkException>(() => StampTextLayout.Compose(
            Enumerable.Repeat("Очень длинная надпись для проверки переноса", 30).ToArray(),
            75 * 4, 50 * 4, 4, WatermarkStamp.Default));
        Assert.Contains("Увеличьте ширину или высоту блока", error.Message);
    }

    [Fact]
    public void Long_identifier_is_wrapped_without_losing_characters()
    {
        var text = new string('А', 65);
        var block = StampTextLayout.Compose([text], 75 * 4, 50 * 4, 4, WatermarkStamp.Default);
        Assert.True(block.Lines.Count > 1);
        Assert.Equal(text, string.Concat(block.Lines.Select(line => line.Text)));
        using var ruler = new SKPaint { Typeface = WatermarkFont.Typeface };
        foreach (var line in block.Lines)
        {
            ruler.TextSize = line.Size;
            Assert.True(ruler.MeasureText(line.Text) <= 75 * 4);
        }
    }

    [Fact]
    public void Wide_character_on_a_small_block_tries_a_smaller_readable_font()
    {
        var block = StampTextLayout.Compose(["Ш"], 20, 60, 4, WatermarkStamp.Default);
        Assert.Equal("Ш", Assert.Single(block.Lines).Text);
        Assert.InRange(block.FontPt, 10, 17.5);
        using var ruler = new SKPaint { Typeface = WatermarkFont.Typeface, TextSize = block.Lines[0].Size };
        Assert.True(ruler.MeasureText("Ш") <= 20 - 2 * 4 * 25.4 / 72);
    }

    [Fact]
    public void Approved_issue_keeps_30_point_body_and_40_point_heading_with_all_fields()
    {
        var block = StampTextLayout.Compose(ApprovedLines, 130 * 4, 115 * 4, 4, WatermarkStamp.Default);
        Assert.Equal(30, block.FontPt);
        Assert.Equal("КОПИЯ", block.Lines[0].Text);
        Assert.Equal(40.5 * 4 * 25.4 / 72, block.Lines[0].Size, 3);
        Assert.Equal(block.Lines[1].Size * 1.35, block.Lines[0].Size, 3);
        Assert.Equal(string.Join(" ", ApprovedLines), string.Join(" ", block.Lines.Select(line => line.Text)));
        Assert.Contains("Narrow", WatermarkFont.Typeface.FamilyName);
        Assert.True(WatermarkFont.Typeface.FontWeight >= 600);
    }

    [Fact]
    public void Visible_ink_uses_the_shallower_approved_angle()
    {
        using var bitmap = new SKBitmap(840, 1188);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            WatermarkStamp.Draw(canvas, bitmap.Width, bitmap.Height, ["ДИАГОНАЛЬНАЯ НАДПИСЬ"], WatermarkStamp.Default,
                                pixelsPerMm: 4);
        }
        var points = Ink(bitmap);
        var avgX = points.Average(point => point.X);
        var avgY = points.Average(point => point.Y);
        var xx = points.Sum(point => Math.Pow(point.X - avgX, 2));
        var yy = points.Sum(point => Math.Pow(point.Y - avgY, 2));
        var xy = points.Sum(point => (point.X - avgX) * (point.Y - avgY));
        var angle = Math.Atan2(2 * xy, xx - yy) * 90 / Math.PI;
        Assert.InRange(angle, -37, -33);
        Assert.InRange(avgX, 400, 440);
        Assert.InRange(avgY, 574, 614);
    }

    [Theory]
    [InlineData(210, 297)]
    [InlineData(297, 210)]
    [InlineData(297, 420)]
    [InlineData(594, 841)]
    [InlineData(100, 120)]
    public void Tiled_ink_reaches_each_page_edge_on_small_and_large_sheets(int widthMm, int heightMm)
    {
        const int scale = 2;
        using var bitmap = Render(StampPlacement.Tiled, widthMm * scale, heightMm * scale, scale);
        var ink = Ink(bitmap);
        Assert.Contains(ink, point => point.X < 5 * scale);
        Assert.Contains(ink, point => point.X >= bitmap.Width - 5 * scale);
        Assert.Contains(ink, point => point.Y < 5 * scale);
        Assert.Contains(ink, point => point.Y >= bitmap.Height - 5 * scale);
    }

    [Fact]
    public void Repeated_ink_is_clipped_to_an_explicit_margin()
    {
        using var bitmap = Render(StampPlacement.Tiled, marginMm: 12);
        var ink = Ink(bitmap);
        Assert.NotEmpty(ink);
        Assert.All(ink, point =>
        {
            Assert.InRange(point.X, 12 * 4, 840 - 12 * 4 - 1);
            Assert.InRange(point.Y, 12 * 4, 1188 - 12 * 4 - 1);
        });
        Assert.Contains(ink, point => point.X < 14 * 4);
        Assert.Contains(ink, point => point.Y > 1188 - 14 * 4);
    }

    [Fact]
    public void Tiled_pixels_match_the_approved_sample_geometry_and_color()
    {
        using var actual = Render(StampPlacement.Tiled);
        using var approved = ApprovedReference();
        Assert.Equal(0, PdfStand.DiffPixels(actual, approved, tolerance: 2));
    }

    [Fact]
    public void Stamp_does_not_lighten_the_black_text_and_table_lines_under_it()
    {
        using var bitmap = new SKBitmap(840, 1188);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var ink = new SKPaint { Color = SKColors.Black };
            for (var y = 10; y < bitmap.Height; y += 20)
                canvas.DrawRect(0, y, bitmap.Width, 2, ink);
            WatermarkStamp.Draw(canvas, bitmap.Width, bitmap.Height,
                ["КОПИЯ", "Экземпляр № 1", "Выдан: Иванов Иван Иванович"], WatermarkStamp.Default,
                new StampLayout(StampPlacement.Tiled), 4);
        }
        for (var y = 10; y < bitmap.Height; y += 20)
        for (var x = 0; x < bitmap.Width; x++)
            Assert.Equal(SKColors.Black, bitmap.GetPixel(x, y));
    }

    private static SKBitmap Render(StampPlacement placement, int width = 840, int height = 1188,
                                   double pixelsPerMm = 4, double marginMm = 0)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        WatermarkStamp.Draw(canvas, width, height, ApprovedLines,
                            WatermarkStamp.Default, new StampLayout(placement, MarginMm: marginMm), pixelsPerMm);
        return bitmap;
    }

    // Математика отдельно согласованного образца. Этот путь не вызывает Plan или Draw
    // расширения: возврат к отбору лишь целых блоков либо иной фазе сетки меняет пиксели.
    private static SKBitmap ApprovedReference()
    {
        const float scale = 4;
        var bitmap = new SKBitmap(840, 1188);
        var look = new StampLook(R: 80, G: 120, B: 155, Alpha: 60,
                                 MinFontPt: 30, MaxFontPt: 30, LineSpacing: 1.12);
        var block = StampTextLayout.Compose(ApprovedLines, 130 * scale, 115 * scale, scale, look);
        using var fill = new SKPaint
        {
            Typeface = WatermarkFont.Typeface, IsAntialias = true,
            Color = new SKColor(80, 120, 155, 60), BlendMode = SKBlendMode.Multiply,
        };
        var widest = 0f;
        foreach (var line in block.Lines)
        {
            fill.TextSize = line.Size;
            widest = Math.Max(widest, fill.MeasureText(line.Text));
        }
        var stepX = widest + 6 * scale;
        var stepY = block.Height + 7 * scale;
        const double angle = -35;
        var radians = angle * Math.PI / 180;
        var cols = (int)Math.Ceiling((Math.Abs(Math.Cos(radians)) * bitmap.Width +
                                     Math.Abs(Math.Sin(radians)) * bitmap.Height) / stepX / 2) + 1;
        var rows = (int)Math.Ceiling((Math.Abs(Math.Sin(radians)) * bitmap.Width +
                                     Math.Abs(Math.Cos(radians)) * bitmap.Height) / stepY / 2) + 1;
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        canvas.ClipRect(new SKRect(0, 0, bitmap.Width, bitmap.Height));
        canvas.Translate(bitmap.Width / 2f, bitmap.Height / 2f);
        canvas.RotateDegrees((float)angle);
        for (var row = -rows; row <= rows; row++)
        for (var column = -cols; column <= cols; column++)
        {
            canvas.Save();
            canvas.Translate(column * stepX, (float)(row * stepY));
            foreach (var line in block.Lines)
            {
                fill.TextSize = line.Size;
                canvas.DrawText(line.Text, -fill.MeasureText(line.Text) / 2,
                                line.Baseline - (float)block.Height / 2, fill);
            }
            canvas.Restore();
        }
        return bitmap;
    }

    private static List<(int X, int Y)> Ink(SKBitmap bitmap)
    {
        var points = new List<(int, int)>();
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var color = bitmap.GetPixel(x, y);
            if (color.Red < 245 && color.Blue > color.Red + 2) points.Add((x, y));
        }
        return points;
    }
}
