using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using SmartBase.Settings;

namespace SmartBase.Controls.Widgets;

/// <summary>
/// Выпадающий список с галочками: какие пункты показывать, а какие скрыть. Используется в
/// «Оформлении» для видов разделов и модулей панели навигации.
///
/// Внутри — штатный <see cref="ComboBox"/> на общей теме приложения, отличается только
/// шаблон пункта (чекбокс вместо текста). Выбранного значения у списка нет: выбор гасится,
/// а в свёрнутом боксе стоит сводка «Показаны все» / «Показано N из M».
///
/// Главное правило — ХОТЯ БЫ ОДИН ПУНКТ ОСТАЁТСЯ отмеченным: раздел без единого вида (и
/// панель навигации без единого раздела) неработоспособны. У последней галочки чекбокс
/// выключается, а не возвращается «прыжком» после клика — так видно, что пункт снять
/// нельзя, ещё до нажатия.
/// </summary>
public partial class CheckListDropdown : UserControl
{
    private readonly List<CheckListItem> _items = [];

    // Гасит события, пока список наполняется кодом.
    private bool _building;

    /// <summary>Текст в свёрнутом боксе: «Показаны все» / «Показано N из M». Свойство, а не
    /// прямая запись в ComboBox, потому что берут его два места сразу — подсказка и шаблон
    /// выбранного значения (см. разметку).</summary>
    public static readonly StyledProperty<string> SummaryProperty =
        AvaloniaProperty.Register<CheckListDropdown, string>(nameof(Summary), "");

    public string Summary
    {
        get => GetValue(SummaryProperty);
        private set => SetValue(SummaryProperty, value);
    }

    /// <summary>Набор отмеченных изменился; аргумент — ключи СКРЫТЫХ пунктов.</summary>
    public event EventHandler<IReadOnlyList<string>>? HiddenChanged;

    public CheckListDropdown()
    {
        InitializeComponent();

        // Пункт можно только отметить, но не «выбрать»: снимаем выбор сразу. Иначе строка
        // залипала бы подсветкой выбранного, а её текст уезжал бы в свёрнутый бокс вместо
        // сводки.
        Selector.SelectionChanged += (_, _) =>
        {
            if (Selector.SelectedItem is not null) Selector.SelectedItem = null;
        };
    }

    /// <summary>Заполнить список: все пункты раздела и ключи скрытых.</summary>
    public void SetItems(IReadOnlyList<ViewItem> items, IReadOnlyCollection<string> hidden)
    {
        _building = true;

        foreach (var old in _items) old.PropertyChanged -= OnItemChanged;
        _items.Clear();

        foreach (var item in items)
        {
            var row = new CheckListItem(item.Key, item.Label, !hidden.Contains(item.Key));
            row.PropertyChanged += OnItemChanged;
            _items.Add(row);
        }

        Selector.ItemsSource = _items.ToList();

        _building = false;
        UpdateState();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_building || e.PropertyName != nameof(CheckListItem.IsChecked)) return;

        UpdateState();
        HiddenChanged?.Invoke(this, _items.Where(i => !i.IsChecked).Select(i => i.Key).ToList());
    }

    private void UpdateState()
    {
        var total = _items.Count;
        var shown = _items.Count(i => i.IsChecked);

        // Замок на последней галочке; как только отмечен ещё один пункт — снимается со всех.
        foreach (var row in _items) row.CanToggle = !(shown == 1 && row.IsChecked);

        Summary = total == 0     ? "Список пуст"
                : shown == total ? "Показаны все"
                                 : $"Показано {shown} из {total}";
    }
}

/// <summary>Строка списка: ключ пункта, подпись и состояние галочки. Наблюдаемая — галочку
/// ставит привязка шаблона, а <see cref="CanToggle"/> меняет сам контрол, когда отмеченным
/// остаётся один пункт.</summary>
public sealed partial class CheckListItem : ObservableObject
{
    public CheckListItem(string key, string label, bool isChecked)
    {
        Key        = key;
        Label      = label;
        _isChecked = isChecked;
    }

    public string Key   { get; }
    public string Label { get; }

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _canToggle = true;
}
