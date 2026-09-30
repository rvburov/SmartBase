using System.Collections;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SmartBase.Controls.Components;
using SmartBase.Core.Documents;
using SmartBase.Core.Models;
using SmartBase.Database.DocIndex;
using SmartBase.Utils;

namespace SmartBase.Views.Library;

/// <summary>
/// Лента карточек раздела «Библиотека документов».
///
/// Шаблон строки строится в C#, а не в XAML, по той же причине, что у задач: размеры и кисти
/// берутся из ресурсов и должны переживать смену масштаба шрифта, а все видимые значения идут
/// привязками к модели строки — так карточка-заглушка виртуального списка превращается в
/// документ НА МЕСТЕ, без пересборки ленты.
///
/// Лента ПЛОСКАЯ: заголовков-групп нет, коллекция и база подписаны в самой карточке.
/// </summary>
public partial class DocListView : UserControl
{
    /// <summary>Документ отдают системе — «Открыть» в карточке или пункт меню.</summary>
    public event EventHandler<DocRecord>? DocActivated;

    /// <summary>
    /// Документ просят прочитать в правой панели — «Прочитать» в карточке или двойной клик.
    /// Это ГЛАВНОЕ действие документа: чтение с подсветкой найденного, не выходя из программы.
    /// </summary>
    public event EventHandler<DocRecord>? DocReadRequested;

    /// <summary>Выделение изменилось — контроллер обновляет подвал и меню.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>
    /// Нажали «Найти» (или Enter в поле) — искать ровно этот запрос.
    ///
    /// Поиск идет ТОЛЬКО по команде. Раньше он срабатывал на каждую букву с дребезгом в
    /// четверть секунды: на библиотеке в десятки тысяч документов это запрос в кеш текста плюс
    /// по запросу в каждую базу на каждое нажатие клавиши.
    /// </summary>
    public event EventHandler<string>? SearchRequested;

    /// <summary>Запрос очистили крестиком — вернуть обычную ленту.</summary>
    public event EventHandler? SearchCleared;

    private readonly DocSelection _selection = new();
    private DocCardVm? _anchor;

    /// <summary>Что сейчас показано: виртуальный список либо готовый набор карточек.</summary>
    private IList? _items;

    public DocListView()
    {
        InitializeComponent();

        CardsHost.ItemTemplate = new FuncDataTemplate<object>((item, _) => item switch
        {
            DocCardVm card => BuildCard(card),
            _              => new Control(),
        }, supportsRecycling: false);

        SearchBox.TextChanged += (_, _) => SyncCommandRow();
        SearchBox.KeyDown     += (_, e) =>
        {
            if (e.Key is not Avalonia.Input.Key.Enter) return;
            e.Handled = true;
            Find();
        };
        FindBtn.Click  += (_, _) => Find();
        ClearBtn.Click += (_, _) =>
        {
            SearchBox.Text = "";
            SearchBox.Focus();
            SyncCommandRow();
            SearchCleared?.Invoke(this, EventArgs.Empty);
        };
        StatusAction.Tapped += (_, e) => { e.Handled = true; _statusAction?.Invoke(); };

        SyncCommandRow();

        // Размер шрифта меняется в «Оформлении» на лету — лента обязана перестроиться, иначе
        // старые карточки остались бы со старыми метриками.
        FontScale.Changed += ApplyToList;
    }

    /// <summary>Выделенные документы — их ключи переживают перезагрузку ленты.</summary>
    public DocSelection Selection => _selection;

    /// <summary>
    /// Карточки, лежащие в памяти целиком (режим малого раздела). У виртуального списка
    /// намеренно пусто: перебрать его значило бы материализовать все страницы, то есть ровно
    /// то, ради чего виртуализация и заведена. Виртуальный список обновляет свои карточки сам
    /// (<c>DocVirtualList.ReapplyTextState</c>).
    /// </summary>
    public IReadOnlyList<DocCardVm> Cards =>
        _items is List<object> list ? [.. list.OfType<DocCardVm>()] : [];

    // Геометрия плитки и число строк сниппета живут в DocTiles — там они связаны арифметикой
    // с высотой карточки, и там же их проверяет тест.

    /// <summary>Текст запроса — контроллер читает его при обновлениях ленты.</summary>
    public string SearchText => SearchBox.Text ?? "";

    /// <summary>Положение прокрутки — контроллер восстанавливает его после тихого обновления.</summary>
    public double ScrollOffset
    {
        get => CardsScroll.Offset.Y;
        set => RestoreScroll(value);
    }

    // До отрисовки у ScrollViewer нет высоты содержимого, и присвоение схлопнулось бы в 0.
    private void RestoreScroll(double offset) =>
        Dispatcher.UIThread.Post(
            () => CardsScroll.Offset = CardsScroll.Offset.WithY(offset),
            DispatcherPriority.Background);

    /// <summary>Показать ленту. <paramref name="items"/> — виртуальный список либо готовый набор.</summary>
    public void Show(IList items, string emptyText = "Нет документов")
    {
        _items = items;
        CardsHost.ItemsSource = items;
        EmptyLabel.Text      = emptyText;
        EmptyLabel.IsVisible = items.Count == 0;
    }

    // ── Блок поиска ──────────────────────────────────────────────────────────

    /// <summary>Что делает ссылка справа в подвале; null — ссылки нет.</summary>
    private Action? _statusAction;

    // Тот же запрос можно нажать еще раз: повтор — это «обновить выдачу», и он нужен после
    // чтения документов или правок в базе. Раньше повтор молча глотался, а кнопка гасла на
    // выполненном запросе, и обновить выдачу руками было нечем. Двойное нажатие безопасно:
    // загрузки, начатые раньше, гасятся поколением контроллера.
    private void Find()
    {
        var query = SearchText.Trim();
        if (query.Length == 0) return;
        SearchRequested?.Invoke(this, query);
    }

    // Крестик, кнопка и цвет якоря — одной функцией: три состояния одного ряда не должны
    // выставляться порознь.
    private void SyncCommandRow()
    {
        ClearBtn.IsVisible = SearchText.Length > 0;
        FindBtn.IsEnabled  = SearchText.Trim().Length > 0;
        SearchIcon.Stroke  = SearchText.Length > 0
            ? Res("BrushAccent",        "#007AFF")
            : Res("BrushSecondaryText", "#8E8E93");
    }

    /// <summary>
    /// Кисть из палитры с запасным значением.
    ///
    /// Именно <c>TryFindResource</c>, а НЕ <c>FindResource</c>: этот метод зовется в том числе
    /// из конструктора, когда контрол еще не в дереве, — там поиск ресурса возвращает
    /// <c>UnsetValue</c>, и приведение к <c>IBrush</c> роняет построение окна. Правило общее для
    /// всего кода на C# (см. docs/developer/ui/theming.md), и модель карточки ниже пользуется
    /// тем же приемом.
    /// </summary>
    private IBrush Res(string key, string fallbackHex) =>
        this.TryFindResource(key, out var res) && res is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallbackHex));

    /// <summary>
    /// Подвал блока: строка состояния и необязательное действие справа.
    ///
    /// Текст собирает контроллер — он один знает и про кеш, и про ленту. Вид здесь ничего не
    /// решает: его дело показать то, что сказали, одной строкой постоянной высоты.
    /// </summary>
    public void SetStatus(string text, string action = "", Action? onAction = null)
    {
        StatusLabel.Text = text;
        ToolTip.SetTip(StatusLabel, text);

        _statusAction        = onAction;
        StatusAction.Text    = action;
        StatusAction.IsVisible = action.Length > 0 && onAction is not null;
    }

    /// <summary>
    /// Нить хода чтения по нижней кромке блока. <paramref name="total"/> не больше нуля —
    /// нить скрыта. Доля задается весами колонок: измерять ширину незачем.
    /// </summary>
    public void SetProgress(int done, int total)
    {
        if (total <= 0) { ReadThread.IsVisible = false; return; }

        var passed = Math.Clamp(done, 0, total);
        ReadThread.ColumnDefinitions[0].Width = new GridLength(passed, GridUnitType.Star);
        ReadThread.ColumnDefinitions[1].Width = new GridLength(total - passed, GridUnitType.Star);
        ReadThread.IsVisible = true;
    }

    /// <summary>Показать ошибку раздела (нет прав, база занята) — тот же баннер, что в диалогах.</summary>
    public void ShowNotice(string text) => NoticeBanner.Show(text);

    public void HideNotice() => NoticeBanner.Hide();

    // Перестройка ленты под новый размер шрифта: шаблон строится заново, источник тот же.
    private void ApplyToList()
    {
        if (_items is null) return;
        CardsHost.ItemsSource = null;
        CardsHost.ItemsSource = _items;
    }

    // ── Карточка документа ───────────────────────────────────────────────────

    private Control BuildCard(DocCardVm vm)
    {
        // При переиспользовании контейнера виртуализацией контент временно = null — отдаем
        // пустую карточку (то же, что делает лента задач).
        if (vm is null) return new Control();

        var bold      = (FontWeight)this.FindResource("FontWeightBold")!;
        var secondary = (IBrush)this.FindResource("BrushSecondaryText")!;
        var accent    = (IBrush)this.FindResource("BrushAccent")!;
        var radius    = (CornerRadius)this.FindResource("RadiusMedium")!;
        var small     = (double)this.FindResource("FontSizeSmall")!;

        Control Bound<T>(T c, params (AvaloniaProperty Prop, string Name)[] binds) where T : Control
        {
            foreach (var (prop, name) in binds) c.Bind(prop, new Binding(name) { Source = vm });
            return c;
        }

        // Колонка 0 — плитка формата: окошко страницы сверху, лента формата снизу.
        //
        // Лента — светлая заливка и надпись цветом формата, как баннеры в диалогах. Сплошной
        // насыщенной заливки в приложении нет нигде, и плитка ее не заводит. Обводка документа
        // цветом формата тоже не набирается: она общая серая, одна на весь документ.
        //
        // Миниатюры выключены настройкой — окошка страницы нет ВОВСЕ: плитка целиком залита
        // цветом формата, и надпись стоит по центру ее высоты. Пустой лист над лентой не значил
        // бы ничего — картинки не будет, и ждать ее нечего. Настройка читается здесь потому, что
        // ее смена перечитывает ленту (LibraryControllerView.OnLibrarySettingsChanged), и карточки
        // собираются заново.
        var withThumbs = LibrarySettings.Thumbnails;
        var shape      = DocTiles.Of(LibrarySettings.CardSize);

        var sheet = new StackPanel();

        if (withThumbs)
        {
            // Миниатюра и пустое окошко лежат в одной ячейке и переключаются привязкой:
            // пересобирать карточку под приехавшую картинку нечем — контейнер строки к тому
            // моменту уже создан.
            var thumb = (Image)Bound(new Image
            {
                Width  = shape.BoxWidth,
                Height = shape.BoxHeight,
                Stretch = Stretch.UniformToFill,
            },
            (Image.SourceProperty,     nameof(DocCardVm.Thumbnail)),
            (Visual.IsVisibleProperty, nameof(DocCardVm.HasThumbnail)));

            var pageFill = (Border)Bound(new Border(),
                (Visual.IsVisibleProperty, nameof(DocCardVm.HasBadge)));
            pageFill.Background = (IBrush)this.FindResource("BrushSecondaryBg")!;

            var pageBox = new Panel();
            pageBox.Children.Add(pageFill);
            pageBox.Children.Add(thumb);

            // Своей рамки у окошка нет: документ обводит одна общая рамка снаружи (preview ниже).
            sheet.Children.Add(new Border
            {
                Width  = shape.BoxWidth,
                Height = shape.BoxHeight,
                Child  = pageBox,
            });
        }

        // Надпись формата растет вместе с лентой — токеном типографики, а не числом: иначе
        // настройка «Размер шрифта» обходила бы плитку стороной. TryFindResource с запасным
        // значением, потому что карточка собирается и до попадания в дерево.
        var labelSize = this.TryFindResource(shape.LabelFontKey, out var lf) && lf is double d
            ? d
            : shape.LabelFontFallback;

        var tapeText = (TextBlock)Bound(new TextBlock
        {
            FontWeight          = FontWeight.SemiBold,
            FontSize            = labelSize,
            // Длинное слово («EXCEL», редкое расширение) на маленькой плитке иначе обрезалось бы
            // посреди буквы жестким клипом — без единого признака, что текст не влез.
            TextTrimming        = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        },
        (TextBlock.TextProperty,       nameof(DocCardVm.FormatLabel)),
        (TextBlock.ForegroundProperty, nameof(DocCardVm.FormatStroke)));

        // Лента формата: заливка и надпись цветом формата. Линии между окошком и лентой НЕТ —
        // ни своей рамки сверху у ленты, ни снизу у окошка: заливка начинается прямо от листа,
        // и документ читается одним листом с цветным низом, а не двумя склеенными частями.
        var tape = (Border)Bound(new Border
        {
            Width  = shape.BoxWidth,
            // Без миниатюр лента занимает всю плитку — высота та же, что у карточки с окошком,
            // чтобы лента не поехала при смене настройки.
            Height = withThumbs ? shape.TapeHeight : shape.TileHeight,
            Child  = tapeText,
        },
        (Border.BackgroundProperty, nameof(DocCardVm.FormatFill)));

        sheet.Children.Add(tape);
        // Скругленный клип, а НЕ ClipToBounds у рамки: тот режет по прямоугольнику, и белый лист
        // с заливкой ленты закрывали углы своими прямыми. На белом фоне ленты это было незаметно,
        // а на выделенной карточке скругление исчезало. Радиус на линию меньше внешнего.
        Controls.Behaviors.RoundedClip.SetRadius(sheet, new CornerRadius(3));

        // Рамка вокруг ВСЕГО документа — окошка и ленты разом, цветом формата.
        var preview = (Border)Bound(new Border
        {
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Child           = sheet,
        },
        (Border.BorderBrushProperty, nameof(DocCardVm.FormatStroke)));

        // Колонка 2 — название, подпись «коллекция · база · имя файла», сниппет и действие.
        var title = (TextBlock)Bound(new TextBlock
        {
            FontWeight   = bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        },
        (TextBlock.TextProperty,       nameof(DocCardVm.TitleValue)),
        (TextBlock.ForegroundProperty, nameof(DocCardVm.TitleColor)));

        var subtitle = (TextBlock)Bound(new TextBlock
        {
            Foreground   = secondary,
            FontSize     = small,
            TextTrimming = TextTrimming.CharacterEllipsis,
        },
        (TextBlock.TextProperty, nameof(DocCardVm.SubtitleValue)));

        // Сниппет — строка с подсветкой совпадений. Inlines собираются в модели строки, потому
        // что приезжают вместе с найденным документом; привязать к ним TextBlock напрямую
        // нельзя, поэтому обновление идет событием модели.
        var snippet = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxLines     = shape.SnippetLines,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize     = small,
            Margin       = new Thickness(0, 2, 0, 0),
        };
        snippet.Bind(Visual.IsVisibleProperty, new Binding(nameof(DocCardVm.HasSnippet)) { Source = vm });
        vm.RenderSnippet(snippet);
        vm.SnippetChanged += () => vm.RenderSnippet(snippet);

        // Два действия у документа, и они разные по смыслу. «Прочитать» открывает правую
        // панель: текст документа с подсветкой найденного, не выходя из программы. «Открыть»
        // отдает файл системе — привычная программа, печать, правка.
        //
        // Это не Button, а кликабельные строки: две кнопки в карточке перетягивали бы на себя
        // все внимание, а действия здесь второстепенны рядом с самой карточкой.
        var readRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing     = 14,
            Margin      = new Thickness(0, 6, 0, 0),
        };
        readRow.Children.Add(ActionLink("IconBookOpen", "Прочитать", accent,
            () => { if (!vm.IsPlaceholder) DocReadRequested?.Invoke(this, vm.Doc); }));
        readRow.Children.Add(ActionLink("IconOpenExternal", "Открыть", secondary,
            () => { if (!vm.IsPlaceholder) DocActivated?.Invoke(this, vm.Doc); }));

        // Место под строку действий занято ВСЕГДА — и у заглушки страницы, и у пропавшего файла.
        // Спрятать ее совсем значило бы сделать такие карточки ниже соседних, а лента держится
        // ровно на том, что высота у всех одна: иначе она дергается на каждой подъехавшей
        // странице. Поэтому гасим прозрачностью, а клики снимаем отдельно.
        readRow.Bind(Visual.OpacityProperty, new Binding(nameof(DocCardVm.ReadOpacity)) { Source = vm });
        readRow.Bind(InputElement.IsHitTestVisibleProperty,
                     new Binding(nameof(DocCardVm.CanOpen)) { Source = vm });

        // Текст разведен по высоте плитки: что документ ЕСТЬ (название, где лежит, найденная
        // строка) — вверху, что с ним МОЖНО СДЕЛАТЬ — внизу. Собранные в кучу по центру, они
        // читались одним абзацем, и действие терялось среди подписей. Пустая строка «*»
        // посередине забирает весь запас высоты, поэтому у карточек любой высоты действие стоит
        // на одном уровне с нижним краем плитки.
        var textTop = new StackPanel { Spacing = 2 };
        textTop.Children.Add(title);
        textTop.Children.Add(subtitle);
        textTop.Children.Add(snippet);

        var left = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(textTop, 0);
        Grid.SetRow(readRow, 2);
        left.Children.Add(textTop);
        left.Children.Add(readRow);

        // Колонка 4 — чипы состояния и метрики файла. Колонка была занята одним чипом типа и
        // выглядела пустой; размер и объем встают сюда, не отнимая у карточки ни одной строки.
        var right = new StackPanel
        {
            Spacing             = 5,
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        right.Children.Add(BoundChip(vm, nameof(DocCardVm.TypeText),
                                         nameof(DocCardVm.TypeBg),
                                         nameof(DocCardVm.TypeFg),
                                         nameof(DocCardVm.HasType)));
        right.Children.Add(BoundChip(vm, nameof(DocCardVm.UnreadText),
                                         nameof(DocCardVm.UnreadBg),
                                         nameof(DocCardVm.UnreadFg),
                                         nameof(DocCardVm.IsUnread)));
        right.Children.Add(BoundChip(vm, nameof(DocCardVm.MissingText),
                                         nameof(DocCardVm.MissingBg),
                                         nameof(DocCardVm.MissingFg),
                                         nameof(DocCardVm.IsMissing)));

        // Метрики: размер всегда, страницы — у прочитанных. Моноширинных цифр в шрифте
        // приложения нет, поэтому строка просто прижата вправо.
        right.Children.Add(Bound(new TextBlock
        {
            Foreground          = secondary,
            FontSize            = small,
            HorizontalAlignment = HorizontalAlignment.Right,
        },
        (TextBlock.TextProperty,   nameof(DocCardVm.MetaText)),
        (Visual.IsVisibleProperty, nameof(DocCardVm.HasMeta))));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,12,*,12,Auto") };
        var stripe = (Border)Bound(new Border
        {
            Width = 4, CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Stretch,
        },
        (Border.BackgroundProperty, nameof(DocCardVm.StripeBrush)));

        var previewHost = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        previewHost.Children.Add(stripe);
        previewHost.Children.Add(preview);

        Grid.SetColumn(previewHost, 0);
        Grid.SetColumn(left, 2);
        Grid.SetColumn(right, 4);
        grid.Children.Add(previewHost);
        grid.Children.Add(left);
        grid.Children.Add(right);

        var card = new Border
        {
            CornerRadius = radius,
            Padding      = new Thickness(14, 10),
            Margin       = new Thickness(0, 0, 0, 8),
            // Высота карточки — из таблицы размеров, а не по содержимому: все строки ленты
            // обязаны быть одной высоты, иначе прокрутка дергается, а ползунок «дышит»
            // (протяженность VirtualizingStackPanel оценивает по материализованным строкам).
            // MinHeight, а не Height: при увеличенном шрифте текст обязан помещаться.
            MinHeight    = shape.CardHeight,
            Child        = grid,
        };
        card.Bind(Border.BorderThicknessProperty, card.GetResourceObservable("BorderLine"));
        card.Bind(Border.BackgroundProperty,
                  new Binding(nameof(DocCardVm.BackgroundBrush)) { Source = vm });
        card.Bind(Border.BorderBrushProperty,
                  new Binding(nameof(DocCardVm.BorderBrushValue)) { Source = vm });

        card.PointerEntered   += (_, _) => vm.IsHovered = true;
        card.PointerExited    += (_, _) => vm.IsHovered = false;
        card.Tapped           += (_, e) => OnCardTapped(vm, e);
        // Двойной клик — то же, что «Прочитать»: главное действие документа теперь чтение в
        // панели, а не запуск чужой программы. Открыть файл наружу — отдельная кнопка, и
        // случайно попасть в нее двойным кликом нельзя.
        card.DoubleTapped     += (_, e) =>
        {
            e.Handled = true;
            if (!vm.IsPlaceholder) DocReadRequested?.Invoke(this, vm.Doc);
        };
        return card;
    }

    /// <summary>
    /// Действие карточки: иконка плюс подпись, клик целиком по паре. Останавливает событие
    /// своим <c>Handled</c> — иначе клик добрался бы до карточки и вдобавок сменил выделение.
    /// </summary>
    private static Control ActionLink(string icon, string text, IBrush brush, Action run)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing     = 6,
            Cursor      = new Cursor(StandardCursorType.Hand),
        };
        row.Children.Add(IconGlyph(icon, brush));
        row.Children.Add(new TextBlock
        {
            Text              = text,
            Foreground        = brush,
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Tapped += (_, e) => { e.Handled = true; run(); };
        return row;
    }

    private static Control IconGlyph(string key, IBrush brush)
    {
        var size = (double)Application.Current!.FindResource("IconSizeMedium")!;
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data            = (Geometry)Application.Current!.FindResource(key)!,
            Stroke          = brush,
            StrokeThickness = (double)Application.Current!.FindResource("IconStrokeMedium")!,
            StrokeLineCap   = PenLineCap.Round,
            StrokeJoin      = PenLineJoin.Round,
        };
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(path);
        return new Viewbox
        {
            Width = size, Height = size,
            VerticalAlignment = VerticalAlignment.Center,
            Child = canvas,
        };
    }

    // Чип карточки: пилюля с текстом. Все привязано к модели строки — и текст, и обе кисти, и
    // видимость. Строится один раз на всю жизнь контейнера.
    private static Border BoundChip(DocCardVm vm, string textProp, string bgProp, string fgProp,
                                    string visibleProp)
    {
        var label = new TextBlock { FontWeight = FontWeight.SemiBold };
        label.Bind(TextBlock.TextProperty,       new Binding(textProp) { Source = vm });
        label.Bind(TextBlock.ForegroundProperty, new Binding(fgProp)   { Source = vm });

        var chip = new Border
        {
            CornerRadius = new CornerRadius(99),
            Padding      = new Thickness(10, 3),
            Child        = label,
        };
        chip.Bind(Border.BackgroundProperty, new Binding(bgProp) { Source = vm });
        chip.Bind(Visual.IsVisibleProperty,  new Binding(visibleProp) { Source = vm });
        return chip;
    }

    // ── Выделение ────────────────────────────────────────────────────────────

    private void OnCardTapped(DocCardVm vm, TappedEventArgs e)
    {
        if (vm.IsPlaceholder) return;

        var mods = e.KeyModifiers;
        if (mods.HasFlag(KeyModifiers.Shift) && mods.HasFlag(KeyModifiers.Control) && _anchor is not null)
            _selection.SelectRange(RealizedDocs(), _anchor.Doc, vm.Doc);
        else if (mods.HasFlag(KeyModifiers.Shift))
            _selection.Toggle(vm.Doc);
        else
        {
            _selection.Select(vm.Doc);
            _anchor = vm;
        }

        SyncSelection();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // Карточки, которые СЕЙЧАС на экране (страницы виртуального списка в кеше). Диапазон
    // Ctrl+Shift считается только по ним: обещать выделение того, чего лента не догрузила,
    // нельзя.
    private List<DocRecord> RealizedDocs()
    {
        var result = new List<DocRecord>();
        if (_items is null) return result;
        if (_items is DocVirtualList virt) return virt.RealizedDocs();

        foreach (var row in _items)
            if (row is DocCardVm { IsPlaceholder: false } card) result.Add(card.Doc);
        return result;
    }

    private void SyncSelection()
    {
        if (_items is DocVirtualList virt) { virt.SyncSelection(_selection); return; }
        if (_items is null) return;
        foreach (var row in _items)
            if (row is DocCardVm { IsPlaceholder: false } card)
                card.IsSelected = _selection.Contains(card.Doc);
    }
}

// ── Модель строки карточки ───────────────────────────────────────────────────
//
// ВСЕ видимые значения карточка берет привязками к этой модели, а не читает при постройке. Так
// строка виртуального списка рождается ЗАГЛУШКОЙ и превращается в документ НА МЕСТЕ (Fill),
// когда придет фоновая страница. Пересобирать ленту для этого не нужно, а высота заглушки
// совпадает с высотой карточки (разметка одна и та же), поэтому прокрутка не дергается.
public partial class DocCardVm : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundBrush))]
    [NotifyPropertyChangedFor(nameof(BorderBrushValue))]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundBrush))]
    private bool _isHovered;

    /// <summary>Сниппет перерисован — строка карточки собирает Inlines заново.</summary>
    public event Action? SnippetChanged;

    /// <summary>Фон карточки: выделение заметнее наведения (как в ленте задач).</summary>
    public IBrush BackgroundBrush => IsSelected
        ? new SolidColorBrush(Color.FromArgb(50, 128, 128, 128))
        : IsHovered ? Brush("BrushInactiveBg", "#FAFAFA") : Brush("BrushBg", "#FFFFFF");

    public IBrush BorderBrushValue => IsSelected
        ? Brush("BrushAccent", "#007AFF")
        : Brush("BrushSeparator", "#E5E5EA");

    private static IBrush Brush(string key, string fallback) =>
        Application.Current!.TryFindResource(key, out var res) && res is IBrush b
            ? b : new SolidColorBrush(Color.Parse(fallback));

    /// <summary>Документ строки. У заглушки — пустая заготовка: настоящего еще нет.</summary>
    public DocRecord Doc { get; private set; } = new();

    /// <summary>
    /// Строка еще не заполнена — место под документ, который тянет фоновая страница. Такая
    /// карточка не выделяется и не открывается: под ней пока нет записи.
    /// </summary>
    public bool IsPlaceholder { get; private set; }

    public string TitleValue    { get; private set; } = "";
    public IBrush TitleColor    { get; private set; } = Brushes.Gray;
    public string SubtitleValue { get; private set; } = "";
    /// <summary>Подпись на ленте плитки: «PDF», «WORD», «EXCEL», «ТЕКСТ» либо расширение.</summary>
    public string FormatLabel   { get; private set; } = "";
    /// <summary>Светлая заливка ленты.</summary>
    public IBrush FormatFill    { get; private set; } = Brushes.Transparent;
    /// <summary>Обводка ленты и цвет надписи — одна кисть на оба.</summary>
    public IBrush FormatStroke  { get; private set; } = Brushes.Gray;
    /// <summary>Миниатюры еще нет — окошко страницы залито серым.</summary>
    public bool   HasBadge      { get; private set; } = true;
    public IImage? Thumbnail    { get; private set; }
    public bool   HasThumbnail  => Thumbnail is not null;
    public string TypeText      { get; private set; } = "";
    public IBrush TypeBg        { get; private set; } = Brushes.Transparent;
    public IBrush TypeFg        { get; private set; } = Brushes.Gray;
    public bool   HasType       { get; private set; }
    public bool   IsMissing     { get; private set; }
    public string MissingText   { get; private set; } = "файл не найден";
    public IBrush MissingBg     { get; private set; } = Brushes.Transparent;
    public IBrush MissingFg     { get; private set; } = Brushes.Gray;
    /// <summary>Серый чип про текст документа: не прочитан, нет текстового слоя и т.п.</summary>
    public bool   IsUnread      { get; private set; }
    public string UnreadText    { get; private set; } = "";
    public IBrush UnreadBg      { get; private set; } = Brushes.Transparent;
    public IBrush UnreadFg      { get; private set; } = Brushes.Gray;
    /// <summary>Метрики файла в правой колонке: «4,2 МБ · 112 страниц».</summary>
    public string MetaText      { get; private set; } = "";
    public bool   HasMeta       { get; private set; }
    public IBrush StripeBrush   { get; private set; } = Brushes.Transparent;
    public bool   HasSnippet    { get; private set; }
    public bool   CanOpen       { get; private set; }

    /// <summary>Видимость строки «Прочитать документ» — прозрачностью, чтобы место под нее
    /// осталось занято и высота карточки не зависела от того, есть файл на диске или нет.</summary>
    public double ReadOpacity   => CanOpen ? 1 : 0;

    private List<(string Text, bool Hit)> _snippet = [];

    public DocCardVm(DocRecord doc) => Apply(doc);

    private DocCardVm() { }

    /// <summary>
    /// Пустое место под документ: разметка та же, поэтому строка занимает столько же высоты,
    /// сколько займет настоящая карточка, и прокрутка не прыгает, когда страница приедет.
    /// </summary>
    public static DocCardVm Loading() => new()
    {
        IsPlaceholder = true,
        TitleValue    = "Загрузка…",
        TitleColor    = Brush("BrushSecondaryText", "#8E8E93"),
        SubtitleValue = "",
        FormatLabel   = "",
        FormatFill    = Brush("BrushSecondaryBg", "#F2F2F7"),
        FormatStroke  = Brush("BrushSeparator", "#E5E5EA"),
        HasBadge      = true,
        StripeBrush   = Brushes.Transparent,
    };

    /// <summary>Заглушка → документ: значения меняются, привязки узнают об этом сами.</summary>
    public void Fill(DocRecord doc)
    {
        Apply(doc);
        foreach (var name in Notified) OnPropertyChanged(name);
        SnippetChanged?.Invoke();
    }

    /// <summary>Миниатюра приехала из фонового рендера — показать ее вместо бейджа.</summary>
    public void SetThumbnail(IImage image)
    {
        Thumbnail = image;
        HasBadge  = false;
        OnPropertyChanged(nameof(Thumbnail));
        OnPropertyChanged(nameof(HasThumbnail));
        OnPropertyChanged(nameof(HasBadge));
    }

    // Что обязано доехать до привязок при оживлении заглушки. Список явный, а не пустое имя
    // «обновилось все»: так видно, что именно показывает карточка, и новое поле не потеряется.
    private static readonly string[] Notified =
    [
        nameof(IsPlaceholder), nameof(TitleValue), nameof(TitleColor), nameof(SubtitleValue),
        nameof(FormatLabel), nameof(FormatFill), nameof(FormatStroke), nameof(HasBadge),
        nameof(Thumbnail), nameof(HasThumbnail),
        nameof(TypeText), nameof(TypeBg), nameof(TypeFg), nameof(HasType),
        nameof(IsMissing), nameof(MissingText), nameof(MissingBg), nameof(MissingFg),
        nameof(IsUnread), nameof(UnreadText), nameof(UnreadBg), nameof(UnreadFg),
        nameof(MetaText), nameof(HasMeta),
        nameof(StripeBrush), nameof(HasSnippet), nameof(CanOpen), nameof(ReadOpacity),
    ];

    private void Apply(DocRecord doc)
    {
        Doc           = doc;
        IsPlaceholder = false;

        // Название записи, а при пустом — имя файла: «Без названия» рядом с документом,
        // у которого имя файла и есть название, читалось бы как потеря данных.
        var title  = doc.DisplayTitle;
        var byFile = title == CalendarRecord.UntitledPlaceholder && doc.FileName.Length > 0;
        TitleValue = byFile ? doc.FileName : title;
        TitleColor = Brush("BrushPrimaryText", "#000000");

        var parts = new List<string>();
        if (doc.CollectionName.Length > 0) parts.Add(doc.CollectionName);
        if (doc.DbName.Length > 0)         parts.Add($"база «{doc.DbName}»");
        // Имя файла в подписи — только если оно не стало заголовком: дважды одно и то же
        // в двух строках подряд выглядит ошибкой показа.
        if (!byFile && doc.FileName.Length > 0) parts.Add(doc.FileName);
        SubtitleValue = string.Join(" · ", parts);

        var family   = DocFormats.FamilyOf(doc.Format);
        FormatLabel  = DocFormats.LabelOf(doc.Format);
        FormatFill   = Brush(DocFormats.FillBrushKey(family),   "#F3F3F4");
        FormatStroke = Brush(DocFormats.StrokeBrushKey(family), "#8E8E93");
        HasBadge     = Thumbnail is null;

        HasType = doc.DocType.Length > 0;
        TypeText = doc.DocType;
        TypeBg   = Brush("BrushAccentLight", "#E5F0FF");
        TypeFg   = Brush("BrushAccent", "#007AFF");

        // «Файл не найден» — то же состояние, что различает FileEditor: без него раздел обещал
        // бы открыть то, чего нет. Размер приходит ТЕМ ЖЕ вопросом к диску — существование и
        // длину он отдает разом, — поэтому метрика в правой колонке лишнего не стоит.
        //
        // Сам вопрос задан заранее, в фоне, вместе со страницей ленты (DocStamps.Probe). Раньше
        // его задавала карточка, то есть UI-поток, по разу на карточку: на локальном диске это
        // копейки, а на общей папке сервера — поход по сети на каждую из двухсот карточек
        // страницы и подвисшая прокрутка. Здесь диск спрашивается только у документа, который
        // пришел непроверенным, — медленно, но лучше, чем соврать «файл не найден».
        if (!doc.OnDiskKnown) DocStamps.Probe([doc]);
        IsMissing   = doc.Path.Length > 0 && doc.SizeBytes < 0;
        MissingBg   = Brush("BrushRedLight", "#FFE5E5");
        MissingFg   = Brush("BrushRed", "#FF3B30");
        CanOpen     = doc.Path.Length > 0 && !IsMissing;

        // Серый чип про текст. Красный тут был бы неправдой: непрочитанный документ — это не
        // поломка, а работа, которую еще не делали. Красным набрано только «файла нет».
        ApplyTextBits();

        // Нечитаемый цвет коллекции (правка базы руками) — полоска просто не рисуется;
        // ронять карточку из-за нее нечего.
        StripeBrush = ColorTools.Solid(EventColors.Display(doc.Color)) ?? Brushes.Transparent;

        _snippet   = DocTextIndex.SplitSnippet(doc.Snippet);
        HasSnippet = _snippet.Count > 0;
    }

    // Чип про текст и метрики — из состояния, которое кеш узнает ПОЗЖЕ ленты. Поэтому вынесено
    // отдельно: RefreshTextState зовет это же, когда перепись досчиталась.
    private void ApplyTextBits()
    {
        UnreadText = UnreadLabel(Doc.TextState);
        IsUnread   = UnreadText.Length > 0 && !IsMissing;
        UnreadBg   = Brush("BrushSecondaryBg", "#F2F2F7");
        UnreadFg   = Brush("BrushSecondaryText", "#8E8E93");

        MetaText = BuildMeta(Doc);
        HasMeta  = MetaText.Length > 0;
    }

    /// <summary>
    /// Кеш досчитал, что известно про текст этого документа, — обновить чип и метрики на месте.
    /// Карточка уже на экране, пересобирать ее нечем.
    /// </summary>
    public void RefreshTextState()
    {
        if (IsPlaceholder) return;
        ApplyTextBits();
        OnPropertyChanged(nameof(IsUnread));
        OnPropertyChanged(nameof(UnreadText));
        OnPropertyChanged(nameof(MetaText));
        OnPropertyChanged(nameof(HasMeta));
    }

    // Подпись серого чипа по состоянию текста. Пустая строка — чипа нет: у прочитанного
    // документа сообщать не о чем, и у непрошенного кеша тоже.
    private static string UnreadLabel(DocTextState state) => state switch
    {
        DocTextState.NotRead     => "текст не прочитан",
        DocTextState.NoText      => "нет текстового слоя",
        DocTextState.Unsupported => "поиск только по названию",
        DocTextState.Failed      => "не удалось прочитать",
        _                        => "",
    };

    // Метрики правой колонки. Размер есть всегда (его отдает та же проверка файла), страницы —
    // только у прочитанных документов: число страниц считается при разборе, и обещать его
    // раньше было бы враньем. Больше двух значений не ставим — третье никто не читает.
    private static string BuildMeta(DocRecord doc)
    {
        var parts = new List<string>(2);
        if (doc.SizeBytes >= 0) parts.Add(SizeText(doc.SizeBytes));
        if (doc.Pages > 0)      parts.Add($"{doc.Pages} {PagesWord(doc.Pages)}");
        return string.Join(" · ", parts);
    }

    private static string SizeText(long bytes)
    {
        if (bytes < 1024)             return $"{bytes} Б";
        if (bytes < 1024L * 1024)     return $"{bytes / 1024.0:0.#} КБ";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.#} МБ";
        return $"{bytes / (1024.0 * 1024 * 1024):0.#} ГБ";
    }

    // «1 страница», «2 страницы», «5 страниц» — обычное русское согласование, включая
    // одиннадцать-девятнадцать, которые ведут себя как «страниц» вопреки последней цифре.
    private static string PagesWord(int n)
    {
        var tail = n % 100;
        if (tail is >= 11 and <= 14) return "страниц";
        return (n % 10) switch
        {
            1           => "страница",
            2 or 3 or 4 => "страницы",
            _           => "страниц",
        };
    }

    /// <summary>
    /// Собрать строку сниппета: обычные куски — как есть, совпадения — с желтой подсветкой.
    /// Inlines привязкой не выражаются, поэтому карточка зовет это сама — при постройке и по
    /// событию <see cref="SnippetChanged"/>.
    /// </summary>
    public void RenderSnippet(TextBlock target)
    {
        target.Inlines?.Clear();
        if (_snippet.Count == 0) return;

        var hitBg = Brush("BrushHighlight", "#FFF0B3");
        var plain = Brush("BrushSecondaryText", "#8E8E93");
        var text  = Brush("BrushPrimaryText", "#1C1C1E");

        foreach (var (chunk, hit) in _snippet)
            target.Inlines?.Add(new Run(chunk)
            {
                Foreground = hit ? text : plain,
                Background = hit ? hitBg : null,
                FontWeight = hit ? FontWeight.SemiBold : FontWeight.Normal,
            });
    }
}
