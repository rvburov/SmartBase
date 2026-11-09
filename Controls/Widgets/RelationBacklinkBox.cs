using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using SmartBase.Controls.Behaviors;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Utils;

namespace SmartBase.Controls.Widgets;

/// <summary>
/// Карточка ссылок страницы записи («Счета · Клиент»): число записей-источников и первые названия
/// текстом, иконка-связь справа, как у <see cref="RelationSelectBox"/> в режиме правки. Нажатие
/// открывает список источников (<see cref="OpenList"/>): поиск по названию, сначала новые записи,
/// страницами по <see cref="PageSize"/> при прокрутке, под списком — сколько показано из скольких.
/// Клик по строке, карандаш или Enter — переход к записи-источнику (<see cref="Navigate"/>).
/// Галочек нет: ссылку ставят и снимают в самой записи-источнике.
///
/// Читает в фоне по снимку баз (<see cref="RelationBacklinkReader"/>): поиск — с задержкой 150 мс,
/// отменой и номером поколения, как в списке связи; закрытие списка и снятие контрола отменяют
/// незавершенное чтение, устаревший ответ не перекрывает новый.
/// </summary>
public sealed class RelationBacklinkBox : Panel
{
    public const int PageSize = 200;

    private readonly RelationBacklinkReader _reader;
    private readonly RelationBacklinkGroup _group;
    private readonly Control _iconHit;
    private readonly Popup _popup;
    private readonly AvaloniaList<SourceVm> _shown = [];
    private readonly HashSet<int> _shownIds = [];
    private TextBox _search = null!;
    private ListBox _list = null!;
    private TextBlock _hint = null!;
    private CancellationTokenSource? _work;
    private int _generation;
    private string _query = "";
    private int? _next;
    private int? _total;
    private bool _loading;
    // Точное число уже считается — следующие страницы второго подсчета не заводят.
    private bool _counting;

    /// <summary>Переход к записи-источнику: (база-источник — 0, если это база записи; номер записи).</summary>
    public event EventHandler<(int SourceDbId, int RecordId)>? Navigate;

    /// <summary>Список источников закрыт.</summary>
    public event EventHandler? ListClosed;

    public RelationBacklinkBox(RelationBacklinkReader reader, RelationBacklinkGroup group)
    {
        _reader = reader;
        _group  = group;

        var value = new TextBlock
        {
            Text              = RelationBacklinkText.Value(group),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Foreground        = group.Sources == 0 ? Brush("BrushSecondaryText", Colors.Gray) : Brush("BrushPrimaryText", Colors.Black),
        };
        _iconHit = new Border
        {
            Background        = Brushes.Transparent,
            Padding           = new Thickness(4, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Stretch,
            Child             = BuildIcon("IconFieldRelation"),
        };
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_iconHit, 1);
        content.Children.Add(value);
        content.Children.Add(_iconHit);
        Children.Add(new Border
        {
            Background        = Brushes.Transparent,
            MinHeight         = 30,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child             = content,
            Cursor            = new Cursor(StandardCursorType.Hand),
        });

        _popup = new Popup
        {
            PlacementTarget       = _iconHit,
            Placement             = PlacementMode.BottomEdgeAlignedRight,
            IsLightDismissEnabled = true,
            Child                 = WindowZoom.WrapPopupContent(BuildList()),
        };
        Children.Add(_popup);
        _popup.Closed += (_, _) =>
        {
            CancelWork();
            ListClosed?.Invoke(this, EventArgs.Empty);
        };
        DetachedFromVisualTree += (_, _) => CancelWork();
        PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            OpenList();
        };
    }

    public RelationBacklinkGroup Group => _group;

    /// <summary>Список открыт.</summary>
    public bool IsListOpen => _popup.IsOpen;

    /// <summary>Строки списка (стенд проверяет страницы и поиск).</summary>
    internal IReadOnlyList<(int RecordId, string Title)> ShownSources => _shown.Select(s => (s.Id, s.Title)).ToList();

    /// <summary>Текст под списком.</summary>
    internal string HintText => _hint.IsVisible ? _hint.Text ?? "" : "";

    /// <summary>Чтение списка еще идет.</summary>
    internal bool IsLoading => _loading;

    /// <summary>Открыть список источников с пустым поиском.</summary>
    public void OpenList()
    {
        if (_popup.IsOpen) return;
        // Число считается заново: с прошлого открытия ссылки могли поменяться.
        _total = null;
        _search.Text = "";
        _popup.IsOpen = true;
        Reload("", delay: false);
        _search.Focus();
    }

    /// <summary>Подгрузить следующую страницу (прокрутка к концу списка).</summary>
    internal void LoadMore()
    {
        if (_loading || _next is null || !_popup.IsOpen) return;
        _ = LoadAsync(_query, _next, _generation, delay: false);
    }

    /// <summary>Поиск по названию (стенд набирает текст поля поиска).</summary>
    internal void Search(string text) => _search.Text = text;

    private Control BuildList()
    {
        var fontBase = Res("FontSizeBase", 12.0);
        _list = new ListBox
        {
            Background      = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding         = new Thickness(8, 6),
            ItemsSource     = _shown,
            ItemTemplate    = BuildItemTemplate(fontBase),
            SelectionMode   = SelectionMode.Single,
        };
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(0, 2)),
                        new Setter(ListBoxItem.MinHeightProperty, 0.0) },
        });
        _list.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || _list.SelectedItem is not SourceVm vm) return;
            e.Handled = true;
            Open(vm.Id);
        };
        // Прокрутка к концу — следующая страница.
        _list.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<ScrollViewer>("PART_ScrollViewer") is not { } scroll) return;
            scroll.ScrollChanged += (_, _) =>
            {
                if (scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 40) LoadMore();
            };
        };

        _search = new TextBox
        {
            Watermark                = "Поиск по названию…",
            Height                   = 30,
            FontSize                 = fontBase,
            Background               = Brush("BrushBg", Colors.White),
            CornerRadius             = Radius("RadiusBase", 6),
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin                   = new Thickness(0, 0, 0, 4),
        };
        _search.TextChanged += (_, _) =>
        {
            // Отложенное TextChanged от установки текста при открытии — не новый поиск.
            var text = _search.Text?.Trim() ?? "";
            if (text == _query) return;
            Reload(text, delay: true);
        };
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (_list.SelectedItem as SourceVm ?? _shown.FirstOrDefault()) is { } vm)
            {
                e.Handled = true;
                Open(vm.Id);
            }
            else if (e.Key == Key.Down && _shown.Count > 0)
            {
                e.Handled = true;
                _list.SelectedIndex = Math.Max(0, _list.SelectedIndex);
                (_list.ContainerFromIndex(_list.SelectedIndex) as Control)?.Focus();
            }
        };

        var card = new Border
        {
            Background      = Brush("BrushBg", Colors.White),
            BorderBrush     = Brush("BrushSeparator", Colors.LightGray),
            BorderThickness = new Thickness(1),
            CornerRadius    = Radius("RadiusBase", 6),
            Padding         = new Thickness(0),
            ClipToBounds    = true,
            Height          = Res("PopupListHeight", 232),
            Child           = Controls.Components.EmptyListHint.Wrap(_list, _search, Brush("BrushSecondaryText", Colors.Gray), fontBase),
        };

        _hint = new TextBlock
        {
            FontSize     = Res("FontSizeSmall", 11.0),
            Foreground   = Brush("BrushSecondaryText", Colors.Gray),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(2, 4, 2, 0),
            IsVisible    = false,
        };

        var stack = new StackPanel { Spacing = 2, Width = Res("PopupContentWidth", 240) };
        stack.Children.Add(_search);
        stack.Children.Add(card);
        stack.Children.Add(_hint);
        return new Border
        {
            Background      = Brush("BrushSecondaryBg", Colors.WhiteSmoke),
            BorderBrush     = Brush("BrushSeparator", Colors.LightGray),
            BorderThickness = new Thickness(1),
            CornerRadius    = Radius("RadiusMedium", 8),
            Padding         = new Thickness(12),
            BoxShadow       = BoxShadows.Parse("0 4 16 0 #22000000"),
            Child           = stack,
        };
    }

    // Строка: название записи-источника и карандаш перехода; клик по строке — тоже переход.
    private IDataTemplate BuildItemTemplate(double fontBase) =>
        new FuncDataTemplate<SourceVm>((_, _) =>
        {
            var name = new TextBlock
            {
                FontSize          = fontBase,
                TextTrimming      = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            name.Bind(TextBlock.TextProperty, new Binding(nameof(SourceVm.Title)));
            var edit = new Border
            {
                Background = Brushes.Transparent,
                Padding    = new Thickness(6, 2),
                Child      = BuildIcon("IconPencil"),
            };
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Background        = Brushes.Transparent,
                Cursor            = new Cursor(StandardCursorType.Hand),
            };
            Grid.SetColumn(edit, 1);
            grid.Children.Add(name);
            grid.Children.Add(edit);
            // Нажатие, а не отпускание — как у карандаша в списке связи: выбор строки списком
            // до перехода не нужен.
            grid.PointerPressed += (s, e) =>
            {
                if ((s as Control)?.DataContext is not SourceVm vm || !e.GetCurrentPoint(s as Control).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                Open(vm.Id);
            };
            return grid;
        });

    private void Open(int recordId)
    {
        _popup.IsOpen = false;
        Navigate?.Invoke(this, (_group.SourceDbId, recordId));
    }

    private void CancelWork()
    {
        _generation++;
        _work?.Cancel();
        _work?.Dispose();
        _work = null;
        _loading = false;
        _counting = false;
    }

    // Новый поиск: прежний ответ больше не нужен, список и ключ страницы — с начала.
    private void Reload(string query, bool delay)
    {
        CancelWork();
        _query = query;
        _next  = null;
        _shown.Clear();
        _shownIds.Clear();
        _ = LoadAsync(query, null, _generation, delay);
    }

    private async Task LoadAsync(string query, int? before, int generation, bool delay)
    {
        var cancel = _work ??= new CancellationTokenSource();
        var ct = cancel.Token;
        _loading = true;
        if (query.Length > 0 && before is null) ShowHint("Поиск…");
        try
        {
            if (delay) await Task.Delay(150, ct);
            var page = await Task.Run(() => _reader.Sources(_group, query, before, PageSize, ct), ct);
            if (ct.IsCancellationRequested || generation != _generation) return;
            foreach (var source in page.Items)
                if (_shownIds.Add(source.RecordId)) _shown.Add(new SourceVm(source.RecordId, RelationBacklinkText.Title(source.Title)));
            _next = page.Next;
            _loading = false;
            UpdateHint();
            // Без поиска точное число нужно, только пока показаны не все.
            if (query.Length == 0 && _next is not null && _total is null && !_counting) _ = CountAsync(generation, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось прочитать ссылки на запись");
            if (generation != _generation) return;
            _loading = false;
            _shown.Clear();
            _shownIds.Clear();
            ShowHint(ex is PermissionDeniedException ? "Нет доступа к коллекции" : "Поиск недоступен");
        }
    }

    private async Task CountAsync(int generation, CancellationToken ct)
    {
        _counting = true;
        try
        {
            var total = await Task.Run(() => _reader.Total(_group, ct), ct);
            if (ct.IsCancellationRequested || generation != _generation) return;
            _total = total;
            UpdateHint();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Не удалось посчитать ссылки на запись"); }
        finally { if (generation == _generation) _counting = false; }
    }

    private void UpdateHint()
    {
        if (_query.Length > 0)
        {
            if (_next is null) _hint.IsVisible = false;
            else ShowHint(RelationBacklinkText.SearchHint(_shown.Count));
            return;
        }
        if (_next is null) { _hint.IsVisible = false; return; }
        var text = RelationBacklinkText.PageHint(_shown.Count, _total, _group.More);
        if (text.Length == 0) _hint.IsVisible = false;
        else ShowHint(text);
    }

    private void ShowHint(string text)
    {
        _hint.Text = text;
        _hint.IsVisible = true;
    }

    private static Control BuildIcon(string key)
    {
        var geo    = Application.Current!.TryFindResource(key, out var g) && g is Geometry geom ? geom : null;
        var size   = Res("IconSizeSmall", 16.0);
        var thick  = Res("IconStrokeSmall", 1.8);
        var stroke = Brush("BrushSecondaryText", Colors.Gray);
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data            = geo,
            Stroke          = stroke,
            StrokeThickness = thick,
            StrokeLineCap   = PenLineCap.Round,
            StrokeJoin      = PenLineJoin.Round,
        });
        return new Viewbox { Width = size, Height = size, Child = canvas, VerticalAlignment = VerticalAlignment.Center };
    }

    private static IBrush Brush(string key, Color fallback) =>
        Application.Current!.TryFindResource(key, out var r) && r is IBrush b ? b : new SolidColorBrush(fallback);

    private static CornerRadius Radius(string key, double fallback) =>
        Application.Current!.TryFindResource(key, out var r) && r is CornerRadius c ? c : new CornerRadius(fallback);

    private static double Res(string key, double fallback) =>
        Application.Current!.TryFindResource(key, out var r) && r is double d ? d : fallback;

    private sealed record SourceVm(int Id, string Title);
}
