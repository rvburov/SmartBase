using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace SmartBase.Utils;

/// <summary>
/// Внешняя полоса прокрутки для <see cref="ScrollViewer"/> — та, что лежит НЕ внутри его
/// шаблона, а отдельной колонкой родительской сетки и потому может захватывать строки
/// выше прокручиваемой области (фиксированную шапку).
///
/// Зачем: стандарт проекта — полоса начинается у ВЕРХНЕГО края блока, как в таблице
/// конструктора (там вертикальная полоса стоит на уровне шапки колонок, см.
/// TableViewDataGridThemes.axaml, «Отличие 5»). Собственная полоса ScrollViewer'а живёт
/// внутри его шаблона и выше его границ подняться не может, поэтому в местах с
/// фиксированной шапкой (список задач, диаграмма, попап фильтра) полоса выносится наружу:
///   ScrollViewer  — VerticalScrollBarVisibility="Hidden" (прокрутка работает, своей
///                   полосы нет), колонка 0;
///   ScrollBar     — колонка 1 с Grid.RowSpan на шапку + тело, ScrollBarLink.Source
///                   указывает на ScrollViewer.
///
/// Диапазон, шаги стрелок и постраничный шаг берутся у самого ScrollViewer
/// (SmallChange/LargeChange) — прокрутка стрелками и кликом по дорожке ведёт себя ровно
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
    /// Готовая связка «закреплённая шапка + список» с полосой на всю высоту — для попапов,
    /// собираемых в C# (фильтр колонки таблицы, истории профиля, журнала логов). Полоса
    /// списка выносится из его шаблона в отдельную колонку и встаёт на уровень шапки, как
    /// вертикальная полоса таблицы стоит на уровне шапки колонок.
    /// ВНИМАНИЕ: у <paramref name="header"/> и <paramref name="list"/> выставляется правый
    /// Margin — воздух между текстом и полосой (в таблице его даёт отступ ячейки).
    /// </summary>
    public static Control HeaderedList(Control header, ListBox list, double gap = 4, double air = 8)
    {
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Hidden);
        header.Margin = new Thickness(0, 0, air, 0);
        list.Margin   = new Thickness(0, 0, air, 0);

        var bar = new ScrollBar { Orientation = Orientation.Vertical, IsVisible = false };
        // Полоса ссылается на ScrollViewer ВНУТРИ шаблона списка — он появляется только
        // после применения шаблона.
        list.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<ScrollViewer>("PART_ScrollViewer") is { } sv) SetSource(bar, sv);
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            // Инвариантный формат обязателен: в ru-культуре дробное число ушло бы с
            // запятой и строка определений разъехалась бы на лишние строки.
            RowDefinitions    = new RowDefinitions(FormattableString.Invariant($"Auto,{gap},*")),
        };
        Grid.SetRow(list, 2);
        Grid.SetColumn(bar, 1);
        Grid.SetRowSpan(bar, 3);
        grid.Children.Add(header);
        grid.Children.Add(list);
        grid.Children.Add(bar);
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

    /// <summary>Отписка как IDisposable: System.Reactive в проекте не подключён.</summary>
    private sealed class Unsubscribe(Action off) : IDisposable
    {
        public void Dispose() => off();
    }
}
