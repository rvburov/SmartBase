using System.ComponentModel;
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
using SmartBase.Controls.Behaviors;
using SmartBase.Controls.Components;
using SmartBase.Utils;

namespace SmartBase.Extensions.DocRegistry;

/// <summary>
/// Выбор адресатов рассылки: та же плашка, поиск и закрепленная шапка, что у фильтра
/// таблицы. Набор меняется только по «Готово»; закрытие мимо кнопки отменяет правки.
/// </summary>
public sealed class ParticipantPicker : UserControl
{
    private readonly Func<string> _source;
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock _summary;
    private readonly Button _button;
    private IReadOnlyList<BroadcastRecipient> _participants = [];

    public event EventHandler? SelectionChanged;

    /// <summary>Текущий выбор, без удаленных из списка участников.</summary>
    public IReadOnlyCollection<string> SelectedIds => _participants.Where(p => _selected.Contains(p.Id))
        .Select(p => p.Id).ToArray();

    public string Summary => _summary.Text ?? "";

    public ParticipantPicker(Func<string> source)
    {
        _source = source;
        _summary = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var caret = new Avalonia.Controls.Shapes.Path
        {
            Data = Resource<Geometry>(null, "IconChevronDown"),
            Stroke = Resource<IBrush>(null, "BrushSecondaryText"),
            StrokeThickness = 1,
            Width = 12,
            Height = 12,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        content.Children.Add(_summary);
        Grid.SetColumn(caret, 1);
        content.Children.Add(caret);
        _button = new Button
        {
            Name = "BroadcastRecipientsButton",
            Classes = { "dialog", "auto" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            CornerRadius = Resource<CornerRadius>(null, "RadiusBase"),
            Content = content,
        };
        _button.Click += (_, _) => Open();
        Content = _button;
        RefreshParticipants();
    }

    /// <summary>Перечитать текущее поле настроек. Новые адресаты сами не выбираются.</summary>
    public void RefreshParticipants()
    {
        _participants = ParticipantBroadcast.Participants(_source());
        var before = _selected.Count;
        _selected.IntersectWith(_participants.Select(p => p.Id));
        RefreshSummary();
        if (_selected.Count != before) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Open()
    {
        if (!IsEffectivelyEnabled) return;
        RefreshParticipants();
        var flyout = PopupPlate.Filter();
        flyout.Content = WindowZoom.WrapPopupContent(BuildPanel(this, flyout.Hide));
        TransientPopups.Track(flyout);
        flyout.ShowAt(_button);
    }

    /// <summary>Содержимое открытого выбора; общий код также проверяется стендом без экрана.</summary>
    internal Control BuildPanel(Control host, Action close)
    {
        var working = new HashSet<string>(_selected, StringComparer.OrdinalIgnoreCase);
        var font = Resource<double>(host, "FontSizeBase");
        var radius = Resource<CornerRadius>(host, "RadiusBase");
        var secondary = Resource<IBrush>(host, "BrushSecondaryText");
        var separator = Resource<IBrush>(host, "BrushSeparator");
        var panel = new StackPanel
        {
            Width = Resource<double>(host, "PopupContentWidth"),
            Margin = new Thickness(12),
            Spacing = 2,
            Cursor = new Cursor(StandardCursorType.Arrow),
        };
        var search = new TextBox
        {
            Name = "RecipientSearch",
            Watermark = "Поиск участников...",
            Height = Resource<double>(host, "ControlHeightStandard"),
            CornerRadius = radius,
            Background = Resource<IBrush>(host, "BrushBg"),
            Margin = new Thickness(0, 0, 0, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var selectAll = new CheckBox
        {
            Name = "RecipientSelectAll",
            Content = "(Выделить все)",
            FontSize = font,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        var shown = new AvaloniaList<RecipientRow>();
        var syncing = false;
        void SyncAll()
        {
            syncing = true;
            selectAll.Content = string.IsNullOrWhiteSpace(search.Text)
                ? "(Выделить все)" : "(Выделить все найденное)";
            selectAll.IsEnabled = shown.Count > 0;
            selectAll.IsChecked = shown.Count > 0 && shown.All(r => r.Checked);
            syncing = false;
        }
        var items = _participants.Select(p => new RecipientRow(p, working, () =>
        {
            if (!syncing) SyncAll();
        })).ToArray();
        var list = new ListBox
        {
            Name = "RecipientValues",
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            ItemsSource = shown,
            ItemTemplate = new FuncDataTemplate<RecipientRow>((_, _) =>
            {
                var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
                label.Bind(TextBlock.TextProperty, new Binding(nameof(RecipientRow.Label)));
                var box = new CheckBox
                {
                    Content = label,
                    FontSize = font,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Cursor = new Cursor(StandardCursorType.Hand),
                };
                box.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(RecipientRow.Checked)) { Mode = BindingMode.TwoWay });
                return box;
            }),
        };
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(0, 2)),
                        new Setter(ListBoxItem.MinHeightProperty, 0.0) },
        });
        void FlatRow(params string[] states) => list.Styles.Add(new Style(x =>
        {
            Selector s = x.OfType<ListBoxItem>();
            foreach (var state in states) s = s.Class(state);
            return s.Template().OfType<ContentPresenter>().Name("PART_ContentPresenter");
        })
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent) },
        });
        FlatRow();
        FlatRow(":pointerover");
        FlatRow(":selected");
        FlatRow(":selected", ":pointerover");

        void Filter()
        {
            var q = search.Text?.Trim() ?? "";
            shown.Clear();
            shown.AddRange(q.Length == 0 ? items : items.Where(p =>
                p.Label.Contains(q, StringComparison.OrdinalIgnoreCase)
                || p.Id.Contains(q, StringComparison.OrdinalIgnoreCase)));
            SyncAll();
        }
        search.TextChanged += (_, _) => Filter();
        selectAll.IsCheckedChanged += (_, _) =>
        {
            if (syncing) return;
            var on = selectAll.IsChecked == true;
            syncing = true;
            foreach (var item in shown) item.Checked = on;
            syncing = false;
            SyncAll();
        };
        var card = new Border
        {
            Background = Resource<IBrush>(host, "BrushBg"),
            BorderBrush = separator,
            CornerRadius = radius,
            Padding = new Thickness(0),
            ClipToBounds = true,
            Height = Resource<double>(host, "PopupListHeight"),
            Child = EmptyListHint.Wrap(list, search, secondary, font,
                ScrollBarLink.HeaderedList(selectAll, list, gap: 2)),
        };
        card.Bind(Border.BorderThicknessProperty, card.GetResourceObservable("BorderLine"));
        panel.Children.Add(search);
        panel.Children.Add(card);
        var line = new Border { Background = separator, Margin = new Thickness(0, 6) };
        line.Bind(Layoutable.HeightProperty, line.GetResourceObservable("SeparatorThickness"));
        line.Bind(Layoutable.UseLayoutRoundingProperty, line.GetResourceObservable("LineUseLayoutRounding"));
        RenderOptions.SetEdgeMode(line, EdgeMode.Aliased);
        panel.Children.Add(line);
        var done = new Button
        {
            Name = "RecipientDone",
            Content = "Готово",
            Classes = { "dialog" },
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0),
        };
        done.Click += (_, _) =>
        {
            // Список мог измениться, пока панель была открыта. Подтвержденные галочки
            // применяются лишь к тем адресатам, которые по-прежнему есть в настройке.
            RefreshParticipants();
            _selected.Clear();
            _selected.UnionWith(_participants.Where(p => working.Contains(p.Id)).Select(p => p.Id));
            RefreshSummary();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            close();
        };
        panel.Children.Add(done);
        Filter();
        return panel;
    }

    private void RefreshSummary() => _summary.Text = _participants.Count == 0 ? "Список пуст"
        : _selected.Count == 0 ? "Выберите участников"
        : _selected.Count == _participants.Count ? $"Все участники ({_participants.Count})"
        : $"Выбрано: {_selected.Count} из {_participants.Count}";

    private static T Resource<T>(Control? host, string key)
    {
        if (host is not null && host.TryFindResource(key, out var local) && local is T value) return value;
        // Настройки создают редактор до прикрепления к окну. В этот момент контрол еще
        // не видит ресурсы приложения через логического родителя.
        if (Application.Current is { } app && app.TryFindResource(key, out var resource) && resource is T global)
            return global;
        throw new InvalidOperationException($"Не найден ресурс оформления «{key}».");
    }

    private sealed class RecipientRow(BroadcastRecipient participant, HashSet<string> selected,
                                      Action changed) : INotifyPropertyChanged
    {
        public string Id => participant.Id;
        public string Label => participant.Label;
        public bool Checked
        {
            get => selected.Contains(Id);
            set
            {
                if (value == Checked) return;
                if (value) selected.Add(Id); else selected.Remove(Id);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked)));
                changed();
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
