using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SmartBase.Settings;
using SmartBase.Utils;

namespace SmartBase.Controls.Components;

/// <summary>
/// Навбар раздела «Библиотека документов»: заголовок слева, фильтр типов у правого края.
///
/// Строки поиска здесь нет намеренно: она стоит блоком над самой лентой, на месте строки
/// быстрого добавления задач. Деление по охвату — навбар держит то, что относится к разделу
/// целиком, а поиск переписывает ленту и потому живет над ней.
///
/// Переключателя вида тоже нет: вид у раздела один, и выпадашка на один пункт — это орган
/// управления, который ничем не управляет. Вместе с ним у раздела нет настроек «Отображение по
/// умолчанию» и «Отображаемые виды»: нечем управлять — нечего и настраивать. Появится второй
/// вид — вернутся все трое разом (список видов по-прежнему лежит в <c>ViewCatalog.Library</c>).
///
/// Выпадашка типов не своя, а общий <see cref="ColumnFilterPanel"/> — тот же чеклист с поиском
/// значений, количествами справа и кнопкой «Готово», что у колонки таблицы. Своя копия этого
/// списка разъехалась бы с оригиналом по мелочам (правила галочек при поиске, «(Пусто)»
/// последним, ширина строки под прокруткой), а чинить пришлось бы дважды.
///
/// Сортировки в выпадашке нет (<c>Sortable = false</c>): порядок ленты задает коллекция, а не
/// тип документа. Показать «От А до Я» и ничего не переставить было бы хуже, чем не показывать.
/// </summary>
public partial class NavbarLibrary : UserControl
{
    /// <summary>Набор выбранных типов документа изменился; null — фильтр снят (все типы).</summary>
    public event EventHandler<IReadOnlySet<string>?>? TypesChanged;

    /// <summary>Откуда брать значения и количества для выпадашки — ставит контроллер раздела.</summary>
    public Func<Task<List<(string Value, int Count)>>>? LoadTypes { get; set; }

    /// <summary>Куда сказать о сбое чтения значений — у раздела свой журнал.</summary>
    public Action<Exception>? LoadTypesFailed { get; set; }

    private IReadOnlySet<string>? _types;

    public NavbarLibrary()
    {
        InitializeComponent();
        TypeBtn.Click += (_, _) => ShowTypes();
    }

    public void SetTitle(string text, IBrush? color = null)
    {
        TitleLabel.Text       = text;
        TitleLabel.Foreground = color ?? (IBrush)Application.Current!.FindResource("BrushPrimaryText")!;
    }

    /// <summary>Выбранные типы; null — фильтр снят.</summary>
    public IReadOnlySet<string>? Types => _types;

    /// <summary>
    /// Сбросить фильтр типов (например, когда сменился набор отмеченных коллекций и прежние
    /// типы к нему не относятся). Событие не поднимает: зовущий и так перечитывает ленту.
    /// </summary>
    public void ResetTypes()
    {
        _types = null;
        UpdateTypeLabel();
    }

    private void ShowTypes()
    {
        if (LoadTypes is null) return;

        ColumnFilterPanel.Show(this, TypeBtn, new ColumnFilterSpec
        {
            Key        = "docType",
            AscLabel   = "",
            DescLabel  = "",
            Sortable   = false,
            Filter     = _types,
            // Пустой тип показывается как «(Пусто)» — то же слово, что в фильтре колонки
            // таблицы: документ без проставленного типа должен быть отмечаемым, а не безымянным.
            Display    = raw => raw.Length == 0 ? "(Пусто)" : raw,
            LoadValues = LoadTypes,
            LoadFailed = LoadTypesFailed,
            Apply      = result =>
            {
                if (!result.FilterKnown) return;
                _types = result.Filter;
                UpdateTypeLabel();
                TypesChanged?.Invoke(this, _types);
            },
        });
    }

    // Подпись кнопки говорит состояние фильтра: снят — название, сужен — сколько типов выбрано.
    // Перечислять сами типы не пытаемся: их бывает десяток, и строка все равно схлопнулась бы.
    private void UpdateTypeLabel() =>
        TypeLabel.Text = _types is null
            ? "Тип документа"
            : _types.Count == 0
                ? "Тип документа: ничего"
                : $"Тип документа: {_types.Count}";
}
