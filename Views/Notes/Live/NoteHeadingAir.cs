using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using SmartBase.Core.Notes;

namespace SmartBase.Views.Notes.Live;

/// <summary>
/// Воздух вокруг заголовка в редакторе: пустая строка там, где ее нет в файле. Ровно то же делает
/// «Просмотр» (<c>Controls/Markup</c>), и режимы совпадают.
///
/// Как это возможно, хотя высота строки в AvaloniaEdit жестко фиксирована. Фиксирована высота
/// ОДНОЙ текстовой строки (<c>VisualLineTextParagraphProperties.LineHeight</c> = кегль × 1,35, и
/// <c>TextLineImpl.CreateLineMetrics</c> возвращает к ней любую посчитанную высоту). Но строка
/// ДОКУМЕНТА состоит из скольких угодно текстовых строк — на этом стоит перенос по словам:
/// <c>TextView</c> зовет <c>FormatLine</c> в цикле, пока не разложит строку целиком. Значит
/// разрыв посреди строки дает лишнюю текстовую строку, а с ней и лишнюю высоту.
///
/// Отсюда и устройство: элемент нулевой длины в КОНЦЕ строки отдает <see cref="TextEndOfLine"/>,
/// текст остается в первой текстовой строке, а вторая получается пустой. Место занято настоящей
/// строкой раскладки, поэтому воздух прокручивается вместе с текстом и попадает в высоту
/// документа.
///
/// Именно в конце, а не в начале следующей строки — хотя на вид это одно и то же место. Пустая
/// строка принадлежит той строке документа, в которой стоит разрыв, и каретка следующей строки
/// про нее ничего не знает. Поставь разрыв в начало — и каретка, встав на начало строки, рисуется
/// на пустой строке НАД текстом: на экране заголовок с пустой строкой под ним, а не с воздухом.
/// </summary>
internal sealed class NoteHeadingAir : VisualLineElementGenerator
{
    private readonly List<NoteSpan> _buffer = new(8);

    /// <summary>Разметка документа. Ставится вместе с разметкой раскраски и пряталки.</summary>
    public NoteMarkupLayout Layout { get; set; } = NoteMarkupLayout.Empty;

    /// <summary>Выключено в режиме «Просмотр исходного кода»: там текст показывают как есть.</summary>
    public bool Enabled { get; set; }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        if (!Enabled) return -1;

        var line = CurrentContext.Document.GetLineByOffset(startOffset);

        // Только в самом начале строки: разрыв посреди разорвал бы текст. Первая строка
        // документа воздуха не получает — сверху его дает рамка редактора.
        //
        // Пустая строка ПОД заголовком делается разрывом в начале СЛЕДУЮЩЕЙ строки: разрыв в
        // конце строки лишней текстовой строки не дает, цикл раскладки на нем и заканчивается.
        if (startOffset > line.Offset || line.PreviousLine is not { } previous) return -1;

        return NeedsAir(previous, line) ? line.Offset : -1;
    }

    public override VisualLineElement ConstructElement(int offset) => new BreakElement();

    /// <summary>
    /// Пустая строка нужна на стыке «заголовок ↔ текст», и только если в файле ее еще нет:
    /// когда автор уже отбил заголовок пустой строкой, второй просвет был бы лишним. Ровно так
    /// же считает «Просмотр»: берется большее из пустых строк исходника и одной строки.
    /// </summary>
    private bool NeedsAir(DocumentLine previous, DocumentLine line) =>
        previous.Length > 0 && line.Length > 0 && (IsHeading(previous) || IsHeading(line));

    private bool IsHeading(DocumentLine line)
    {
        _buffer.Clear();
        Layout.SpansIn(line.Offset, line.EndOffset, _buffer);

        foreach (var span in _buffer)
            if (span.Style is >= NoteStyle.Heading1 and <= NoteStyle.Heading6) return true;
        return false;
    }

    /// <summary>
    /// Разрыв текстовой строки. Символов документа не занимает (<c>documentLength</c> = 0),
    /// поэтому на текст, каретку и копирование не влияет; одна визуальная колонка — потому что
    /// нулевую базовый класс запрещает.
    /// </summary>
    private sealed class BreakElement() : VisualLineElement(1, 0)
    {
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) =>
            new TextEndOfLine(1);

        /// <summary>Каретка внутрь не заходит: пустая строка — не место в тексте.</summary>
        public override int GetNextCaretPosition(int visualColumn,
                                                 AvaloniaEdit.Document.LogicalDirection direction,
                                                 CaretPositioningMode mode) => -1;
    }
}
