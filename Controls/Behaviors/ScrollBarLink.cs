using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace SmartBase.Controls.Behaviors;

/// <summary>
/// Внешняя полоса прокрутки для <see cref="ScrollViewer"/> — та, что лежит НЕ внутри его
/// шаблона, а отдельной колонкой родительской сетки и потому может захватывать строки
/// выше прокручиваемой области (фиксированную шапку).
///
/// Зачем: стандарт проекта — полоса начинается у ВЕРХНЕГО края блока, как в таблице
/// конструктора (там вертикальная полоса стоит на уровне шапки колонок, см.
/// TableViewDataGridThemes.axaml, «Отличие 5»). Собственная полоса ScrollViewer'а живет
/// внутри его шаблона и выше его границ подняться не может, поэтому в местах с
/// фиксированной шапкой (список задач, диаграмма, попап фильтра) полоса выносится наружу:
///   ScrollViewer  — VerticalScrollBarVisibility="Hidden" (прокрутка работает, своей
///                   полосы нет), колонка 0;
///   ScrollBar     — колонка 1 с Grid.RowSpan на шапку + тело, ScrollBarLink.Source
///                   указывает на ScrollViewer.
///
/// Диапазон, шаги стрелок и постраничный шаг берутся у самого ScrollViewer
/// (SmallChange/LargeChange) — прокрутка стрелками и кликом по дорожке ведет себя ровно
/// так же, как у встроенной полосы. Полоса прячется, когда прокручивать нечего, — колонка
/// Auto схлопывается и шапка занимает всю ширину.
/// </summary>
public static class ScrollBarLink
{
    /// <summary>Прокручиваемая область, которой управляет эта полоса.</summary>
    public static readonly AttachedProperty<ScrollViewer?> SourceProperty =
        AvaloniaProperty.RegisterAttached<ScrollBar, ScrollViewer?>("Source", typeof(ScrollBarLink));

    public static ScrollViewer? GetSource(ScrollBar b)          => b.GetValue(SourceProperty);
    public static void SetSource(ScrollBar b, ScrollViewer? v)  => b.SetValue(SourceProperty, v);

    /// <summary>Подписки текущей связи — чтобы повторное присвоение Source не плодило их.</summary>
    private static readonly AttachedProperty<IDisposable?> LinkProperty =
        AvaloniaProperty.RegisterAttached<ScrollBar, IDisposable?>("Link", typeof(ScrollBarLink));

    static ScrollBarLink()
    {
        SourceProperty.Changed.AddClassHandler<ScrollBar>((bar, e) =>
        {
            bar.GetValue(LinkProperty)?.Dispose();
            bar.SetValue(LinkProperty, e.NewValue is ScrollViewer sv ? Attach(bar, sv) : null);
        });
    }

    /// <summary>
    /// Готовая связка «закрепленная шапка + список» с обеими полосами по краям — для попапов,
    /// собираемых в C# (фильтр колонки таблицы, истории профиля, журнала логов). Полосы
    /// списка выносятся из его шаблона в отдельные колонку и строку сетки: вертикальная
    /// встает на уровень шапки, горизонтальная дает прочитать длинные значения целиком
    /// (без нее они обрезались многоточием). Раскладка повторяет таблицу конструктора,
    /// включая квадрат на пересечении полос.
    ///
    /// ВНИМАНИЕ: у карточки-контейнера <c>Padding</c> должен быть НУЛЕВОЙ — иначе полосы
    /// отойдут от ее краев. Весь воздух задает <paramref name="padding"/>: он вешается
    /// Margin'ом на шапку и список, то есть на содержимое ВНУТРИ полос.
    /// </summary>
    /// <param name="scroller">Вызывается, когда у списка появится его внутренний
    /// <see cref="ScrollViewer"/> — например, чтобы следить за прокруткой.</param>
    public static Control HeaderedList(Control header, ListBox list, double gap = 4,
                                       Thickness? padding = null, Action<ScrollViewer>? scroller = null)
    {
        // Прокрутка у списка работает, своих полос он не рисует — их роль играют вынесенные.
        // Горизонтальная включена намеренно: длинные значения перестают обрезаться
        // многоточием, их можно домотать полосой снизу.
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Hidden);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Hidden);

        return Build(header, list, padding, gap, (vbar, hbar) =>
            // Полосы ссылаются на ScrollViewer ВНУТРИ шаблона списка — он появляется
            // только после применения шаблона.
            list.TemplateApplied += (_, e) =>
            {
                if (e.NameScope.Find<ScrollViewer>("PART_ScrollViewer") is not { } sv) return;
                SetSource(vbar, sv);
                SetSource(hbar, sv);
                scroller?.Invoke(sv);
            });
    }

    /// <summary>
    /// То же для случая, когда прокручиваемая область — обычный <see cref="ScrollViewer"/>
    /// (пикер коллекций, попапы свойств сайдбара): полоса берется у него напрямую.
    /// Горизонтальную прокрутку метод НЕ включает — остается та, что задал вызывающий.
    /// </summary>
    public static Control HeaderedList(Control header, ScrollViewer scroll, double gap = 4, Thickness? padding = null)
    {
        scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;

        return Build(header, scroll, padding, gap, (vbar, hbar) =>
        {
            SetSource(vbar, scroll);
            SetSource(hbar, scroll);
        });
    }

    private static Control Build(Control header, Control body, Thickness? padding, double gap,
                                 Action<ScrollBar, ScrollBar> link)
    {
        var pad = padding ?? new Thickness(8, 6);
        header.Margin = new Thickness(pad.Left, pad.Top, pad.Right, 0);
        body.Margin   = new Thickness(pad.Left, 0, pad.Right, pad.Bottom);

        var vbar = new ScrollBar { Orientation = Orientation.Vertical,   IsVisible = false };
        var hbar = new ScrollBar { Orientation = Orientation.Horizontal, IsVisible = false };
        link(vbar, hbar);

        // Квадрат на пересечении полос — иначе там белое пятно (в таблице то же делает
        // PART_BottomRightCorner). Схлопывается сам вместе со скрытой полосой.
        var corner = new Border();
        corner.Bind(Border.BackgroundProperty, corner.GetResourceObservable("BrushScrollbarTrack"));

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            // Инвариантный формат обязателен: в ru-культуре дробное число ушло бы с
            // запятой и строка определений разъехалась бы на лишние строки.
            RowDefinitions    = new RowDefinitions(FormattableString.Invariant($"Auto,{gap},*,Auto")),
        };
        Grid.SetRow(body, 2);
        Grid.SetColumn(vbar, 1); Grid.SetRowSpan(vbar, 3);
        Grid.SetRow(hbar, 3);
        Grid.SetRow(corner, 3); Grid.SetColumn(corner, 1);
        grid.Children.Add(header);
        grid.Children.Add(body);
        grid.Children.Add(vbar);
        grid.Children.Add(hbar);
        grid.Children.Add(corner);
        return grid;
    }

    private static IDisposable Attach(ScrollBar bar, ScrollViewer viewer)
    {
        var horizontal = bar.Orientation == Orientation.Horizontal;
        // Полоса и область синхронизируются в обе стороны; флаг гасит эхо: Pull меняет
        // Value полосы, что иначе тут же вернулось бы в Offset области.
        var syncing = false;

        void Pull()
        {
            var extent   = horizontal ? viewer.Extent.Width   : viewer.Extent.Height;
            var viewport = horizontal ? viewer.Viewport.Width : viewer.Viewport.Height;
            var offset   = horizontal ? viewer.Offset.X       : viewer.Offset.Y;
            var max      = Math.Max(0, extent - viewport);

            syncing = true;
            bar.Maximum      = max;
            bar.ViewportSize = viewport;
            bar.SmallChange  = horizontal ? viewer.SmallChange.Width : viewer.SmallChange.Height;
            bar.LargeChange  = horizontal ? viewer.LargeChange.Width : viewer.LargeChange.Height;
            bar.Value        = Math.Clamp(offset, 0, max);
            syncing = false;

            bar.IsVisible = max > 0.5;
        }

        void Push()
        {
            if (syncing) return;
            viewer.Offset = horizontal
                ? viewer.Offset.WithX(bar.Value)
                : viewer.Offset.WithY(bar.Value);
        }

        void OnViewerChanged(object? _, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ScrollViewer.OffsetProperty ||
                e.Property == ScrollViewer.ExtentProperty ||
                e.Property == ScrollViewer.ViewportProperty ||
                e.Property == ScrollViewer.SmallChangeProperty ||
                e.Property == ScrollViewer.LargeChangeProperty)
                Pull();
        }

        void OnBarChanged(object? _, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == RangeBase.ValueProperty) Push();
        }

        viewer.PropertyChanged += OnViewerChanged;
        bar.PropertyChanged    += OnBarChanged;
        Pull();

        return new Unsubscribe(() =>
        {
            viewer.PropertyChanged -= OnViewerChanged;
            bar.PropertyChanged    -= OnBarChanged;
        });
    }

    /// <summary>Отписка как IDisposable: System.Reactive в проекте не подключен.</summary>
    private sealed class Unsubscribe(Action off) : IDisposable
    {
        public void Dispose() => off();
    }
}
