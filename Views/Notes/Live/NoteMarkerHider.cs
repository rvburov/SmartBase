using AvaloniaEdit.Rendering;
using SmartBase.Core.Notes;

namespace SmartBase.Views.Notes.Live;

/// <summary>
/// Прячет служебные символы разметки — но не на той строке, где стоит каретка или идет выделение.
/// Это и есть «динамический просмотр»: пока пишешь абзац, звездочки и решетки видны и правятся, а
/// стоит увести курсор — строка становится готовым текстом.
///
/// Маркер списка не прячется, а подменяется точкой: убрать «-» совсем значило бы сдвинуть строку
/// влево и потерять сам вид списка.
/// </summary>
internal sealed class NoteMarkerHider : VisualLineElementGenerator
{
    private int _revealFrom = 1;
    private int _revealTo   = 1;

    public NoteMarkupLayout Layout { get; set; } = NoteMarkupLayout.Empty;
    public bool Enabled { get; set; }

    /// <summary>Строки, на которых маркеры показываются: строка каретки либо все строки выделения.</summary>
    public void Reveal(int from, int to)
    {
        _revealFrom = Math.Min(from, to);
        _revealTo   = Math.Max(from, to);
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        if (!Enabled) return -1;

        var line = CurrentContext.Document.GetLineByOffset(startOffset);
        if (line.LineNumber >= _revealFrom && line.LineNumber <= _revealTo) return -1;

        // Строго в пределах строки: элемент, вылезший за ее конец, затянул бы следующую строку
        // в ту же визуальную (так работает сворачивание) и упал бы на переводе строки.
        return Layout.FirstMarkerFrom(startOffset, line.EndOffset);
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        if (!Layout.TryMarkerAt(offset, out var m)) return null;

        var line = CurrentContext.Document.GetLineByOffset(offset);
        var len  = Math.Min(m.Length, line.EndOffset - offset);
        if (len <= 0) return null;

        return m.Replacement is { Length: > 0 } text
            ? new FormattedTextElement(text, len)
            : new HiddenMarkerElement(len);
    }
}
