namespace SmartBase.Extensions.PdfWatermark;

public enum StampPlacement { Center, Tiled }

/// <summary>Размеры блока в его собственных осях, до поворота. Все расстояния в миллиметрах.</summary>
public sealed record StampLayout(
    StampPlacement Placement = StampPlacement.Center,
    double BlockWidthMm = 130,
    double BlockHeightMm = 115,
    double GapXmm = 6,
    double GapYmm = 7,
    double MarginMm = 0)
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
    /// Сетка в повернутых осях: вместе с блоками по диагонали идут сами ряды. Повторение
    /// продолжается за края листа, а видимые части обрезаются при отрисовке по полям страницы.
    /// Измеренные размеры текста в пикселях задают шаг без пустого места внутри блока.
    /// </summary>
    public StampGrid Plan(double width, double height, double pixelsPerMm,
                           double? measuredTextWidth = null, double? measuredTextHeight = null)
    {
        Validate();
        if (!double.IsFinite(width) || !double.IsFinite(height) ||
            !double.IsFinite(pixelsPerMm) || width <= 0 || height <= 0 || pixelsPerMm <= 0)
            throw new PdfWatermarkException("Не удалось определить размеры страницы.");

        var margin = MarginMm * pixelsPerMm;
        var roomW = width - margin * 2;
        var roomH = height - margin * 2;
        if (!double.IsFinite(margin) || roomW <= 0 || roomH <= 0)
            throw new PdfWatermarkException("Отступы занимают всю страницу. Уменьшите отступ от краев.");

        var angle = WatermarkStamp.AngleFor(width, height);
        var radians = angle * Math.PI / 180;
        var cos = Math.Abs(Math.Cos(radians));
        var sin = Math.Abs(Math.Sin(radians));
        var w = BlockWidthMm * pixelsPerMm;
        var h = BlockHeightMm * pixelsPerMm;
        if (!double.IsFinite(w) || !double.IsFinite(h))
            throw new PdfWatermarkException("Не удалось определить размеры блока.");

        if (Placement == StampPlacement.Center)
        {
            // Один центральный блок вписывается в небольшой лист. Ниже текстовый расчет
            // проверит читаемость; повторяющийся узор сохраняет физический размер букв.
            var boundW = cos * w + sin * h;
            var boundH = sin * w + cos * h;
            var fit = Math.Min(1, Math.Min(roomW / boundW, roomH / boundH));
            return new(w * fit, h * fit, angle, margin, [new(0, 0)]);
        }

        w = measuredTextWidth ?? w;
        h = measuredTextHeight ?? h;
        if (!double.IsFinite(w) || !double.IsFinite(h) || w <= 0 || h <= 0)
            throw new PdfWatermarkException("Не удалось определить размеры текста знака.");
        var stepX = w + GapXmm * pixelsPerMm;
        var stepY = h + GapYmm * pixelsPerMm;
        if (!double.IsFinite(stepX) || !double.IsFinite(stepY))
            throw new PdfWatermarkException("Не удалось определить промежутки между блоками.");
        // Дополнительный ряд с каждой стороны покрывает крайние части букв, как в
        // согласованном образце. Целый блок больше не обязан помещаться на листе.
        var columnCount = Math.Ceiling((cos * roomW + sin * roomH) / stepX / 2) + 1;
        var rowCount = Math.Ceiling((sin * roomW + cos * roomH) / stepY / 2) + 1;
        // Ограничение проверяется до преобразования в int и включает крайние ряды.
        if (!double.IsFinite(columnCount) || !double.IsFinite(rowCount) ||
            columnCount > 10_000 || rowCount > 10_000 ||
            (columnCount * 2 + 1) * (rowCount * 2 + 1) > 20_000)
            throw new PdfWatermarkException("Слишком много блоков на странице. Увеличьте размеры блока или промежутки.");

        var columns = (int)columnCount;
        var rows = (int)rowCount;
        var centers = new List<StampCenter>((columns * 2 + 1) * (rows * 2 + 1));
        for (var row = -rows; row <= rows; row++)
        for (var column = -columns; column <= columns; column++)
            centers.Add(new(column * stepX, row * stepY));

        return new(w, h, angle, margin, centers);
    }

}

/// <summary>Центр блока относительно центра страницы, в повернутых осях сетки.</summary>
public sealed record StampCenter(double X, double Y);

public sealed record StampGrid(double Width, double Height, double Angle, double Margin,
                               IReadOnlyList<StampCenter> Centers);
