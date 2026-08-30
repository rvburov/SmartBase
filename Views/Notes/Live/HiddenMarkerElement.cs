using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace SmartBase.Views.Notes.Live;

/// <summary>
/// Служебные символы разметки, убранные с экрана: звездочки жирного, решетки заголовка, «&gt;»
/// цитаты, ограждения блока кода. В документе они остаются на месте — прячется только отрисовка.
///
/// Публичного «скрытого элемента» в AvaloniaEdit нет, а нулевую визуальную длину базовый класс
/// запрещает (<c>VisualLineElement</c> бросает при <c>visualLength &lt; 1</c>). Поэтому элемент
/// занимает ОДНУ визуальную колонку, но рисует прогон нулевой ширины: место на экране он не
/// съедает, а раскладка строки остается согласованной.
/// </summary>
internal sealed class HiddenMarkerElement(int documentLength) : VisualLineElement(1, documentLength)
{
    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) =>
        // Высота и базовая линия — как у обычного текста: прогон нулевой ШИРИНЫ, а не нулевой
        // высоты. Иначе строка, состоящая из одних спрятанных символов, схлопнулась бы, и текст
        // под ней подпрыгнул.
        new EmptyRun(TextRunProperties, context.TextView.DefaultLineHeight, context.TextView.DefaultBaseline);

    /// <summary>
    /// Каретка внутрь спрятанного не заходит: стрелка перепрыгивает маркер целиком. Иначе человек
    /// нажимал бы «вправо» и курсор замирал бы на месте — символы-то есть, но их не видно.
    /// </summary>
    public override int GetNextCaretPosition(int visualColumn, AvaloniaEdit.Document.LogicalDirection direction,
                                             CaretPositioningMode mode) => -1;

    private sealed class EmptyRun(TextRunProperties props, double height, double baseline) : DrawableTextRun
    {
        public override ReadOnlyMemory<char> Text       => ReadOnlyMemory<char>.Empty;
        public override TextRunProperties    Properties => props;
        public override Size                 Size       => new(0, height);
        public override double               Baseline   => baseline;

        public override void Draw(DrawingContext drawingContext, Point origin) { }
    }
}
