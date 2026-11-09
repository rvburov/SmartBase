using SkiaSharp;
using SmartBase.Extensions.PdfWatermark;

namespace SmartBase.Tests.Extensions.PdfWatermark;

/// <summary>Границы настоящих повернутых блоков, переносы и видимый наклон по пикселям.</summary>
public sealed class StampLayoutTests
{
    [Theory]
    [InlineData(210, 297)]
    [InlineData(297, 210)]
    [InlineData(594, 841)]
    [InlineData(100, 120)]
    public void Whole_diagonal_blocks_stay_inside_page_margins(double width, double height)
    {
        var layout = new StampLayout(StampPlacement.Tiled);
        var grid = layout.Plan(width * 4, height * 4, 4);
        var radians = grid.Angle * Math.PI / 180;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        foreach (var center in grid.Centers)
        foreach (var dx in new[] { -grid.Width / 2, grid.Width / 2 })
        foreach (var dy in new[] { -grid.Height / 2, grid.Height / 2 })
        {
            var x = width * 2 + cos * (center.X + dx) - sin * (center.Y + dy);
            var y = height * 2 + sin * (center.X + dx) + cos * (center.Y + dy);
            Assert.InRange(x, grid.Margin - 0.001, width * 4 - grid.Margin + 0.001);
            Assert.InRange(y, grid.Margin - 0.001, height * 4 - grid.Margin + 0.001);
        }
        if (width >= 210) Assert.True(grid.Centers.Count >= 3, "на странице остался только центральный блок");
    }

    [Fact]
    public void Rows_themselves_follow_the_diagonal_and_cells_do_not_overlap()
    {
        var settings = new StampLayout(StampPlacement.Tiled);
        var grid = settings.Plan(210 * 4, 297 * 4, 4);
        var neighbor = grid.Centers.First(point => Math.Abs(point.Y) < 0.01 && point.X > 0);
        var radians = grid.Angle * Math.PI / 180;
        var dx = Math.Cos(radians) * neighbor.X;
        var dy = Math.Sin(radians) * neighbor.X;
        Assert.True(dx > 0 && dy < 0, "ряд должен идти снизу слева вверх направо");
        Assert.Equal(grid.Angle, Math.Atan2(dy, dx) * 180 / Math.PI, 6);
        foreach (var first in grid.Centers)
        foreach (var second in grid.Centers.Where(other => other != first))
            Assert.True(Math.Abs(first.X - second.X) >= grid.Width - 0.001 ||
                        Math.Abs(first.Y - second.Y) >= grid.Height - 0.001);
    }

    [Fact]
    public void Center_is_one_block_and_dpi_does_not_change_millimeters()
    {
        var low = StampLayout.Default.Plan(210 * 3, 297 * 3, 3);
        var high = StampLayout.Default.Plan(210 * 6, 297 * 6, 6);
        Assert.Equal(new StampCenter(0, 0), Assert.Single(low.Centers));
        Assert.Equal(75 * 3, low.Width);
        Assert.Equal(50 * 3, low.Height);
        Assert.Equal(low.Width * 2, high.Width);
        Assert.Equal(low.Height * 2, high.Height);
        Assert.Equal(low.Angle, high.Angle);
    }

    [Fact]
    public void Larger_gaps_reduce_the_number_of_repeated_blocks()
    {
        var dense = new StampLayout(StampPlacement.Tiled).Plan(210 * 4, 297 * 4, 4);
        var sparse = new StampLayout(StampPlacement.Tiled, GapXmm: 100, GapYmm: 100).Plan(210 * 4, 297 * 4, 4);
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
    public void Copy_heading_is_larger_and_font_is_embedded_narrow_bold()
    {
        var lines = CopyNumber.StampText("Иванов Иван Иванович", 2, new DateTime(2026, 11, 9), "Передача третьим лицам запрещена");
        var block = StampTextLayout.Compose(lines, 75 * 4, 50 * 4, 4, WatermarkStamp.Default);
        Assert.Equal("КОПИЯ", block.Lines[0].Text);
        Assert.Equal(block.Lines[1].Size * 1.35, block.Lines[0].Size, 3);
        Assert.Contains("Narrow", WatermarkFont.Typeface.FamilyName);
        Assert.True(WatermarkFont.Typeface.FontWeight >= 600);
    }

    [Fact]
    public void Visible_ink_runs_upwards_along_the_diagonal()
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
        Assert.True(xy / Math.Sqrt(xx * yy) < -0.8, "пиксели надписи не идут снизу слева вверх направо");
        Assert.InRange(avgX, 400, 440);
        Assert.InRange(avgY, 574, 614);
    }

    [Fact]
    public void Repetition_adds_visible_blocks_beyond_the_center_and_respects_margins()
    {
        using var single = Render(StampPlacement.Center);
        using var repeated = Render(StampPlacement.Tiled);
        var centerInk = Ink(single);
        var repeatedInk = Ink(repeated);
        Assert.True(repeatedInk.Count > centerInk.Count * 3);
        Assert.Contains(repeatedInk, point => point.Y < 350);
        Assert.Contains(repeatedInk, point => point.Y > 850);
        Assert.All(repeatedInk, point =>
        {
            Assert.InRange(point.X, 8 * 4, 840 - 8 * 4);
            Assert.InRange(point.Y, 8 * 4, 1188 - 8 * 4);
        });
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

    private static SKBitmap Render(StampPlacement placement)
    {
        var bitmap = new SKBitmap(840, 1188);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        WatermarkStamp.Draw(canvas, bitmap.Width, bitmap.Height,
                            ["КОПИЯ", "Экземпляр № 1", "Выдан: Иванов Иван Иванович", "Дата выдачи: 09.11.2026"],
                            WatermarkStamp.Default, new StampLayout(placement), 4);
        return bitmap;
    }

    private static List<(int X, int Y)> Ink(SKBitmap bitmap)
    {
        var points = new List<(int, int)>();
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
            if (bitmap.GetPixel(x, y).Red < 235) points.Add((x, y));
        return points;
    }
}
