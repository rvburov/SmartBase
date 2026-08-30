using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;

namespace SmartBase.Controls.Markup;

/// <summary>
/// Абзац размеченного текста: обычный <see cref="SelectableTextBlock"/> плюс то, чего в
/// строчной разметке Avalonia нет вовсе.
///
/// Задача, из-за которой понадобился свой класс: коробка кода внутри строки и заливка
/// <c>==подсветки==</c>. У <c>Run</c> есть <c>Background</c>, но он закрашивает ровно коробку
/// глифов — без воздуха и без скругления. Положить вместо прогона настоящий контрол
/// (<c>InlineUIContainer</c>) можно, но тогда кусок становится неделимым: длинный путь в
/// обратных кавычках не переносится и вылезает за край блока.
///
/// Поэтому фон рисуется САМ, по диапазонам символов: <c>TextLayout.HitTestTextRange</c> отдает
/// прямоугольники под любым куском текста — по одному на каждую строку, на которую он попал.
/// Текст при этом остается обычными прогонами и переносится как ни в чем не бывало, а коробка
/// на переносе аккуратно разваливается надвое — ровно как в браузере.
///
/// Тем же способом сделаны ссылки: подчеркивание под курсором и клик ищут диапазон, а не
/// контрол, поэтому длинная подпись ссылки переносится по словам.
/// </summary>
internal sealed class MarkupTextBlock : SelectableTextBlock
{
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    /// <summary>Заливка под куском текста: коробка кода, подсветка.</summary>
    private readonly record struct Box(
        int Start, int Length, IBrush Fill, CornerRadius Radius, double PadX, double PadY);

    /// <summary>Ссылка: диапазон, адрес и прогоны, которым на время нажатия меняют цвет.</summary>
    private sealed record Link(int Start, int Length, string Url, List<Run> Runs);

    private readonly List<Box>  _boxes = [];
    private readonly List<Link> _links = [];

    /// <summary>Начало отрисовки текста в координатах контрола. Ставится при отрисовке;
    /// до первой отрисовки попадать пальцем некуда, поэтому ноль безопасен.</summary>
    private Point _origin;

    private int _hovered = -1;
    private int _pressed = -1;

    /// <summary>Нажали на ссылку. Адрес отдается как есть — решает получатель.</summary>
    public event EventHandler<string>? LinkActivated;

    /// <summary>
    /// Разрешено ли выделять текст мышью. По умолчанию нет: выделение включено только там, где
    /// текст читают и цитируют (инструкция пользователя). Выключаем не свойством базового
    /// класса — такого у него нет, — а тем, что не пускаем указатель в его обработчики.
    /// </summary>
    private bool _selectable;

    public bool IsSelectable
    {
        get => _selectable;
        init
        {
            _selectable = value;
            // Абзацев на экране бывает больше сотни (инструкция пользователя), и каждый
            // фокусируемый — это лишняя остановка Tab. Фокус нужен только выделяемому тексту:
            // без него не работает копирование с клавиатуры.
            Focusable = value;
        }
    }

    public MarkupTextBlock()
    {
        TextWrapping   = TextWrapping.Wrap;
        // Одна высота на ВСЕ строки, включая заголовки, — так же, как в редакторе заметок.
        LineHeight     = MarkupTheme.LineHeight;
        // Кисть выделения зарегистрирована БЕЗ значения по умолчанию: не задать — подсветки
        // не будет вовсе.
        SelectionBrush = MarkupTheme.Selection;
        Focusable      = false;
    }

    /// <summary>
    /// «Выделить все» вручную: штатный <c>SelectAll</c> берет длину из <c>Text</c>, а у нас текст
    /// живет в прогонах и <c>Text</c> пуст — выделялось бы ноль символов.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var selectAll = TopLevel.GetTopLevel(this)?.PlatformSettings?.HotkeyConfiguration.SelectAll;
        if (IsSelectable && selectAll?.Any(g => g.Matches(e)) == true)
        {
            SelectionStart = 0;
            SelectionEnd   = Inlines?.Text?.Length ?? 0;
            e.Handled      = true;
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>
    /// Заливка добавляется так, чтобы широкие лежали ниже узких: код внутри подсветки должен
    /// остаться видимым, а не скрыться под ее подложкой. Список короткий (несколько коробок на
    /// абзац), поэтому место ищется перебором — сортировать на каждой отрисовке дороже.
    /// </summary>
    public void AddBox(int start, int length, IBrush fill, CornerRadius radius, double padX, double padY)
    {
        if (length <= 0) return;

        var at = 0;
        while (at < _boxes.Count && _boxes[at].Length >= length) at++;
        _boxes.Insert(at, new Box(start, length, fill, radius, padX, padY));
    }

    public void AddLink(int start, int length, string url, List<Run> runs)
    {
        if (length > 0) _links.Add(new Link(start, length, url, runs));
    }

    public bool HasLinks => _links.Count > 0;

    // ── Отрисовка ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Единственная точка, где известно, куда лег текст. Заливки рисуются ДО текста (иначе
    /// закрасят его), подчеркивание ссылки — ПОСЛЕ: под курсором оно должно быть видно поверх
    /// подложки, если ссылка попала внутрь цитаты или выносного блока.
    /// </summary>
    protected override void RenderTextLayout(DrawingContext context, Point origin)
    {
        // Ровно то же начало, с которого базовый класс рисует текст: раскладку он сдвигает на
        // левый вынос глифов, и без этой поправки коробки поехали бы относительно букв.
        var text = origin + new Point(TextLayout.OverhangLeading, 0);
        _origin = text;

        foreach (var box in _boxes)
        foreach (var rect in TextLayout.HitTestTextRange(box.Start, box.Length))
            context.DrawRectangle(box.Fill, null,
                                  rect.Translate(new Vector(text.X, text.Y))
                                      .Inflate(new Thickness(box.PadX, box.PadY)),
                                  box.Radius.TopLeft, box.Radius.TopLeft);

        base.RenderTextLayout(context, origin);

        if (_hovered < 0 || _hovered >= _links.Count) return;

        var link = _links[_hovered];
        var pen  = new Pen(MarkupTheme.Accent, 1);
        foreach (var rect in TextLayout.HitTestTextRange(link.Start, link.Length))
        {
            var y = rect.Bottom + text.Y - 1;
            context.DrawLine(pen, new Point(rect.Left + text.X, y), new Point(rect.Right + text.X, y));
        }
    }

    // ── Указатель ───────────────────────────────────────────────────────────────

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (IsSelectable) base.OnPointerMoved(e);

        var hit = LinkAt(e.GetPosition(this));
        if (hit == _hovered) return;

        _hovered = hit;
        Cursor   = hit >= 0 ? HandCursor : null;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hovered < 0) return;

        _hovered = -1;
        Cursor   = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (IsSelectable) base.OnPointerPressed(e);

        _pressed = LinkAt(e.GetPosition(this));
        if (_pressed >= 0) Fade(_links[_pressed], true);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (IsSelectable) base.OnPointerReleased(e);

        var pressed = _pressed;
        _pressed = -1;
        if (pressed < 0) return;

        Fade(_links[pressed], false);
        // Отпустили там же, где нажали, — это клик. Съехали на другую ссылку или мимо —
        // человек передумал, и переход не нужен.
        if (LinkAt(e.GetPosition(this)) == pressed) LinkActivated?.Invoke(this, _links[pressed].Url);
    }

    /// <summary>Нажатая ссылка гаснет — единственный отклик, который у текста вообще есть:
    /// подсветить фон значило бы спорить с выделением, а сдвинуть текст нельзя.</summary>
    private void Fade(Link link, bool on)
    {
        var brush = on ? Faded(MarkupTheme.Accent) : MarkupTheme.Accent;
        foreach (var run in link.Runs) run.Foreground = brush;
    }

    private static IBrush Faded(IBrush source) =>
        source is ISolidColorBrush s ? new SolidColorBrush(s.Color, 0.7) : source;

    /// <summary>Номер ссылки под точкой или -1. Точка переводится в координаты раскладки —
    /// того же начала, с которого ее рисовали.</summary>
    private int LinkAt(Point point)
    {
        if (_links.Count == 0) return -1;

        var hit = TextLayout.HitTestPoint(point - _origin);
        if (!hit.IsInside) return -1;

        for (var i = 0; i < _links.Count; i++)
            if (hit.TextPosition >= _links[i].Start && hit.TextPosition < _links[i].Start + _links[i].Length)
                return i;
        return -1;
    }
}
