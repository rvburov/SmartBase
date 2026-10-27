using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using SmartBase.Controls.Behaviors;
using SmartBase.Controls.Components;
using SmartBase.Core.Models;

namespace SmartBase.Views.ConstructorDb;

/// <summary>
/// Отбор по видимому цвету ячейки: подменю справа, несколько галочек фона и текста.
/// Выбор копится до «Готово», как отбор значений; цвета не меняют порядок записей.
/// </summary>
public partial class TableView
{
    // Для стенда: последнее открытое подменю цветов, отдельно от основного окна фильтра.
    internal Flyout? LastOpenedColorFilter { get; private set; }

    private sealed class ColorSection(StackPanel root, Action<IReadOnlyList<(string Token, int Count)>> fill,
                                      Action loading, Action failed, Action close)
    {
        public StackPanel Root { get; } = root;
        public void Fill(IReadOnlyList<(string Token, int Count)> colors) => fill(colors);
        public void Loading() => loading();
        public void Failed() => failed();
        public void Close() => close();
    }

    private ColorSection BuildColorSection(HashSet<string> chosen, Action retry)
    {
        // Пункт не исчезает ни до загрузки, ни у колонки без заданных цветов.
        var root = new StackPanel();
        var body = new StackPanel { Spacing = 2 };
        IReadOnlyList<(string Token, int Count)> colors = [];
        var loaded = false;
        var failed = false;
        var retryButton = MakeFlatItem("Повторить", retry);

        var flyout = PopupPlate.Filter();
        flyout.Placement = PlacementMode.RightEdgeAlignedTop;
        flyout.HorizontalOffset = 6;
        // Нажатие «Готово» в родительском окне сразу применяет выбор, даже если подменю открыто.
        flyout.OverlayDismissEventPassThrough = true;

        void Render()
        {
            body.Children.Clear();
            retryButton.IsVisible = failed;
            if (!loaded || (colors.Count == 0 && chosen.Count == 0))
                body.Children.Add(new TextBlock
                {
                    Text = failed ? "Не удалось загрузить цвета" : loaded ? "Список пуст" : "Загрузка…",
                    Foreground = (IBrush)this.FindResource("BrushSecondaryText")!,
                    Margin = new Thickness(8, 4),
                    TextWrapping = TextWrapping.Wrap,
                });
            foreach (var aspect in new[] { StyleAspect.Background, StyleAspect.Text })
            {
                var ofAspect = colors.Where(c => ColorQuery.TryParseToken(c.Token, out var a, out _) && a == aspect).ToList();
                // Исчезнувший цвет остается с нулевым счетчиком: его галочку можно снять.
                foreach (var token in chosen)
                    if (ColorQuery.TryParseToken(token, out var a, out _) && a == aspect
                        && ofAspect.All(c => c.Token != token))
                        ofAspect.Add((token, 0));
                if (ofAspect.Count == 0) continue;

                body.Children.Add(new TextBlock
                {
                    Text       = aspect == StyleAspect.Text ? "Цвет текста" : "Цвет ячейки",
                    Foreground = (IBrush)this.FindResource("BrushSecondaryText")!,
                    Margin     = new Thickness(8, 4, 0, 2),
                });
                foreach (var (token, count) in ofAspect)
                    body.Children.Add(MakeColorItem(token, loaded ? count : null, chosen));
            }
        }

        var content = new StackPanel
        {
            Width   = (double)this.FindResource("PopupContentWidth")!,
            Margin  = new Thickness(12),
            Spacing = 2,
            Children =
            {
                new ScrollViewer
                {
                    Content = body,
                    MaxHeight = (double)this.FindResource("PopupListHeight")!,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                },
                retryButton,
                MakeSeparator(),
                MakeFlatItem("Снять фильтр по цвету", () => { chosen.Clear(); Render(); }, Icon("IconClose")),
            },
        };
        flyout.Content = WindowZoom.WrapPopupContent(content);

        Button? header = null;
        header = MakeFlatItem("Фильтр по цвету", () =>
        {
            if (flyout.IsOpen) { flyout.Hide(); return; }
            LastOpenedColorFilter?.Hide();
            LastOpenedColorFilter = flyout;
            var anchor = header!;
            // Якорь внутри полей основной карточки. Выносим подменю за ее край,
            // учитывая фактическую ширину и зум, а не фиксированный отступ полей.
            if (anchor.GetVisualAncestors().OfType<FlyoutPresenter>().FirstOrDefault() is { } plate
                && anchor.TranslatePoint(new Point(anchor.Bounds.Width, 0), plate) is { } right)
                flyout.HorizontalOffset = 6 + Math.Max(0, plate.Bounds.Width - right.X);
            TransientPopups.Track(flyout);
            flyout.ShowAt(anchor);
        }, Icon("IconPalette"));
        if (header.Content is Grid row)
        {
            var chevron = MakeStrokeIcon(Icon("IconChevronRight"), (IBrush)this.FindResource("BrushSecondaryText")!);
            Grid.SetColumn(chevron, 2);
            row.Children.Add(chevron);
        }
        root.Children.Add(header);

        void Fill(IReadOnlyList<(string Token, int Count)> list)
        {
            colors = list;
            loaded = true;
            failed = false;
            Render();
        }

        Render();
        return new ColorSection(root, Fill,
            () => { loaded = false; failed = false; Render(); },
            () => { loaded = false; failed = true; Render(); }, flyout.Hide);
    }

    private Grid MakeColorItem(string token, int? count, HashSet<string> chosen)
    {
        ColorQuery.TryParseToken(token, out var aspect, out var hex);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Control sample = aspect == StyleAspect.Text ? ColorMenu.TextSample(hex) : ColorMenu.Swatch(hex);
        row.Children.Add(sample);

        var name = new TextBlock
        {
            Text              = ColorQuery.Name(token),
            Foreground        = (IBrush)this.FindResource("BrushPrimaryText")!,
            Margin            = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        var cnt = new TextBlock
        {
            Text              = count?.ToString("N0") ?? "…",
            Foreground        = (IBrush)this.FindResource("BrushSecondaryText")!,
            Margin            = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var check = new CheckBox
        {
            Content                    = row,
            Tag                        = token,
            IsChecked                  = chosen.Contains(token),
            FontSize                   = (double)this.FindResource("FontSizeBase")!,
            HorizontalAlignment        = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Cursor                     = new Cursor(StandardCursorType.Hand),
        };
        check.IsCheckedChanged += (_, _) =>
        {
            if (check.IsChecked == true) chosen.Add(token);
            else chosen.Remove(token);
        };
        // Счетчик вне Content чекбокса: тема измеряет его по тексту, а числа должны
        // стоять у правого края карточки независимо от длины названия цвета.
        var result = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        result.Children.Add(check);
        Grid.SetColumn(cnt, 1);
        result.Children.Add(cnt);
        return result;
    }
}
