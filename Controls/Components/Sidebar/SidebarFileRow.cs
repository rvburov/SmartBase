using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SmartBase.Core.Models;
using SmartBase.Core.Notes;

namespace SmartBase.Controls.Components;

/// <summary>
/// Строка дерева заметок. Копия <c>SidebarRow</c> конструктора в той части, которая задает ВИД:
/// шаг отступа, слоты, высота контента и линия-разделитель. Отличия — только в данных: ключ узла
/// строковый, чекбоксов и статусов очереди тут нет.
///
/// Слот чекбокса оставлен нулевой ширины, а не выброшен: на нем стоит та же формула ширины строки
/// (<c>SidebarFiles.MeasureRowWidth</c>), что и в конструкторе, — правка отступов там переносится
/// сюда механически.
/// </summary>
public partial class SidebarFileRow : ObservableObject
{
    private const double IndentStep = 22;

    // Кисти из общей палитры Colors.axaml (запасной hex — на случай отсутствия ресурса).
    private static readonly IBrush Accent      = Brush("BrushAccent",        "#007AFF");
    private static readonly IBrush AccentLight = Brush("BrushAccentLight",   "#E5F0FF");
    private static readonly IBrush Hover       = Brush("BrushSecondaryBg",   "#F2F2F7");
    private static readonly IBrush Secondary   = Brush("BrushSecondaryText", "#8E8E93");
    private static readonly IBrush Primary     = Brush("BrushPrimaryText",   "#1C1C1E");

    // Геометрия иконки из общего словаря Assets/Styles/Icons.axaml по ключу x:Key.
    private static Geometry? Icon(string key) =>
        Application.Current!.TryFindResource(key, out var res) && res is Geometry g ? g : null;

    private static IBrush Brush(string key, string fallbackHex) =>
        Application.Current!.TryFindResource(key, out var res) && res is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallbackHex));

    public NoteNode Node  { get; }
    public int      Depth { get; }

    /// <summary>Показываем первую строку имени — как в дереве конструктора. Полное имя живет в
    /// <c>Node.Name</c>: по нему идут сортировка и операции.</summary>
    public string Name => TitleText.FirstLine(Node.Name);

    [ObservableProperty] private bool _hovered;
    [ObservableProperty] private bool _selected;

    // Ширина, ОДИНАКОВАЯ у всех строк списка (ее считает SidebarFiles.MeasureRowWidth). Нужна
    // из-за виртуализации: экстент горизонтальной прокрутки панель считает по РЕАЛИЗОВАННЫМ
    // строкам, и без общей ширины полоса дергалась бы при вертикальной прокрутке. Заодно
    // подсветка строки идет во всю ширину содержимого, а не до края видимой части.
    [ObservableProperty] private double _minRowWidth;

    public SidebarFileRow(NoteNode node, int depth, bool selected)
    {
        Node      = node;
        Depth     = depth;
        _selected = selected;
    }

    public void SetHovered(bool v) => Hovered = v;

    // Строка повторяет СТРУКТУРУ строки таблицы: контент = высота ячейки (RowHeightStandard = 36),
    // а линия-разделитель ДОБАВЛЯЕТСЯ снизу отдельным элементом толщиной SeparatorThickness (тот же
    // зум-компенсированный токен, что у линий сетки таблицы = ровно 1 физ. пиксель). ПЕРВАЯ строка
    // сайдбара стоит на месте ШАПКИ колонок: высота ровно 36, линия лежит поверх контента.
    private bool _isFirstRow;
    public bool IsFirstRow
    {
        get => _isFirstRow;
        set
        {
            if (_isFirstRow == value) return;
            _isFirstRow = value;
            OnPropertyChanged(nameof(LineGridRow));
            OnPropertyChanged(nameof(LineRowHeight));
        }
    }

    public double     RowContentHeight => CellHeight;
    public int        LineGridRow      => IsFirstRow ? 0 : 1;
    public GridLength LineRowHeight    => IsFirstRow ? new GridLength(0) : GridLength.Auto;

    private static double CellHeight =>
        Application.Current!.TryFindResource("RowHeightStandard", out var v) && v is double d ? d : 36;

    public Thickness IndentMargin => new(Depth * IndentStep, 0, 0, 0);

    /// <summary>Слот стрелки: у папок занят, у файлов пуст — имена файлов встают ровно под
    /// именами папок того же уровня.</summary>
    public double ChevronSlot => Node.IsFolder ? IndentStep : 0;

    /// <summary>Слота чекбокса в заметках нет — он есть только в календаре и задачах. Ширина
    /// нулевая, но колонка остается: формула ширины строки общая с конструктором.</summary>
    public double CheckboxSlot => 0;

    public bool      ShowChevron     => Node.IsFolder;
    public Geometry? ChevronGeometry => Node.IsExpanded ? Icon("IconChevronDown") : Icon("IconChevronRight");

    public Geometry? IconGeometry =>
        Node.IsFolder     ? Icon("IconFolder")
        : Node.IsMarkdown ? Icon("IconFileText")
        :                   Icon("IconFile");

    public IBrush IconBrush => Selected ? Accent : Secondary;

    /// <summary>Имя файла, который открыть нельзя, приглушено — видно, что строка «неживая»,
    /// еще до клика по ней.</summary>
    public IBrush NameBrush =>
        Selected                             ? Accent
        : !Node.IsFolder && !Node.IsMarkdown ? Secondary
        :                                      Primary;

    public IBrush RowBackground =>
        Selected ? AccentLight :
        Hovered  ? Hover       : Brushes.Transparent;

    partial void OnHoveredChanged(bool value) => OnPropertyChanged(nameof(RowBackground));

    partial void OnSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(RowBackground));
        OnPropertyChanged(nameof(IconBrush));
        OnPropertyChanged(nameof(NameBrush));
    }

    /// <summary>Стрелка перерисовывается отдельно: раскрытие меняет узел, а не строку.</summary>
    public void NotifyExpanded() => OnPropertyChanged(nameof(ChevronGeometry));
}
