namespace SmartBase.Extensions.PdfWatermark;

public enum StampPlacement { Center, Tiled }

/// <summary>Размеры блока в его собственных осях, до поворота. Все расстояния в миллиметрах.</summary>
public sealed record StampLayout(
    StampPlacement Placement = StampPlacement.Center,
    double BlockWidthMm = 75,
    double BlockHeightMm = 50,
    double GapXmm = 10,
    double GapYmm = 12,
    double MarginMm = 8)
{
    public static readonly StampLayout Default = new();

    public void Validate()
    {
        if (!Enum.IsDefined(Placement))
            throw new PdfWatermarkException("Выберите размещение водяного знака.");
        Check(BlockWidthMm, 20, 300, "Ширина блока");
        Check(BlockHeightMm, 15, 200, "Высота блока");
        Check(GapXmm, 0, 100, "Промежуток между столбцами");
        Check(GapYmm, 0, 100, "Промежуток между рядами");
        Check(MarginMm, 0, 50, "Отступ от краев страницы");
    }

    private static void Check(double value, double min, double max, string name)
    {
        if (!double.IsFinite(value) || value < min || value > max)
            throw new PdfWatermarkException($"{name}: укажите значение от {min} до {max} мм.");
    }

    /// <summary>
    /// Сетка в повернутых осях: вместе с блоками по диагонали идут сами ряды. В расчет
    /// попадают только целые блоки внутри полей страницы, включая их повернутые углы.
    /// </summary>
    public StampGrid Plan(double width, double height, double pixelsPerMm)
    {
        Validate();
        if (!double.IsFinite(width) || !double.IsFinite(height) ||
            !double.IsFinite(pixelsPerMm) || width <= 0 || height <= 0 || pixelsPerMm <= 0)
            throw new PdfWatermarkException("Не удалось определить размеры страницы.");

        var margin = MarginMm * pixelsPerMm;
        var roomW = width - margin * 2;
        var roomH = height - margin * 2;
        if (roomW <= 0 || roomH <= 0)
            throw new PdfWatermarkException("Отступы занимают всю страницу. Уменьшите отступ от краев.");

        var angle = WatermarkStamp.AngleFor(width, height);
        var radians = angle * Math.PI / 180;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var w = BlockWidthMm * pixelsPerMm;
        var h = BlockHeightMm * pixelsPerMm;
        var boundW = Math.Abs(cos) * w + Math.Abs(sin) * h;
        var boundH = Math.Abs(sin) * w + Math.Abs(cos) * h;
        // Небольшой лист получает вписанный блок. Ниже текстовый расчет проверит, что буквы
        // остались читаемыми: молча ужимать многословную надпись до нескольких пикселей нельзя.
        var fit = Math.Min(1, Math.Min(roomW / boundW, roomH / boundH));
        w *= fit;
        h *= fit;
        boundW *= fit;
        boundH *= fit;

        var centers = new List<StampCenter> { new(0, 0) };
        if (Placement == StampPlacement.Tiled)
        {
            var stepX = w + GapXmm * pixelsPerMm;
            var stepY = h + GapYmm * pixelsPerMm;
            var columnCount = Math.Ceiling((Math.Abs(cos) * roomW + Math.Abs(sin) * roomH) / stepX / 2);
            var rowCount = Math.Ceiling((Math.Abs(sin) * roomW + Math.Abs(cos) * roomH) / stepY / 2);
            // Неограниченно большой MediaBox не должен породить миллионы вызовов Skia.
            if (!double.IsFinite(columnCount) || !double.IsFinite(rowCount) ||
                columnCount > 10_000 || rowCount > 10_000 ||
                (columnCount * 2 + 1) * (rowCount * 2 + 1) > 20_000)
                throw new PdfWatermarkException("Слишком много блоков на странице. Увеличьте размеры блока или промежутки.");

            var columns = (int)columnCount;
            var rows = (int)rowCount;

            for (var row = -rows; row <= rows; row++)
            for (var column = -columns; column <= columns; column++)
            {
                if (row == 0 && column == 0) continue;
                var x = column * stepX;
                var y = row * stepY;
                var pageX = cos * x - sin * y;
                var pageY = sin * x + cos * y;
                if (Math.Abs(pageX) + boundW / 2 <= roomW / 2 + 0.001 &&
                    Math.Abs(pageY) + boundH / 2 <= roomH / 2 + 0.001)
                    centers.Add(new(x, y));
            }
        }

        return new(w, h, angle, margin, centers);
    }
}

/// <summary>Центр блока относительно центра страницы, в повернутых осях сетки.</summary>
public sealed record StampCenter(double X, double Y);

public sealed record StampGrid(double Width, double Height, double Angle, double Margin,
                               IReadOnlyList<StampCenter> Centers);
