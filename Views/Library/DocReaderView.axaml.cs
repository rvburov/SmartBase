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

    /// <summary>Режим показа: текстом или настоящими страницами документа.</summary>
    private enum ReadMode { Text, Pages }

    private ReadMode _mode = ReadMode.Text;

    /// <summary>Строки документа с подсветкой — список рисует по ним.</summary>
    private readonly List<ReaderLine> _lines = [];

    /// <summary>Совпадения по всему документу: номер строки и место в ней.</summary>
    private readonly List<(int Line, int Start, int Length)> _hits = [];

    /// <summary>Страницы документа — только у PDF; у остальных форматов режим недоступен.</summary>
    private readonly List<PageVm> _pages = [];

    /// <summary>Совпадения в режиме страниц: номер страницы и номер слова на ней.</summary>
    private readonly List<(int Page, int Index)> _pageHits = [];

    /// <summary>Открытый документ для отрисовки страниц; null — формат без настоящего вида.</summary>
    private DocPageSource? _source;

    /// <summary>
    /// Ширина страницы в пикселях, под которую нарисованы картинки. Панель тянут за ручку, и
    /// страница, нарисованная под прежнюю ширину, растянулась бы и расплылась.
    /// </summary>
    private int _pageWidth;

    /// <summary>
    /// Страницы, чьи картинки сейчас в памяти, в порядке обращения. Страница A4 при ширине
    /// панели — это мегабайты, и держать их все у документа на три сотни страниц нельзя.
    /// </summary>
    private readonly List<int> _rendered = [];

    private const int RenderedCap = 10;

    private int _current = -1;

    /// <summary>Поколение чтения: ответ старого разбора не должен перерисовать новый документ.</summary>
    private int _gen;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Дебаунс правки строки поиска. Пересчет подсветки идет по всему тексту документа, и на
    /// четырех миллионах знаков он заметен — на каждое нажатие его гонять нельзя.
    /// </summary>
    private readonly DispatcherTimer _findDebounce = new() { Interval = TimeSpan.FromMilliseconds(200) };

    /// <summary>Дребезг смены ширины панели: страницы перерисовываются под новую ширину.</summary>
    private readonly DispatcherTimer _widthDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };

    public DocReaderView()
    {
        InitializeComponent();

        LinesList.ItemTemplate = new FuncDataTemplate<ReaderLine>((_, _) => new ReaderLineView(this), supportsRecycling: false);
        LinesList.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<ScrollViewer>("PART_ScrollViewer") is { } sv) ScrollBarLink.SetSource(LinesBar, sv);
        };

        PagesList.ItemTemplate = new FuncDataTemplate<PageVm>((_, _) => new PageItemView(this), supportsRecycling: false);
        PagesList.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<ScrollViewer>("PART_ScrollViewer") is { } sv) ScrollBarLink.SetSource(PagesBar, sv);
        };

        // Переключатель режима ведет себя как пара радиокнопок: выбран ровно один, и нажатие на
        // уже выбранный ничего не меняет — иначе панель осталась бы без режима вовсе.
        TextModeBtn.IsCheckedChanged  += (_, _) => { if (TextModeBtn.IsChecked  == true) SetMode(ReadMode.Text);  else if (_mode == ReadMode.Text)  TextModeBtn.IsChecked  = true; };
        PagesModeBtn.IsCheckedChanged += (_, _) => { if (PagesModeBtn.IsChecked == true) SetMode(ReadMode.Pages); else if (_mode == ReadMode.Pages) PagesModeBtn.IsChecked = true; };

        // Сменилась ширина панели — страницы перерисовываются под новую. С дребезгом: ручку
        // тянут плавно, и рисовать страницу на каждый пиксель движения незачем.
        PagesList.PropertyChanged += (_, e) =>
        {
            if (e.Property != BoundsProperty || _mode != ReadMode.Pages) return;
            _widthDebounce.Stop();
            _widthDebounce.Start();
        };
        _widthDebounce.Tick += (_, _) => { _widthDebounce.Stop(); ReflowPages(); };

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
        _widthDebounce.Stop();

        Reset();
        // Каждый документ открывается ТЕКСТОМ, даже если прошлый читали страницами: человек
        // нажал «Прочитать» после поиска, и ему нужно найденное слово, а не обложка.
        SetMode(ReadMode.Text, force: true);
        ShowState("Читаю документ…");
        _ = LoadAsync(doc, ++_gen);
        _ = OpenPagesAsync(doc, _gen);
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
        _pages.Clear();
        _pageHits.Clear();
        _rendered.Clear();
        _current  = -1;
        _pageWidth = 0;

        // Документ PDFium отпускаем сразу: он держит открытый файл, а файл могут захотеть
        // переименовать или удалить прямо сейчас.
        _source?.Dispose();
        _source = null;

        LinesList.ItemsSource = null;
        PagesList.ItemsSource = null;
        PagesModeBtn.IsEnabled = false;
        ToolTip.SetTip(PagesModeBtn, null);
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
        LinesList.IsVisible  = text.Length == 0 && _mode == ReadMode.Text;
        PagesList.IsVisible  = text.Length == 0 && _mode == ReadMode.Pages;
    }

    // ── Режим «как в документе» ───────────────────────────────────────────────

    /// <summary>
    /// Открыть документ для показа страницами. Только PDF: страницу надо чем-то нарисовать и
    /// где-то взять места слов — для DOCX и XLSX ни того, ни другого у нас нет.
    /// </summary>
    private async Task OpenPagesAsync(DocRecord doc, int gen)
    {
        if (DocTextExtractor.ExtensionOf(doc.Path) != "pdf")
        {
            ToolTip.SetTip(PagesModeBtn, "Страницами пока показывается только PDF");
            return;
        }

        var source = await Task.Run(() => DocPageSource.Open(doc.Path));
        if (gen != _gen || source is null)
        {
            source?.Dispose();
            if (gen == _gen) ToolTip.SetTip(PagesModeBtn, "Страницы этого документа не открылись");
            return;
        }

        _source = source;
        _pages.Clear();
        for (var i = 0; i < source.PageCount; i++) _pages.Add(new PageVm(i, source.Size(i)));

        PagesList.ItemsSource  = _pages;
        PagesModeBtn.IsEnabled = true;
        ToolTip.SetTip(PagesModeBtn, null);

        // Совпадения на страницах считаются сразу, а не при переключении режима: счетчик
        // обязан говорить правду в ту же секунду, когда человек нажмет «Как в документе».
        _ = ScanPagesAsync(gen);
    }

    private void SetMode(ReadMode mode, bool force = false)
    {
        if (!force && mode == _mode) return;
        if (mode == ReadMode.Pages && _source is null) { TextModeBtn.IsChecked = true; return; }

        _mode = mode;
        TextModeBtn.IsChecked  = mode == ReadMode.Text;
        PagesModeBtn.IsChecked = mode == ReadMode.Pages;

        // Состояние («скан», «файла нет») относится к ТЕКСТУ. У скана страницы есть и
        // показывать их можно — на них просто нечего подсвечивать.
        ShowState(mode == ReadMode.Pages ? "" : StateLabel.Text ?? "");

        _current = HitCount > 0 ? 0 : -1;
        if (mode == ReadMode.Pages) ReflowPages();
        ApplyCurrent(scroll: true);
    }

    /// <summary>
    /// Пересчитать ширину страницы под текущую ширину панели и перерисовать то, что видно.
    /// Картинки прежней ширины выбрасываются: растянутая страница расплывается, а
    /// сжатая — впустую держит память.
    /// </summary>
    private void ReflowPages()
    {
        if (_source is null || _pages.Count == 0) return;

        var width = (int)Math.Round(Math.Max(0, PagesList.Bounds.Width - 32) *
                                    (VisualRoot?.RenderScaling ?? 1));
        if (width < 120) return;

        // Мелкое изменение ширины перерисовкой не отрабатываем: разница в пару процентов не
        // видна, а перерисовка сотни страниц заметна.
        if (_pageWidth > 0 && Math.Abs(width - _pageWidth) < _pageWidth * 0.1) return;

        _pageWidth = width;
        foreach (var page in _rendered.ToList()) _pages[page].SetBitmap(null);
        _rendered.Clear();
        foreach (var page in _pages) page.Refresh();
    }

    /// <summary>
    /// Попросить картинку страницы. Зовется самой страницей из отрисовки, когда та появляется
    /// на экране: рисовать весь документ вперед незачем, а у трехсот страниц и невозможно.
    ///
    /// Работа ОТКЛАДЫВАЕТСЯ в очередь, и это не украшение: зовут нас изнутри прохода отрисовки,
    /// а любое обращение к раскладке оттуда (а подготовка ширины ее трогает) роняет Avalonia
    /// с «Visual was invalidated during the render pass».
    /// </summary>
    private void RequestPage(PageVm page)
    {
        if (_source is null || page.Bitmap is not null || page.Loading) return;

        // Признак ставим сразу: отрисовка успеет позвать нас еще раз, пока задание ждет очереди.
        page.Loading = true;
        Dispatcher.UIThread.Post(() => _ = RenderPageAsync(page), DispatcherPriority.Background);
    }

    private async Task RenderPageAsync(PageVm page)
    {
        if (_source is not { } source) { page.Loading = false; return; }
        if (_pageWidth <= 0) { ReflowPages(); if (_pageWidth <= 0) { page.Loading = false; return; } }

        var gen   = _gen;
        var width = _pageWidth;

        try
        {
            var png = await Task.Run(() => source.Render(page.Index, width));
            if (gen != _gen || png is null) return;

            using var stream = new MemoryStream(png);
            page.SetBitmap(new Avalonia.Media.Imaging.Bitmap(stream));
            Remember(page.Index);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Библиотека: страница {Page} не нарисовалась", page.Index + 1);
        }
        finally
        {
            page.Loading = false;
        }
    }

    /// <summary>Держим в памяти только последние страницы — остальные рисуются заново.</summary>
    private void Remember(int page)
    {
        _rendered.Remove(page);
        _rendered.Add(page);
        while (_rendered.Count > RenderedCap)
        {
            var oldest = _rendered[0];
            _rendered.RemoveAt(0);
            if (oldest >= 0 && oldest < _pages.Count) _pages[oldest].SetBitmap(null);
        }
    }

    /// <summary>
    /// Обойти страницы и найти на них слова запроса. Идет в фоне и страница за страницей:
    /// разбор трехсот страниц — это секунды, и держать ради них панель нельзя. Счетчик растет
    /// по ходу дела, поэтому видно, что работа идет.
    /// </summary>
    private async Task ScanPagesAsync(int gen)
    {
        if (_source is not { } source) return;

        var tokens = DocHighlight.Tokens(FindBox.Text);
        _pageHits.Clear();
        if (tokens.Count == 0) { UpdateCounter(); return; }

        for (var i = 0; i < _pages.Count; i++)
        {
            if (gen != _gen) return;

            var index = i;
            var found = await Task.Run(() =>
            {
                var hits = new List<int>();
                var words = source.Words(index);
                for (var w = 0; w < words.Count; w++)
                    if (DocHighlight.Find(words[w].Text, tokens).Count > 0) hits.Add(w);
                return (Words: words, Hits: hits);
            });

            if (gen != _gen) return;

            var page = _pages[index];
            page.SetWords(found.Words);
            page.Hits.Clear();
            foreach (var w in found.Hits)
            {
                page.Hits.Add((w, _pageHits.Count));
                _pageHits.Add((index, w));
            }

            if (found.Hits.Count > 0)
            {
                page.Refresh();
                if (_mode == ReadMode.Pages)
                {
                    // Первое найденное совпадение сразу становится текущим — как в режиме текста.
                    if (_current < 0) { _current = 0; ApplyCurrent(scroll: true); }
                    else UpdateCounter();
                }
            }
        }

        if (gen == _gen) UpdateCounter();
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

        // Страницы ищут свое: у них совпадения считаются по словам с их местами, и списка
        // строк для этого недостаточно.
        if (_source is not null) _ = ScanPagesAsync(_gen);
    }

    /// <summary>Сколько совпадений в ТЕКУЩЕМ режиме — по ним ходят стрелки и счетчик.</summary>
    private int HitCount => _mode == ReadMode.Pages ? _pageHits.Count : _hits.Count;

    private void Step(int delta)
    {
        var count = HitCount;
        if (count == 0) return;

        var previous = _current;
        _current = ((_current + delta) % count + count) % count;
        ApplyCurrent(scroll: true, previous);
    }

    private void ApplyCurrent(bool scroll, int previous = -1)
    {
        UpdateCounter();

        // Перерисовываем ДВЕ строки (или две страницы), а не весь список: у документа в миллион
        // знаков перерисовка всего по каждому шагу «дальше» была бы видна глазом.
        if (_mode == ReadMode.Pages)
        {
            if (previous >= 0 && previous < _pageHits.Count) TouchPage(_pageHits[previous].Page);
            if (_current  >= 0 && _current  < _pageHits.Count) TouchPage(_pageHits[_current].Page);

            if (!scroll || _current < 0 || _current >= _pageHits.Count) return;
            PagesList.ScrollIntoView(_pageHits[_current].Page);
            return;
        }

        if (previous >= 0 && previous < _hits.Count) Touch(_hits[previous].Line);
        if (_current  >= 0 && _current  < _hits.Count) Touch(_hits[_current].Line);

        if (!scroll || _current < 0 || _current >= _hits.Count) return;
        var target = _hits[_current].Line;
        if (target >= 0 && target < _lines.Count) LinesList.ScrollIntoView(target);
    }

    private void Touch(int lineIndex)
    {
        if (lineIndex >= 0 && lineIndex < _lines.Count) _lines[lineIndex].Refresh();
    }

    private void TouchPage(int pageIndex)
    {
        if (pageIndex >= 0 && pageIndex < _pages.Count) _pages[pageIndex].Refresh();
    }

    private void UpdateCounter()
    {
        var asked = DocHighlight.Tokens(FindBox.Text).Count > 0;
        var count = HitCount;
        var ready = _mode == ReadMode.Pages ? _pages.Count > 0 : _lines.Count > 0;

        CounterLabel.Text = count > 0
            ? $"{_current + 1} из {count}"
            : asked && ready ? "нет" : "";

        PrevBtn.IsEnabled = count > 0;
        NextBtn.IsEnabled = count > 0;
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
    /// Страница документа: размер в пунктах, нарисованная картинка и совпадения на ней.
    /// Живет столько же, сколько открытый документ; картинку у нее отбирают, когда страница
    /// уезжает далеко от экрана.
    /// </summary>
    private sealed class PageVm(int index, DocPageSize size)
    {
        public int Index { get; } = index;
        public DocPageSize Size { get; } = size;

        public Avalonia.Media.Imaging.Bitmap? Bitmap { get; private set; }

        /// <summary>Страница уже рисуется — второй раз просить не надо.</summary>
        public bool Loading { get; set; }

        /// <summary>Слова страницы с их местами; пусто, пока страницу не разбирали.</summary>
        public IReadOnlyList<DocWord> Words { get; private set; } = [];

        /// <summary>Совпадения: номер слова на странице и сквозной номер по документу.</summary>
        public List<(int Word, int Id)> Hits { get; } = [];

        public event Action? Changed;

        public void SetBitmap(Avalonia.Media.Imaging.Bitmap? bitmap)
        {
            // Старую картинку отпускаем явно: это несколько мегабайт неуправляемой памяти, и
            // ждать сборщика ради них незачем.
            var old = Bitmap;
            Bitmap  = bitmap;
            if (!ReferenceEquals(old, bitmap)) old?.Dispose();
            Refresh();
        }

        public void SetWords(IReadOnlyList<DocWord> words) => Words = words;

        public void Refresh() => Changed?.Invoke();
    }

    /// <summary>
    /// Страница на экране. Рисует себя целиком сама — картинку и прямоугольники подсветки
    /// поверх нее.
    ///
    /// Своя отрисовка, а не Image с Canvas и фигурами: места слов приходят в пунктах страницы,
    /// и переводить их в координаты контрола все равно приходится вручную (ось Y у PDF идет
    /// снизу вверх). С готовыми фигурами к этому добавилась бы раскладка сотни прямоугольников
    /// на каждую перерисовку — а их надо пересчитывать при любой смене ширины панели.
    /// </summary>
    private sealed class PageItemView : Control
    {
        private readonly DocReaderView _owner;
        private PageVm? _page;

        public PageItemView(DocReaderView owner) => _owner = owner;

        protected override Size MeasureOverride(Size availableSize)
        {
            // Высота известна ДО того, как страница нарисована: она считается по пропорции
            // страницы. Иначе список менял бы высоту под каждой подъехавшей картинкой, а
            // ползунок «дышал» бы на всю длину документа.
            var width = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
            var size  = _page?.Size ?? new DocPageSize(595, 842);
            var ratio = size.Width > 0 ? size.Height / size.Width : 1.414;
            return new Size(width, Math.Max(1, width * ratio));
        }

        public override void Render(DrawingContext context)
        {
            var area = new Rect(Bounds.Size);
            if (_page is not { } page) return;

            // Лист всегда белый, даже пока картинки нет: серая дыра в ленте страниц читалась бы
            // как «тут ничего нет», а не «сейчас нарисуется».
            context.FillRectangle(Brushes.White, area);

            if (page.Bitmap is { } bitmap)
                context.DrawImage(bitmap, new Rect(bitmap.Size), area);
            else
                _owner.RequestPage(page);

            var border = _owner.Brush("BrushSeparator", "#D1D1D6");
            context.DrawRectangle(null, new Pen(border, 1), area);

            if (page.Hits.Count == 0 || page.Words.Count == 0) return;

            var scale   = DocPageGeometry.ScaleFor(area.Width, page.Size.Width);
            var plain   = _owner.Brush("BrushHighlight", "#FFF0B3");
            var current = _owner.Brush("BrushAccent",    "#007AFF");

            foreach (var (word, id) in page.Hits)
            {
                if (word < 0 || word >= page.Words.Count) continue;

                var box  = DocPageGeometry.ToScreen(page.Words[word].Box, page.Size.Height, scale, padding: 0.5);
                var rect = new Rect(box.X, box.Y, box.Width, box.Height);
                var now  = id == _owner.CurrentHit;

                // Подсветка ПРОЗРАЧНАЯ: под ней лежит сама страница, и закрасить слово наглухо
                // значило бы спрятать то, что человек искал.
                context.FillRectangle(new SolidColorBrush(((ISolidColorBrush)(now ? current : plain)).Color,
                                                          now ? 0.45 : 0.40), rect, 2);
            }
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            Unhook();
            _page = DataContext as PageVm;
            Hook();
            InvalidateMeasure();
            InvalidateVisual();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Hook();
            InvalidateVisual();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            Unhook();
        }

        private void Hook()
        {
            if (_page is null) return;
            _page.Changed -= Repaint;
            _page.Changed += Repaint;
        }

        private void Unhook()
        {
            if (_page is not null) _page.Changed -= Repaint;
        }

        private void Repaint()
        {
            InvalidateMeasure();
            InvalidateVisual();
        }
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
