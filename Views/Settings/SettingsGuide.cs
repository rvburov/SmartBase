using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Serilog;
using SmartBase.Controls.Widgets;
using SmartBase.Settings;
using SmartBase.Utils;

namespace SmartBase.Views.Settings;

/// <summary>
/// Раздел настроек «Инструкция пользователя»: показ документов <c>docs/user/*.md</c>
/// (вшиты в сборку как AvaloniaResource) и панель «Содержание» справа — как панель
/// «Детали» в «Диагностике».
///
/// Почему НЕ один виджет на весь текст: узнать положение заголовка внутри собранного
/// markdown можно только поиском по визуальному дереву. Поэтому документ режется по
/// заголовкам второго уровня, каждый кусок рендерится своим контролом в общий
/// <see cref="StackPanel"/>, а прокруткой заведует НАШ <see cref="ScrollViewer"/> —
/// положение любой секции известно точно (<c>TranslatePoint</c>), и оглавление умеет к
/// ней прокручивать.
///
/// Порядок документов — <see cref="GuideRegistry"/>; подписи в оглавлении берутся из
/// самих заголовков, руками не дублируются.
///
/// Текст секций ставится порциями (<see cref="RenderGuideChunk"/>): оглавление и пустые
/// секции появляются сразу, первые секции получают текст синхронно, остальные — фоновыми
/// заданиями диспетчера по бюджету времени. Вся инструкция разом держала UI-поток ~3 с при
/// первом заходе в раздел (сборка ~1,2 с и столько же раскладка текста). Переход к секции,
/// до которой фон еще не дошел, сначала дособирает текст до нее.
/// </summary>
public partial class SettingsView
{
    // Пункт оглавления: строка панели «Содержание» + секция текста, к которой она ведет, и ее
    // номер в очереди сборки (_guideQueue).
    private sealed record GuideAnchor(Border Row, Control Section, int Order);

    // Секции в порядке показа и их markdown: текст ставится порциями, _guideNext — первая
    // еще не собранная.
    private readonly List<(AppMarkdown View, string Markdown)> _guideQueue = [];
    private int _guideNext;

    // Бюджет порций: первая — пока человек ждет открытия раздела, фоновые — внутри кадра.
    private static readonly TimeSpan GuideFirstBudget = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan GuideChunkBudget = TimeSpan.FromMilliseconds(12);

    private readonly List<GuideAnchor> _guideAnchors = [];

    // Имя документа → его первый пункт оглавления. По нему прыгают вики-ссылки [[имя]] из текста.
    private readonly Dictionary<string, GuideAnchor> _guideBySlug = new(StringComparer.OrdinalIgnoreCase);

    private bool _guideBuilt;

    // Кеш Y-координат секций внутри GuideHost — чтобы не мерить все дерево на каждый
    // тик прокрутки. Сбрасывается, когда меняется раскладка (зум, размер шрифта, ширина).
    private double[]? _guideOffsets;
    private GuideAnchor? _guideActive;

    // Ширина панели «Содержание». В отличие от «Деталей» журнала панель открыта все время,
    // пока выбран раздел, поэтому ширина помнится между запусками (AppSettings).
    private double _guideTocWidth = AppSettings.GuideTocWidthDefault;

    // ── Сборка раздела ──────────────────────────────────────────────────────────

    /// <summary>Собрать текст и оглавление (один раз за сеанс — документы не меняются).</summary>
    private void BuildGuide()
    {
        if (_guideBuilt) return;
        _guideBuilt = true;

        // Проход 1: читаем документы. Заголовки нужны заранее — их подставляют вместо
        // wiki-ссылок [[имя]], а ссылка может смотреть на документ, который идет ниже.
        var pages = new List<(GuidePage Page, string Text, string Title)>();
        foreach (var page in GuideRegistry.All)
        {
            var text = ReadGuidePage(page);
            if (text is not null) pages.Add((page, text, FirstHeading(text) ?? page.File));
        }
        var titles = pages.ToDictionary(p => Slug(p.Page), p => p.Title);

        // Проход 2: режем на секции и рендерим. Заголовки нумеруются сквозь весь текст
        // («2. Базы, группы…», «2.3. Боковая панель») — по справочнику проще ориентироваться
        // и ссылаться на место. Номера считаются из порядка реестра, а не пишутся руками
        // в markdown: иначе перестановка документа заставляла бы перенумеровывать все.
        var docNo = 0;
        foreach (var (page, text, title) in pages)
        {
            docNo++;
            var sectionNo = 0;
            var first = true;

            foreach (var (heading, body) in SplitBySections(text))
            {
                // Линия отбивает ДОКУМЕНТ — верхний уровень оглавления. Разделы внутри
                // документа (подпункты) линиями не режем: они и так отбиты заголовком,
                // а сетка из линий на каждый подраздел дробит текст в кашу.
                if (first && GuideHost.Children.Count > 0) GuideHost.Children.Add(SectionSeparator());

                var number = first ? $"{docNo}." : $"{docNo}.{++sectionNo}.";
                var body2  = NumberHeading(ResolveWikiLinks(body, titles), number);

                var section = GuideSection(body2);
                GuideHost.Children.Add(section);

                // Первая секция документа — его заголовок в оглавлении, остальные — подпункты.
                var anchor = AddTocRow($"{number} {(first ? title : heading ?? title)}", sub: !first, section);
                if (first) _guideBySlug[Slug(page)] = anchor;
                first = false;
            }
        }

        if (GuideHost.Children.Count == 0)
            GuideHost.Children.Add(new TextBlock
            {
                Text = "Не удалось прочитать инструкцию. Подробности — в разделе «Диагностика».",
                Foreground = Brush("BrushSecondaryText"),
                TextWrapping = TextWrapping.Wrap,
            });

        GuidePanel.ScrollChanged += (_, _) => UpdateGuideActive();
        // Раскладка изменилась (зум, размер шрифта, ширина панели) — прежние координаты
        // секций больше не годятся.
        GuideHost.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) _guideOffsets = null;
        };

        // Первая порция — сразу, остальное дособирается фоном.
        RenderGuideChunk(GuideFirstBudget);

        // Пока раскладки нет, координаты секций не измерить — подсвечиваем первый пункт,
        // он и так соответствует началу текста; дальше подсветку ведет прокрутка.
        if (_guideAnchors.Count > 0) SelectGuideRow(_guideAnchors[0]);
        Dispatcher.UIThread.Post(UpdateGuideActive, DispatcherPriority.Loaded);
    }

    /// <summary>Поставить текст следующим секциям, пока не вышел <paramref name="budget"/> (хотя
    /// бы одной), и запланировать следующую порцию фоном.</summary>
    private void RenderGuideChunk(TimeSpan budget)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var first = true;
        while (_guideNext < _guideQueue.Count && (first || sw.Elapsed < budget))
        {
            var (view, markdown) = _guideQueue[_guideNext++];
            view.Markdown = markdown;
            first = false;
        }
        _guideOffsets = null;
        if (_guideNext < _guideQueue.Count)
            Dispatcher.UIThread.Post(() => RenderGuideChunk(GuideChunkBudget), DispatcherPriority.Background);
    }

    /// <summary>Дособрать текст до секции <paramref name="order"/> включительно и разложить: без
    /// этого переход к ней считал бы положение по пустым секциям выше.</summary>
    private void RenderGuideUpTo(int order)
    {
        if (_guideNext > order) return;
        while (_guideNext <= order && _guideNext < _guideQueue.Count)
        {
            var (view, markdown) = _guideQueue[_guideNext++];
            view.Markdown = markdown;
        }
        _guideOffsets = null;
        GuidePanel.UpdateLayout();
    }

    /// <summary>Текст документа из вшитого ресурса; null — файла нет или он не читается.</summary>
    private static string? ReadGuidePage(GuidePage page)
    {
        try
        {
            using var stream = AssetLoader.Open(GuideRegistry.UriOf(page));
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Инструкция: не удалось прочитать {File:l}", page.File);
            return null;
        }
    }

    /// <summary>Линия между блоками — общий стиль разделителей приложения (Controls.axaml).</summary>
    private static Border SectionSeparator() =>
        new() { Classes = { "separator", "h" }, Margin = new Thickness(0, SectionGap, 0, SectionGap) };

    private const double SectionGap = 18;

    // Секция без текста: markdown встанет порцией (RenderGuideChunk).
    private Control GuideSection(string markdown)
    {
        // Своей прокрутки у виджета нет и не нужно: внутри вертикального StackPanel высота
        // не ограничена, поэтому секция разворачивается на весь текст, а прокруткой заведует
        // общий ScrollViewer раздела.
        var view = new AppMarkdown
        {
            SelectionEnabled = true,   // текст инструкции можно выделить и скопировать
        };
        view.LinkClicked += OnGuideLink;
        _guideQueue.Add((view, markdown));
        return view;
    }

    /// <summary>
    /// Ссылка внутри инструкции. Свои (<c>GuideScheme</c>) ведут на другой документ справочника
    /// и обрабатываются здесь — переход равнозначен нажатию строки в «Содержании». Все прочие
    /// виджет откроет системой, и мешать ему незачем.
    /// </summary>
    private void OnGuideLink(object? sender, LinkEventArgs e)
    {
        if (!e.Url.StartsWith(GuideScheme, StringComparison.Ordinal)) return;

        e.Handled = true;
        if (_guideBySlug.TryGetValue(e.Url[GuideScheme.Length..], out var anchor)) ScrollGuideTo(anchor);
    }

    /// <summary>Схема ссылок внутрь справочника. Своя, потому что адрес тут не путь и не URL —
    /// это имя документа реестра.</summary>
    private const string GuideScheme = "sb-guide:";

    // ── Разбор markdown ─────────────────────────────────────────────────────────

    /// <summary>Первый заголовок первого уровня (<c># …</c>) — подпись документа в оглавлении.</summary>
    private static string? FirstHeading(string md)
    {
        foreach (var line in EnumerateLines(md))
            if (line.StartsWith("# ", StringComparison.Ordinal))
                return line[2..].Trim();
        return null;
    }

    /// <summary>Служебная секция: перечень связей документа, нужный при чтении на GitHub.</summary>
    private const string RelatedDocsHeading = "Связанные документы";

    /// <summary>Имя документа для ссылок: файл без расширения — так на него ссылаются в тексте.</summary>
    private static string Slug(GuidePage page) => Path.GetFileNameWithoutExtension(page.File);

    /// <summary>
    /// Заменяет вики-ссылки <c>[[имя]]</c> (соглашение документации, см. docs/README.md)
    /// настоящей ссылкой на документ справочника: <c>[Календарь](sb-guide:calendar)</c>.
    /// Двойные скобки с латинским именем файла на экране читались бы мусором, а на GitHub
    /// та же строка кликается — поэтому подмена, а не показ как есть.
    ///
    /// Неизвестное имя остается простым текстом: ссылка, ведущая в никуда, хуже текста.
    /// </summary>
    private static string ResolveWikiLinks(string md, IReadOnlyDictionary<string, string> titles) =>
        WikiLinkPattern().Replace(md, m =>
        {
            var name = m.Groups[1].Value.Trim();
            return titles.TryGetValue(name, out var title) ? $"[{title}]({GuideScheme}{name})" : name;
        });

    [GeneratedRegex(@"\[\[([^\]]+)\]\]")]
    private static partial Regex WikiLinkPattern();

    /// <summary>
    /// Дописывает номер в ПЕРВЫЙ заголовок секции: <c>## Боковая панель</c> →
    /// <c>## 2.3. Боковая панель</c>. Дальше по тексту заголовки (H3 и ниже) не трогаем —
    /// нумерация до третьего уровня в справочнике только мешает.
    /// </summary>
    private static string NumberHeading(string body, string number)
    {
        var lines = body.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();
            if (!line.StartsWith('#')) continue;

            var level = line.Length - line.TrimStart('#').Length;
            lines[i] = $"{new string('#', level)} {number} {line[level..].TrimStart()}";
            break;
        }
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Режет документ на секции по заголовкам второго уровня: первая пара — (null, шапка
    /// документа), дальше — (заголовок, текст секции вместе с ним). Строки внутри блоков
    /// кода пропускаются: <c>## </c> там — это текст, а не заголовок.
    /// </summary>
    private static List<(string? Heading, string Body)> SplitBySections(string md)
    {
        var result  = new List<(string?, string)>();
        var current = new List<string>();
        string? heading = null;

        void Flush()
        {
            var body = string.Join('\n', current).Trim();
            if (body.Length > 0 && heading != RelatedDocsHeading) result.Add((heading, body));
            current.Clear();
        }

        foreach (var line in EnumerateLines(md))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                heading = line[3..].Trim();
            }
            current.Add(line);
        }
        Flush();

        return result;
    }

    // Строки документа с пропуском содержимого блоков кода (``` … ```).
    private static IEnumerable<string> EnumerateLines(string md)
    {
        var inCode = false;
        foreach (var raw in md.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) inCode = !inCode;
            yield return inCode ? "" : line;
        }
    }

    // ── Панель «Содержание» ─────────────────────────────────────────────────────

    private GuideAnchor AddTocRow(string text, bool sub, Control section)
    {
        var row = new Border
        {
            // Подпункт отбит вправо и отличается цветом (стили Border.tocrow в SettingsView.axaml).
            Padding = sub ? new Thickness(30, 5, 12, 5) : new Thickness(16, 7, 12, 7),
        };
        row.Classes.Add("tocrow");
        if (sub) row.Classes.Add("sub");

        var label = new TextBlock
        {
            Text         = text,
            TextWrapping = TextWrapping.Wrap,
        };
        // Заголовок документа — жирный, как строка-раздел; размер не задаем: глобальный
        // стиль TextBlock уже сидит на динамическом FontSizeBase (живой «Размер шрифта»).
        if (!sub && this.TryFindResource("FontWeightBold", out var weight) && weight is FontWeight fw)
            label.FontWeight = fw;

        row.Child = label;

        var anchor = new GuideAnchor(row, section, _guideQueue.Count - 1);
        row.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            ScrollGuideTo(anchor);
        };

        _guideAnchors.Add(anchor);
        GuideTocHost.Children.Add(row);
        return anchor;
    }


    private void ScrollGuideTo(GuideAnchor anchor)
    {
        RenderGuideUpTo(anchor.Order);
        if (OffsetOf(anchor.Section) is not double y) return;
        // Отматываем на отступ выше, чтобы линия блока осталась видна над заголовком.
        AnimateGuideScroll(Math.Max(0, y - SectionGap));
        SelectGuideRow(anchor);
    }

    private void SelectGuideRow(GuideAnchor? anchor)
    {
        if (ReferenceEquals(_guideActive, anchor)) return;
        _guideActive?.Row.Classes.Remove("selected");
        anchor?.Row.Classes.Add("selected");
        _guideActive = anchor;
    }

    /// <summary>Подсветить пункт секции, которая сейчас вверху экрана.</summary>
    private void UpdateGuideActive()
    {
        if (_guideAnchors.Count == 0) return;
        EnsureGuideOffsets();
        if (_guideOffsets is not { Length: > 0 } offsets) return;

        // Секция считается текущей, пока ее начало не ушло ниже верхней кромки с запасом:
        // так подсветка переключается ровно тогда, когда заголовок доезжает до верха.
        var y     = GuidePanel.Offset.Y + 24;
        var index = 0;
        for (var i = 0; i < offsets.Length; i++)
            if (offsets[i] <= y) index = i;

        SelectGuideRow(_guideAnchors[index]);
    }

    private void EnsureGuideOffsets()
    {
        if (_guideOffsets is not null) return;

        var offsets = new double[_guideAnchors.Count];
        for (var i = 0; i < _guideAnchors.Count; i++)
        {
            if (OffsetOf(_guideAnchors[i].Section) is not double y) return;   // раскладки еще нет
            offsets[i] = y;
        }
        _guideOffsets = offsets;
    }

    /// <summary>Положение секции в координатах прокрутки раздела (null — раскладки еще нет).</summary>
    private double? OffsetOf(Control section) =>
        section.TranslatePoint(new Point(0, 0), GuidePanel) is { } p
            ? GuidePanel.Offset.Y + p.Y
            : null;

    // Плавная прокрутка — тот же прием, что в календаре (ListView.AnimateScroll):
    // easeOutCubic за 300 мс, шаг таймера 16 мс.
    private void AnimateGuideScroll(double targetY)
    {
        var startY    = GuidePanel.Offset.Y;
        var startTime = DateTime.Now;
        var timer     = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var t     = Math.Min((DateTime.Now - startTime).TotalMilliseconds / 300.0, 1.0);
            var eased = 1 - Math.Pow(1 - t, 3);
            GuidePanel.Offset = new Vector(0, startY + (targetY - startY) * eased);
            if (t >= 1.0) timer.Stop();
        };
        timer.Start();
    }

    // ── Показ и скрытие панели ──────────────────────────────────────────────────

    /// <summary>Панель, разделитель и ручка лежат в корне сетки — гасим их вместе с разделом.</summary>
    private void ShowGuideToc(bool show)
    {
        if (show)
        {
            _guideTocWidth = AppSettings.LoadGuideTocWidth();
            GuideTocPanel.Width = _guideTocWidth;
            EnsureGuideGripHooked();
        }
        GuideTocPanel.IsVisible     = show;
        GuideTocDivider.IsVisible   = show;
        GuideTocResizeGrip.IsVisible = show;
    }

    // ── Ручка ширины (копия ручки «Деталей» журнала) ────────────────────────────

    private bool   _guideGripHooked;
    private bool   _guideGripDragging;
    private double _guideGripStartX;
    private double _guideGripStartWidth;

    private void EnsureGuideGripHooked()
    {
        if (_guideGripHooked) return;
        _guideGripHooked = true;
        GuideTocResizeGrip.PointerPressed  += OnGuideGripPressed;
        GuideTocResizeGrip.PointerMoved    += OnGuideGripMoved;
        GuideTocResizeGrip.PointerReleased += OnGuideGripReleased;
    }

    private void OnGuideGripPressed(object? sender, PointerPressedEventArgs e)
    {
        _guideGripDragging   = true;
        _guideGripStartX     = e.GetPosition(this).X;
        _guideGripStartWidth = _guideTocWidth;
        FreezeGuideWidth(true);
        e.Pointer.Capture((IInputElement?)sender);
        e.Handled = true;
    }

    /// <summary>
    /// Заморозить ширину текста на время перетаскивания ручки «Содержания» — причина та же,
    /// что у панели записи (<c>RecordPageView.FreezeContentWidth</c>), только острее: здесь
    /// отрендерены ВСЕ документы сразу (docs/user — около 137 КБ), а переверстка markdown
    /// стоит примерно 1,5 мс на килобайт НА КАЖДОЕ движение мыши. Пока ширина заморожена,
    /// текст не перетекает и подрезается краем прокрутки; отпустили ручку — встал по месту.
    /// </summary>
    private void FreezeGuideWidth(bool freeze) =>
        GuideHost.Width = freeze && GuideHost.Bounds.Width > 0 ? GuideHost.Bounds.Width : double.NaN;

    private void OnGuideGripMoved(object? sender, PointerEventArgs e)
    {
        if (!_guideGripDragging) return;

        // Панель справа: движение вправо ее сужает.
        var dx = e.GetPosition(this).X - _guideGripStartX;
        _guideTocWidth = Math.Clamp(_guideGripStartWidth - dx,
                                    AppSettings.GuideTocWidthMin, AppSettings.GuideTocWidthMax);
        GuideTocPanel.Width = _guideTocWidth;
    }

    private void OnGuideGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        _guideGripDragging = false;
        FreezeGuideWidth(false);   // текст встает по новой ширине за один проход
        e.Pointer.Capture(null);
        e.Handled = true;
        AppSettings.SaveGuideTocWidth((int)Math.Round(_guideTocWidth));
    }

    private IBrush? Brush(string key) => this.TryFindResource(key, out var v) ? v as IBrush : null;
}
