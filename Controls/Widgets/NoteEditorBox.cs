using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using SmartBase.Controls.Components;
using SmartBase.Core.Notes;
using SmartBase.Utils;
using SmartBase.Views.Notes;
using SmartBase.Views.Notes.Live;

namespace SmartBase.Controls.Widgets;

/// <summary>
/// Поле ввода заметки. Внутри — <see cref="TextEditor"/> из AvaloniaEdit вместо обычного
/// <c>TextBox</c>: у того одно начертание на все поле, то есть «живой» markdown (жирный текст
/// жирным прямо при наборе) на нем не сделать в принципе — можно покрасить либо весь текст,
/// либо ничего.
///
/// Виджет решает те же две задачи, что <see cref="AppMarkdown"/> для показа markdown:
///
/// 1. Вешает на СЕБЯ чужую тему (<c>avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml</c>).
///    Глобально в App.axaml ее не подключаем: вместе с видом редактора она принесла бы в общий
///    словарь стили панели поиска и списка автодополнения, которых в приложении нет.
/// 2. Приводит вид к токенам приложения. Глобальные стили из Typography.axaml/Controls.axaml
///    целятся в <c>TextBox</c> и на <c>TextEditor</c> не ложатся, а своя тема AvaloniaEdit
///    приносит, например, синее выделение <c>#660000ff</c> — единственное синее на все окно.
///
/// Класс <c>notesEditor</c> виджет НЕ получает: он объявлен для <c>TextBox</c> и используется
/// еще на трех чужих экранах (настройки, профиль, панель записи) — трогать его нельзя.
/// </summary>
public sealed class NoteEditorBox : UserControl
{
    private static readonly Uri BaseUri  = new("avares://SmartBase/");
    private static readonly Uri ThemeUri = new("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml");

    /// <summary>Воздух вокруг текста. Одно число на все четыре стороны — как у «Просмотра».</summary>
    private const double Air = 16;

    private readonly TextEditor _editor = new();
    private readonly TextBlock  _watermark = new();

    private readonly NoteMarkupColorizer _colorizer = new();
    private readonly NoteMarkerHider     _hider     = new();
    private readonly NoteHeadingAir      _headingAir = new();
    private readonly DispatcherTimer     _markupTimer = new() { Interval = NoteAutosave.MarkupDebounce };

    private int  _renderedFontDelta = FontScale.Delta;
    private bool _liveMarkup;
    private int  _revealFrom = 1;
    private int  _revealTo   = 1;

    /// <summary>Текст изменился — сам человек или подстановка снаружи.</summary>
    public event EventHandler? TextChanged;

    /// <summary>
    /// Прокрутка редактора появилась и готова к связыванию с вынесенной полосой. Отдельное
    /// событие, потому что шаблон применяется позже конструктора: на момент сборки раздела
    /// <see cref="Scroller"/> еще null.
    /// </summary>
    public event EventHandler? ScrollerReady;

    public NoteEditorBox()
    {
        Styles.Add(new StyleInclude(BaseUri) { Source = ThemeUri });

        _editor.WordWrap = true;
        // Полоса у раздела вынесена отдельной колонкой (ScrollBarLink) — своих полос редактор
        // не рисует. Горизонтальной нет вовсе: строки переносятся.
        _editor.VerticalScrollBarVisibility   = ScrollBarVisibility.Hidden;
        _editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _editor.Background      = Brushes.Transparent;
        _editor.BorderThickness = default;

        // Воздух по краям — 16 со всех сторон, как у «Просмотра» (Margin у AppMarkdown).
        //
        // Это рамка, а не воздух внутри содержимого: при прокрутке полоса сверху и снизу
        // остается, и текст не доезжает до линий шапки и футера. Так сделано не по недосмотру —
        // иначе в AvaloniaEdit нельзя. У него ВСЕ строки одной высоты: паразитная на первый
        // взгляд строчка `VisualLineTextParagraphProperties.LineHeight = FontSize * 1.35`
        // жестко задает высоту абзаца, а `TextLineImpl.CreateLineMetrics` в самом конце
        // возвращает посчитанную высоту к ней и сдвигает базовую линию на половину разницы.
        // Поэтому прогон любой высоты (а воздух в первой строке документа был именно им) строку
        // НЕ удлиняет: он только роняет ее текст вниз, и первая строка наезжает на следующие.
        // Ровно это и было видно сверху заметки.
        _editor.Padding = new Thickness(Air);

        // AvaloniaEdit по умолчанию разрешает прокрутку ЗА конец текста: к высоте документа он
        // прибавляет почти целый экран (`documentHeight + viewport - высота строки`), и заметку
        // можно было утянуть так, что внизу оставалось полтора экрана пустоты. В «Просмотре»
        // такого нет — там обычный ScrollViewer с готовым документом. Выравниваем поведение:
        // текст кончается там, где кончается текст.
        _editor.Options.AllowScrollBelowDocument   = false;
        _editor.Options.EnableHyperlinks           = false;
        _editor.Options.EnableEmailHyperlinks      = false;
        _editor.Options.HighlightCurrentLine       = false;
        _editor.Options.EnableRectangularSelection = false;
        _editor.Options.EnableVirtualSpace         = false;
        _editor.Options.ConvertTabsToSpaces        = true;
        _editor.Options.IndentationSize            = 4;
        // Правый клик мимо выделения обязан переносить каретку: иначе команда меню применится
        // не к тому месту, куда человек только что ткнул.
        _editor.TextArea.RightClickMovesCaret = true;

        ApplyLook();

        _editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        // Воздух заголовка — ДО пряталки: на строке заголовка оба смотрят на один и тот же
        // нулевой отступ, и разрыв строки обязан встать перед спрятанными решетками.
        _editor.TextArea.TextView.ElementGenerators.Add(_headingAir);
        _editor.TextArea.TextView.ElementGenerators.Add(_hider);
        _markupTimer.Tick += (_, _) => { _markupTimer.Stop(); Rescan(); };

        // Маркеры видны на строке каретки и на строках выделения — за обоими и следим.
        _editor.TextArea.Caret.PositionChanged += (_, _) => UpdateReveal();
        _editor.TextArea.SelectionChanged      += (_, _) => UpdateReveal();

        _editor.TextChanged += (_, _) =>
        {
            UpdateWatermark();
            // Разметку считаем по паузе: разбор идет по всему тексту заметки, а нажатия идут
            // подряд. Перезапуск таймера на каждый символ — тот же прием, что у автосохранения.
            if (_liveMarkup) { _markupTimer.Stop(); _markupTimer.Start(); }
            TextChanged?.Invoke(this, EventArgs.Empty);
        };

        _editor.TemplateApplied += (_, e) =>
        {
            Scroller = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
            ScrollerReady?.Invoke(this, EventArgs.Empty);
        };

        // Меню редактора: русские Вырезать/Копировать/Вставить плюс команды разметки. Свое,
        // потому что общее меню TextBox из Controls.axaml на TextEditor не распространяется.
        _editor.TextArea.ContextMenu = NoteEditorMenu.Build(Run);

        // AvaloniaEdit принудительно ставит свою англоязычную панель поиска в OnApplyTemplate.
        // Снять ее вызовом Uninstall() нельзя: он идет через Close(), а тот трогает части
        // шаблона панели, которых нет, пока панель ни разу не показали, — и падает с NRE прямо
        // при первой раскладке. Поэтому глушим сами сочетания, туннелем, до того как их увидит
        // TextArea. В приложении такого поиска нет, и в docs/user/shortcuts.md он не описан.
        AddHandler(KeyDownEvent, OnSearchKeys, RoutingStrategies.Tunnel);

        _watermark.IsHitTestVisible = false;
        // Ровно по первой строке текста: там же ее держит рамка редактора.
        _watermark.Margin           = new Thickness(Air);
        _watermark.VerticalAlignment   = VerticalAlignment.Top;
        _watermark.HorizontalAlignment = HorizontalAlignment.Left;
        _watermark.IsVisible = false;

        Content = new Panel { Children = { _editor, _watermark } };
    }

    // ── Фасад для раздела ───────────────────────────────────────────────────────

    /// <summary>Текст заметки. Присвоение сбрасывает историю правок — так и нужно при смене файла.</summary>
    public string Text
    {
        get => _editor.Text ?? "";
        set
        {
            _editor.Text = value ?? "";
            // Смена файла — не набор текста: разметку пересчитываем сразу, чтобы заметка не
            // открывалась на долю секунды голой.
            if (_liveMarkup) { _markupTimer.Stop(); Rescan(); }
        }
    }

    public int CaretOffset     { get => _editor.CaretOffset; set => _editor.CaretOffset = value; }
    public int SelectionStart  => _editor.SelectionStart;
    public int SelectionLength => _editor.SelectionLength;

    public void Select(int start, int length) => _editor.Select(start, length);
    /// <summary>Отдать фокус самому полю ввода, а не обертке.</summary>
    public void FocusEditor() => _editor.Focus();

    /// <summary>Прокрутка редактора для <c>ScrollBarLink</c>. До применения шаблона — null.</summary>
    public ScrollViewer? Scroller { get; private set; }

    /// <summary>Внутренности для живого оформления. Наружу раздела не уходят.</summary>
    internal TextEditor Editor => _editor;
    internal TextArea   Area   => _editor.TextArea;

    /// <summary>
    /// Оформлять разметку прямо в тексте («Динамический просмотр»). Выключено — текст показан как
    /// есть («Просмотр исходного кода»). Один и тот же контрол на оба режима: переключение не
    /// пересоздает редактор и не теряет ни каретку, ни историю правок.
    /// </summary>
    public bool LiveMarkup
    {
        get => _liveMarkup;
        set
        {
            if (_liveMarkup == value) return;
            _liveMarkup = value;
            _colorizer.Enabled  = value;
            _hider.Enabled      = value;
            _headingAir.Enabled = value;

            _markupTimer.Stop();
            if (value)
            {
                SyncReveal();
                Rescan();
            }
            else
            {
                // Разметку не держим: заметку могли закрыть, а память под нее — нет.
                _colorizer.Layout  = NoteMarkupLayout.Empty;
                _hider.Layout      = NoteMarkupLayout.Empty;
                _headingAir.Layout = NoteMarkupLayout.Empty;
                _editor.TextArea.TextView.Redraw();
            }
        }
    }

    private void Rescan()
    {
        var layout = NoteMarkupScanner.Scan(_editor.Document?.Text);
        _colorizer.Layout  = layout;
        _hider.Layout      = layout;
        _headingAir.Layout = layout;
        _editor.TextArea.TextView.Redraw();
    }

    // ── Показ маркеров на строке каретки ────────────────────────────────────────

    private void UpdateReveal()
    {
        if (!_liveMarkup) return;

        var (from, to) = CaretLines();
        if (from == _revealFrom && to == _revealTo) return;

        var (oldFrom, oldTo) = (_revealFrom, _revealTo);
        _revealFrom = from;
        _revealTo   = to;
        _hider.Reveal(from, to);

        // Перерисовываем только затронутые строки. Полная перерисовка на каждое движение каретки
        // пересобирала бы весь видимый экран, и стрелка вниз начала бы спотыкаться.
        RedrawLines(oldFrom, oldTo);
        RedrawLines(from, to);
    }

    // Поставить диапазон без перерисовки — при включении режима, когда перерисовка будет своя.
    private void SyncReveal()
    {
        (_revealFrom, _revealTo) = CaretLines();
        _hider.Reveal(_revealFrom, _revealTo);
    }

    private (int From, int To) CaretLines()
    {
        var doc = _editor.Document;
        if (doc is null) return (1, 1);

        var selection = _editor.TextArea.Selection;
        if (selection is null || selection.IsEmpty) return (_editor.TextArea.Caret.Line, _editor.TextArea.Caret.Line);

        var start = Math.Clamp(_editor.SelectionStart, 0, doc.TextLength);
        var end   = Math.Clamp(start + _editor.SelectionLength, 0, doc.TextLength);
        return (doc.GetLineByOffset(start).LineNumber, doc.GetLineByOffset(end).LineNumber);
    }

    private void RedrawLines(int from, int to)
    {
        var doc = _editor.Document;
        if (doc is null) return;

        from = Math.Max(1, from);
        to   = Math.Min(doc.LineCount, to);
        if (to < from) return;

        var first = doc.GetLineByNumber(from);
        var last  = doc.GetLineByNumber(to);
        _editor.TextArea.TextView.Redraw(first.Offset, last.EndOffset - first.Offset);
    }

    /// <summary>Подсказка на пустой заметке: у AvaloniaEdit своего watermark нет.</summary>
    public string? Watermark
    {
        get => _watermark.Text;
        set { _watermark.Text = value; UpdateWatermark(); }
    }

    private static void OnSearchKeys(object? sender, KeyEventArgs e)
    {
        var mod = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (e.Key == Key.F3 || (mod && e.Key is Key.F or Key.H)) e.Handled = true;
    }

    // ── Команды разметки ────────────────────────────────────────────────────────

    /// <summary>Выполнить команду меню или горячей клавиши.</summary>
    public void Run(NoteCommand command)
    {
        switch (command)
        {
            case NoteCommand.Cut:       _editor.Cut();       return;
            case NoteCommand.Copy:      _editor.Copy();      return;
            case NoteCommand.Paste:     _editor.Paste();     return;
            case NoteCommand.SelectAll: _editor.SelectAll(); return;
        }

        var doc = _editor.Document;
        if (doc is null) return;

        var old = doc.Text;
        if (NoteEditorMenu.Apply(command, old, _editor.SelectionStart, _editor.SelectionLength) is not { } edit)
            return;
        if (string.Equals(edit.Text, old, StringComparison.Ordinal)) return;

        // Пишем ОДНУ минимальную правку, а не весь текст: иначе Ctrl+Z откатывал бы заметку
        // целиком, а синхронизатор облака видел бы файл переписанным заново.
        var (offset, remove, insert) = NoteFormat.Minimize(old, edit.Text);
        using (doc.RunUpdate()) doc.Replace(offset, remove, insert);

        var start = Math.Clamp(edit.Start, 0, doc.TextLength);
        _editor.Select(start, Math.Clamp(edit.Length, 0, doc.TextLength - start));
        _editor.Focus();
    }

    /// <summary>Сочетание клавиш в команду; null — сочетание не наше.</summary>
    public static NoteCommand? CommandFor(Key key, KeyModifiers modifiers)
    {
        var mod   = modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta);
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        if (!mod) return null;

        return (key, shift) switch
        {
            (Key.B, false) => NoteCommand.Bold,
            (Key.I, false) => NoteCommand.Italic,
            (Key.E, false) => NoteCommand.InlineCode,
            (Key.K, false) => NoteCommand.Link,
            (Key.K, true)  => NoteCommand.Strike,
            (Key.L, true)  => NoteCommand.BulletList,
            (Key.D0, _)    => NoteCommand.PlainText,
            (Key.D1, _)    => NoteCommand.Heading1,
            (Key.D2, _)    => NoteCommand.Heading2,
            (Key.D3, _)    => NoteCommand.Heading3,
            (Key.D4, _)    => NoteCommand.Heading4,
            (Key.D5, _)    => NoteCommand.Heading5,
            (Key.D6, _)    => NoteCommand.Heading6,
            _              => null,
        };
    }

    private void UpdateWatermark() =>
        _watermark.IsVisible = (_editor.Document?.TextLength ?? 0) == 0
                               && !string.IsNullOrEmpty(_watermark.Text);

    // ── Вид ─────────────────────────────────────────────────────────────────────

    // Токены приложения вместо темы AvaloniaEdit. Размер шрифта — DynamicResource: он меняется
    // настройкой «Размер шрифта» без пересоздания контрола.
    private void ApplyLook()
    {
        _editor.Bind(TemplatedControl.FontSizeProperty,   this.GetResourceObservable("FontSizeBase"));
        _editor.Bind(TemplatedControl.FontFamilyProperty, this.GetResourceObservable("FontFamilyBase"));
        _editor.Bind(TemplatedControl.ForegroundProperty, this.GetResourceObservable("BrushPrimaryText"));
        _editor.TextArea.Bind(TextArea.SelectionBrushProperty, this.GetResourceObservable("BrushTextSelection"));

        _watermark.Bind(TextBlock.FontSizeProperty,   this.GetResourceObservable("FontSizeBase"));
        _watermark.Bind(TextBlock.FontFamilyProperty, this.GetResourceObservable("FontFamilyBase"));
        _watermark.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushSecondaryText"));

        // SelectionForeground оставляем пустым намеренно: иначе выделение перекрасит текст и
        // съест оформление разметки под собой.
        _editor.TextArea.SelectionForeground = null;
        _editor.TextArea.Caret.CaretBrush    = Brush("BrushPrimaryText", Brushes.Black);
    }

    private IBrush Brush(string key, IBrush fallback) =>
        Application.Current!.TryFindResource(key, out var res) && res is IBrush b ? b : fallback;

    // ── Размер шрифта ───────────────────────────────────────────────────────────

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        FontScale.Changed += OnFontScaleChanged;
        // Размер могли изменить, пока раздел был закрыт, — догоняем.
        if (_renderedFontDelta != FontScale.Delta) OnFontScaleChanged();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        FontScale.Changed -= OnFontScaleChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnFontScaleChanged()
    {
        _renderedFontDelta = FontScale.Delta;
        // Базовый размер приедет сам по DynamicResource, но живое оформление запекает размеры
        // заголовков числом — их пересчитает перерисовка.
        _editor.TextArea.TextView.Redraw();
    }
}
