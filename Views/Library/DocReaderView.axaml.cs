using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using SmartBase.Controls.Behaviors;
using SmartBase.Core.Documents;
using SmartBase.Core.Models;
using SmartBase.Utils;

namespace SmartBase.Views.Library;

/// <summary>
/// Панель чтения документа: текст документа с подсветкой найденного.
///
/// Устройство панели — как у страницы записи в конструкторе: та же колонка главного окна, тот
/// же разделитель и та же ручка ширины (см. MainWindow.ApplyColumns). Отличие одно и важное:
/// запись правят, а документ только читают, поэтому здесь нет ни одного органа, меняющего файл.
///
/// <b>Откуда берется текст.</b> Документ разбирается ЗАНОВО (<see cref="DocReadText"/>), а не
/// достается из кеша поиска: в кеше лежит текст со схлопнутыми переводами строк — индексу так
/// правильно, а на экране это была бы одна бесконечная строка. Разбор идет в фоне с отменой:
/// открыли другой документ — прежний разбор больше никому не нужен.
///
/// <b>Почему ListBox, а не просто текст.</b> Документ бывает в миллион знаков, и одним
/// TextBlock'ом его не нарисовать. Строки лежат списком с виртуализацией, а переход к
/// совпадению — это <c>ScrollIntoView</c> по номеру строки. Выделения у списка нет: выбирать
/// тут нечего, текущее совпадение видно по цвету самого слова.
/// </summary>
public partial class DocReaderView : UserControl
{
    /// <summary>Закрыть панель — крестик в шапке.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Документ, открытый сейчас; null — панель пуста.</summary>
    public DocRecord? Document { get; private set; }

    /// <summary>Строки документа с подсветкой — список рисует по ним.</summary>
    private readonly List<ReaderLine> _lines = [];

    /// <summary>Совпадения по всему документу: номер строки и место в ней.</summary>
    private readonly List<(int Line, int Start, int Length)> _hits = [];

    private int _current = -1;

    /// <summary>Поколение чтения: ответ старого разбора не должен перерисовать новый документ.</summary>
    private int _gen;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Дебаунс правки строки поиска. Пересчет подсветки идет по всему тексту документа, и на
    /// четырех миллионах знаков он заметен — на каждое нажатие его гонять нельзя.
    /// </summary>
    private readonly DispatcherTimer _findDebounce = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public DocReaderView()
    {
        InitializeComponent();

        LinesList.ItemTemplate = new FuncDataTemplate<ReaderLine>((_, _) => new ReaderLineView(this), supportsRecycling: false);
        LinesList.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<ScrollViewer>("PART_ScrollViewer") is { } sv) ScrollBarLink.SetSource(LinesBar, sv);
        };

        CloseBtn.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        PrevBtn.Click  += (_, _) => Step(-1);
        NextBtn.Click  += (_, _) => Step(+1);

        FindBox.TextChanged += (_, _) => { _findDebounce.Stop(); _findDebounce.Start(); };
        FindBox.KeyDown     += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            // Enter — «дальше», как в любом поиске по тексту. Пересчет мог еще не случиться по
            // дебаунсу, поэтому сперва догоняем его.
            if (_findDebounce.IsEnabled) { _findDebounce.Stop(); Rehighlight(); }
            else Step(+1);
        };
        _findDebounce.Tick += (_, _) => { _findDebounce.Stop(); Rehighlight(); };
    }

    // ── Открытие и закрытие ───────────────────────────────────────────────────

    /// <summary>
    /// Показать документ. <paramref name="query"/> — запрос поиска раздела: панель открывается
    /// сразу на первом совпадении, потому что человек нажал «Прочитать» именно ради него.
    /// </summary>
    public void Show(DocRecord doc, string query)
    {
        Document = doc;
        TitleLabel.Text    = doc.DisplayTitle;
        SubtitleLabel.Text = Subtitle(doc);
        FindBox.Text       = query ?? "";
        _findDebounce.Stop();

        Reset();
        ShowState("Читаю документ…");
        _ = LoadAsync(doc, ++_gen);
    }

    /// <summary>
    /// Сменить запрос у уже открытого документа — раздел перечитали с другим словом. Текст
    /// заново не разбирается: подсветка считается по тому, что уже прочитано.
    /// </summary>
    public void SetQuery(string query)
    {
        if (Document is null || FindBox.Text == query) return;

        FindBox.Text = query ?? "";
        _findDebounce.Stop();
        Rehighlight();
    }

    /// <summary>Закрыть панель и отпустить документ — текст мегабайтами в памяти не держим.</summary>
    public void Clear()
    {
        _gen++;
        _cts?.Cancel();
        _cts     = null;
        Document = null;
        Reset();
        TitleLabel.Text    = "Документ";
        SubtitleLabel.Text = "";
        ShowState("");
    }

    private void Reset()
    {
        _lines.Clear();
        _hits.Clear();
        _current = -1;
        LinesList.ItemsSource = null;
        UpdateCounter();
    }

    private static string Subtitle(DocRecord doc)
    {
        var parts = new List<string> { doc.FileName };
        if (doc.CollectionName.Length > 0) parts.Add(doc.CollectionName);
        if (doc.DbName.Length > 0)         parts.Add(doc.DbName);
        return string.Join(" · ", parts);
    }

    // ── Чтение ────────────────────────────────────────────────────────────────

    private async Task LoadAsync(DocRecord doc, int gen)
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        DocReadResult result;
        try
        {
            result = await DocReadText.ReadAsync(doc.Path, LibrarySettings.IndexMaxMb, cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            // Сам разбор исключений не бросает, но вокруг него есть поток и токен — если сюда
            // все же прилетело, панель обязана сказать об этом, а не остаться на «Читаю…».
            Serilog.Log.Warning(ex, "Библиотека: документ не прочитался: {Path:l}", doc.Path);
            result = DocReadResult.Of(DocTextStatus.Failed);
        }

        if (gen != _gen) return;   // за время чтения открыли другой документ

        if (result.Status != DocTextStatus.Ok)
        {
            ShowState(StateText(result.Status, doc));
            return;
        }

        BuildLines(result);
        ShowState("");
        LinesList.ItemsSource = _lines;
        Rehighlight();
    }

    /// <summary>
    /// Что показать вместо текста. Формулировки разные не для красоты: скан не чинится ничем,
    /// чужой формат — тоже, а «тяжелее порога» снимается настройкой, и человек должен понимать,
    /// куда ему идти.
    /// </summary>
    private static string StateText(DocTextStatus status, DocRecord doc) => status switch
    {
        DocTextStatus.NoText =>
            "Текста в документе нет — это скан без текстового слоя. Подсвечивать нечего; " +
            "сам документ открывается кнопкой «Открыть».",
        DocTextStatus.Unsupported =>
            $"Текст такого формата не читается. Читаются {DocTextExtractor.SupportedText}.",
        DocTextStatus.TooBig =>
            $"Документ тяжелее порога чтения ({LibrarySettings.IndexMaxMb} МБ). " +
            "Порог меняется в настройках библиотеки.",
        _ => File.Exists(doc.Path)
            ? "Документ не прочитался: файл занят или поврежден."
            : "Файла нет на диске — возможно, его переместили или удалили.",
    };

    private void ShowState(string text)
    {
        StateLabel.Text      = text;
        StateLabel.IsVisible = text.Length > 0;
        LinesList.IsVisible  = text.Length == 0;
    }

    /// <summary>
    /// Раскладка документа на строки списка. Подпись куска («Страница 4», «Лист „Смета“»)
    /// становится отдельной строкой: так она видна на своем месте при прокрутке и не мешает
    /// поиску — в самом документе ее нет, и искать по ней нечестно.
    /// </summary>
    private void BuildLines(DocReadResult result)
    {
        _lines.Clear();
        foreach (var part in result.Parts)
        {
            if (part.Label.Length > 0) _lines.Add(new ReaderLine { Text = part.Label, IsLabel = true });
            foreach (var line in part.Text.Split('\n')) _lines.Add(new ReaderLine { Text = line });
        }
    }

    // ── Подсветка и переходы ──────────────────────────────────────────────────

    /// <summary>
    /// Пересчитать совпадения по всему документу. Правила ровно те же, по которым ищет раздел
    /// (<see cref="DocHighlight"/>), — иначе получилось бы «поиск нашел, а панель не подсветила».
    /// </summary>
    private void Rehighlight()
    {
        _hits.Clear();
        var tokens = DocHighlight.Tokens(FindBox.Text);

        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            var was  = line.Hits.Count;
            line.Hits.Clear();

            if (!line.IsLabel && tokens.Count > 0)
                foreach (var hit in DocHighlight.Find(line.Text, tokens))
                {
                    line.Hits.Add((hit.Start, hit.Length, _hits.Count));
                    _hits.Add((i, hit.Start, hit.Length));
                }

            // Перерисовываем только те строки, где подсветка была или появилась.
            if (was > 0 || line.Hits.Count > 0) line.Refresh();
        }

        _current = _hits.Count > 0 ? 0 : -1;
        ApplyCurrent(scroll: true);
    }

    private void Step(int delta)
    {
        if (_hits.Count == 0) return;

        var previous = _current;
        _current = ((_current + delta) % _hits.Count + _hits.Count) % _hits.Count;
        ApplyCurrent(scroll: true, previous);
    }

    private void ApplyCurrent(bool scroll, int previous = -1)
    {
        UpdateCounter();

        // Перерисовываем ДВЕ строки, а не весь список: у документа в миллион знаков перерисовка
        // всего по каждому шагу «дальше» была бы видна глазом.
        if (previous >= 0 && previous < _hits.Count) Touch(_hits[previous].Line);
        if (_current  >= 0) Touch(_hits[_current].Line);

        if (!scroll || _current < 0) return;
        var target = _hits[_current].Line;
        if (target >= 0 && target < _lines.Count) LinesList.ScrollIntoView(target);
    }

    private void Touch(int lineIndex)
    {
        if (lineIndex >= 0 && lineIndex < _lines.Count) _lines[lineIndex].Refresh();
    }

    private void UpdateCounter()
    {
        var asked = DocHighlight.Tokens(FindBox.Text).Count > 0;
        CounterLabel.Text = _hits.Count > 0
            ? $"{_current + 1} из {_hits.Count}"
            : asked && _lines.Count > 0 ? "нет" : "";

        PrevBtn.IsEnabled = _hits.Count > 0;
        NextBtn.IsEnabled = _hits.Count > 0;
    }

    /// <summary>Сквозной номер текущего совпадения — строка спрашивает его при отрисовке.</summary>
    private int CurrentHit => _current;

    // ── Отрисовка строки ──────────────────────────────────────────────────────

    /// <summary>
    /// Собрать строку: обычные куски как есть, совпадения с подсветкой, текущее — цветом
    /// акцента. Inlines привязкой не выражаются, поэтому строка рисуется кодом — тот же прием,
    /// что у сниппета карточки (<c>DocCardVm.RenderSnippet</c>).
    /// </summary>
    private void Paint(TextBlock target, ReaderLine line)
    {
        target.Inlines?.Clear();

        if (line.IsLabel)
        {
            // Подпись части — заголовок куска, а не строка текста: серая, помельче, с воздухом
            // сверху.
            target.Text       = line.Text;
            target.Foreground = Brush("BrushSecondaryText", "#8E8E93");
            target.FontWeight = FontWeight.SemiBold;
            target.FontSize   = Size("FontSizeSmall", 12);
            target.Margin     = new Thickness(0, 14, 0, 4);
            return;
        }

        target.Foreground = Brush("BrushPrimaryText", "#1C1C1E");
        target.FontWeight = FontWeight.Normal;
        target.FontSize   = Size("FontSizeMedium", 14);
        target.Margin     = new Thickness(0, 0, 0, 2);

        if (line.Hits.Count == 0) { target.Text = line.Text; return; }

        // Есть подсветка — строка собирается кусками. Text при этом обязан быть пустым: иначе
        // Avalonia нарисует и его, и Inlines.
        target.Text = null;

        var plain   = Brush("BrushHighlight", "#FFF0B3");
        var current = Brush("BrushAccent",    "#007AFF");
        var normal  = target.Foreground;

        var pos = 0;
        foreach (var (start, length, id) in line.Hits)
        {
            if (start > pos) target.Inlines?.Add(new Run(line.Text[pos..start]));

            var now = id == CurrentHit;
            target.Inlines?.Add(new Run(line.Text.Substring(start, length))
            {
                Background = now ? current : plain,
                Foreground = now ? Brushes.White : normal,
                FontWeight = FontWeight.SemiBold,
            });
            pos = start + length;
        }
        if (pos < line.Text.Length) target.Inlines?.Add(new Run(line.Text[pos..]));
    }

    private IBrush Brush(string key, string fallback) =>
        this.TryFindResource(key, out var value) && value is IBrush brush
            ? brush
            : new SolidColorBrush(Color.Parse(fallback));

    private double Size(string key, double fallback) =>
        this.TryFindResource(key, out var value) && value is double size ? size : fallback;

    /// <summary>
    /// Строка документа. Подсветка не выражается привязкой, поэтому строка сообщает о своей
    /// перерисовке событием — как <c>DocCardVm.SnippetChanged</c> у карточки ленты.
    /// </summary>
    private sealed class ReaderLine
    {
        public string Text { get; init; } = "";

        /// <summary>Подпись части документа («Страница 4»), а не его текст.</summary>
        public bool IsLabel { get; init; }

        /// <summary>Совпадения в этой строке: место и сквозной номер по документу.</summary>
        public List<(int Start, int Length, int Id)> Hits { get; } = [];

        public event Action? Changed;
        public void Refresh() => Changed?.Invoke();
    }

    /// <summary>
    /// Контейнер строки. Отдельный тип, а не лямбда в шаблоне, ровно из-за подписки: список
    /// виртуализован, контейнеры приезжают и уезжают, и подписываться-отписываться надо по
    /// привязке и по уходу из дерева. Лямбда с локальной функцией этого не умеет — снять
    /// такую подписку нечем, и строки копили бы обработчиков на каждой прокрутке.
    ///
    /// <see cref="SelectableTextBlock"/>, а не обычный <c>TextBlock</c>: документ пришли ЧИТАТЬ,
    /// и цитату из него обязано быть можно выделить и скопировать. Выделение работает внутри
    /// строки — через границу строк оно не тянется, потому что строки это разные контролы
    /// виртуализованного списка, а не один текст.
    /// </summary>
    private sealed class ReaderLineView : SelectableTextBlock
    {
        private readonly DocReaderView _owner;
        private ReaderLine? _line;

        public ReaderLineView(DocReaderView owner)
        {
            _owner            = owner;
            TextWrapping      = TextWrapping.Wrap;
            HorizontalAlignment = HorizontalAlignment.Stretch;
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            Unhook();
            _line = DataContext as ReaderLine;
            Hook();
            Repaint();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Hook();
            Repaint();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            Unhook();
        }

        private void Hook()
        {
            if (_line is null) return;
            _line.Changed -= Repaint;   // двойной подписки не бывает: снимаем перед тем, как ставить
            _line.Changed += Repaint;
        }

        private void Unhook()
        {
            if (_line is not null) _line.Changed -= Repaint;
        }

        private void Repaint()
        {
            if (_line is not null) _owner.Paint(this, _line);
        }
    }
}
