using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using SmartBase.Core.Notes;

namespace SmartBase.Views.Notes.Live;

/// <summary>
/// Воздух над первой и под последней строкой заметки.
///
/// Обычным способом его не задать. <c>TextEditor.Padding</c> шаблоном уходит на его
/// <c>ScrollViewer</c>, а при ЛОГИЧЕСКОЙ прокрутке <c>ScrollContentPresenter</c> берет
/// <c>Extent</c> прямо у <c>TextView</c> и отступ в него не включает. Получается не воздух, а
/// рамка: текст обрезается по ней и при листании не доезжает до линий шапки и футера — в отличие
/// от «Просмотра», где отступ висит на содержимом внутри прокрутки и уезжает вместе с ним.
///
/// Поэтому отступ заводится там же, где живет текст, — в высоте крайних строк. Генератор ставит в
/// начало первой строки и в конец последней прогон НУЛЕВОЙ ширины, но выше обычного: первый
/// поднимает базовую линию (воздух над текстом), второй растит строку вниз (воздух под текстом).
/// Высота строки уходит в дерево высот, дерево дает <c>DocumentHeight</c>, тот — <c>Extent</c>
/// прокрутки. То есть отступ прокручивается вместе с текстом ровно как <c>Margin</c> содержимого в
/// обычном <c>ScrollViewer</c>: у начала и конца заметки он есть, при листании его нет.
///
/// Элемент занимает НОЛЬ символов документа — на текст, каретку и копирование он не влияет вовсе.
/// Такие элементы разрешены: <c>VisualLine</c> после элемента нулевой длины двигает разбор строки
/// на символ вперед, поэтому вечного цикла не выходит.
/// </summary>
internal sealed class NoteAirGenerator(double air) : VisualLineElementGenerator
{
    // Две точки интереса: самое начало документа и самый его конец. На остальных строках
    // возвращенный конец документа просто не совпадет с их границами и будет пропущен.
    public override int GetFirstInterestedOffset(int startOffset) =>
        NoteAir.InterestFrom(startOffset, CurrentContext.Document.TextLength);

    public override VisualLineElement? ConstructElement(int offset)
    {
        var (top, bottom) = NoteAir.At(offset, CurrentContext.Document.TextLength, air, TrailingBlank());
        return top > 0 || bottom > 0 ? new AirElement(top, bottom) : null;
    }

    /// <summary>
    /// Высота пустой строки в конце файла — той, что дает перевод строки в самом конце. Снизу она
    /// добавляет воздуха сама, сверху такой строки нет, и разницу видно глазом.
    /// </summary>
    private double TrailingBlank()
    {
        var doc = CurrentContext.Document;
        return doc.LineCount > 1 && doc.GetLineByNumber(doc.LineCount).Length == 0
            ? CurrentContext.TextView.DefaultLineHeight
            : 0;
    }

    private sealed class AirElement(double top, double bottom) : VisualLineElement(1, 0)
    {
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) =>
            // Меры берем у обычной строки: воздух должен быть одинаковым независимо от того, чем
            // заметка начинается — абзацем или крупным заголовком.
            new AirRun(TextRunProperties, context.TextView.DefaultLineHeight,
                       context.TextView.DefaultBaseline, top, bottom);

        /// <summary>Каретке тут стоять негде: символов документа за элементом нет.</summary>
        public override int GetNextCaretPosition(int visualColumn, AvaloniaEdit.Document.LogicalDirection direction,
                                                 CaretPositioningMode mode) => -1;

        private sealed class AirRun(TextRunProperties props, double height, double baseline,
                                    double top, double bottom) : DrawableTextRun
        {
            public override ReadOnlyMemory<char> Text       => ReadOnlyMemory<char>.Empty;
            public override TextRunProperties    Properties => props;
            // Ширины нет — только высота. Строка становится выше на величину воздуха, а базовая
            // линия опускается ровно на верхнюю его часть, унося текст вниз.
            public override Size                 Size       => new(0, height + top + bottom);
            public override double               Baseline   => baseline + top;

            public override void Draw(DrawingContext drawingContext, Point origin) { }
        }
    }
}
