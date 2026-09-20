namespace SmartBase.Core.Documents;

/// <summary>Прямоугольник на странице в пунктах PDF: начало координат — ЛЕВЫЙ НИЖНИЙ угол.</summary>
public readonly record struct DocPageBox(double Left, double Bottom, double Right, double Top)
{
    public double Width  => Right - Left;
    public double Height => Top - Bottom;
}

/// <summary>Прямоугольник на экране: начало координат — левый ВЕРХНИЙ угол.</summary>
public readonly record struct DocScreenBox(double X, double Y, double Width, double Height);

/// <summary>
/// Перевод координат страницы PDF в координаты показанной картинки.
///
/// Чистая арифметика в отдельном файле не ради красоты: у PDF ось Y идет СНИЗУ ВВЕРХ, а у
/// экрана — сверху вниз, и переворот здесь единственное место, где легко ошибиться на всю
/// высоту страницы. Ошибка эта тихая: подсветка просто окажется не на том слове, а на глаз
/// разница между «низ страницы» и «верх» видна только если знать, что искать.
/// </summary>
public static class DocPageGeometry
{
    /// <summary>
    /// Прямоугольник слова на показанной странице.
    /// </summary>
    /// <param name="box">Место слова в пунктах, как его отдает разборщик PDF.</param>
    /// <param name="pageHeight">Высота страницы в пунктах — по ней переворачивается ось.</param>
    /// <param name="scale">Во сколько раз показанная страница крупнее пунктов.</param>
    /// <param name="padding">
    /// Насколько расширить прямоугольник во все стороны. Подсветка по точным границам букв
    /// выглядит впритык — слово будто зажато; пара десятых пункта дает ему воздух.
    /// </param>
    public static DocScreenBox ToScreen(DocPageBox box, double pageHeight, double scale,
                                        double padding = 0)
    {
        var left   = (box.Left   - padding) * scale;
        var right  = (box.Right  + padding) * scale;
        // Верх прямоугольника на экране — это ВЕРХ слова на странице, отсчитанный от верхнего
        // края: pageHeight - Top. Возьми здесь Bottom — и подсветка уехала бы на высоту слова.
        var top    = (pageHeight - box.Top    - padding) * scale;
        var bottom = (pageHeight - box.Bottom + padding) * scale;

        return new DocScreenBox(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>
    /// Во сколько раз показанная страница крупнее пунктов. Считается по ШИРИНЕ: высоту
    /// показанной страницы задает та же пропорция, и считать масштаб по ней значило бы получить
    /// два слегка разных числа из-за округления размеров картинки.
    /// </summary>
    public static double ScaleFor(double displayWidth, double pageWidth) =>
        pageWidth > 0 ? displayWidth / pageWidth : 1;
}
