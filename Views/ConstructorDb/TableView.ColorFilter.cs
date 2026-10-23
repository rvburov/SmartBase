using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SmartBase.Controls.Components;
using SmartBase.Core.Models;

namespace SmartBase.Views.ConstructorDb;

/// <summary>
/// «Сортировка по цвету» и «Фильтр по цвету» в окне фильтра колонки — как в табличных редакторах
/// (условия и их ключи — <see cref="ColorQuery"/>). Обе секции раскрываются по щелчку и показывают
/// цвета, которые есть в колонке, — отдельно цвет ячейки и цвет текста, с числом записей. Выбор,
/// как и сортировка, копится до «Готово». У колонки без цветов секций нет вовсе.
/// </summary>
public partial class TableView
{
    /// <summary>Выбранное в окне фильтра по цвету — до «Готово».</summary>
    private sealed class ColorPick
    {
        /// <summary>Цвет, записи которого встают сверху; null — сортировки по цвету нет.</summary>
        public string? Sort;

        /// <summary>Цвет отбора; null — отбора по цвету нет.</summary>
        public string? Filter;
    }

    /// <summary>Секция окна: заголовок-переключатель и список цветов под ним.</summary>
    private sealed class ColorSection(StackPanel root, Action<IReadOnlyList<(string Token, int Count)>> fill, Action render)
    {
        public StackPanel Root { get; } = root;

        /// <summary>Цвета колонки пришли из фона.</summary>
        public void Fill(IReadOnlyList<(string Token, int Count)> colors) => fill(colors);

        /// <summary>Выбор сменился — перерисовать отметку.</summary>
        public void Render() => render();
    }

    /// <param name="title">«Сортировка по цвету» или «Фильтр по цвету».</param>
    /// <param name="current">Выбранный сейчас цвет секции.</param>
    /// <param name="choose">Щелчок по цвету: выбранный — снимает выбор (null).</param>
    private ColorSection BuildColorSection(string title, Func<string?> current, Action<string?> choose)
    {
        var root = new StackPanel { Spacing = 2, IsVisible = current() is not null };
        var body = new StackPanel { Spacing = 0, IsVisible = current() is not null };
        IReadOnlyList<(string Token, int Count)> colors = [];

        var chevron = new ContentControl();
        var header  = MakeFlatItem(title, () =>
        {
            body.IsVisible = !body.IsVisible;
            UpdateChevron();
        }, Icon("IconPalette"));
        if (header.Content is Grid row)
        {
            Grid.SetColumn(chevron, 2);
            row.Children.Add(chevron);
        }
        void UpdateChevron() =>
            chevron.Content = MakeStrokeIcon(Icon(body.IsVisible ? "IconChevronDown" : "IconChevronRight"),
                                             (IBrush)this.FindResource("BrushSecondaryText")!);
        UpdateChevron();

        root.Children.Add(header);
        root.Children.Add(body);

        void Render()
        {
            body.Children.Clear();
            var chosen = current();
            foreach (var aspect in new[] { StyleAspect.Background, StyleAspect.Text })
            {
                var ofAspect = colors.Where(c => ColorQuery.TryParseToken(c.Token, out var a, out _) && a == aspect).ToList();
                // Выбранный цвет мог уйти из колонки (его перекрасили) — он остается в списке,
                // чтобы выбор было видно и можно было снять.
                if (chosen is not null && ColorQuery.TryParseToken(chosen, out var ca, out _) && ca == aspect
                    && ofAspect.All(c => c.Token != chosen))
                    ofAspect.Add((chosen, 0));
                if (ofAspect.Count == 0) continue;

                body.Children.Add(new TextBlock
                {
                    Text       = aspect == StyleAspect.Text ? "Цвет текста" : "Цвет ячейки",
                    Foreground = (IBrush)this.FindResource("BrushSecondaryText")!,
                    Margin     = new Thickness(32, 4, 0, 2),
                });
                foreach (var (token, count) in ofAspect)
                    body.Children.Add(MakeColorItem(token, count, token == chosen, () => choose(token == current() ? null : token)));
            }
        }

        void Fill(IReadOnlyList<(string Token, int Count)> list)
        {
            colors = list;
            // Секция нужна, только если в колонке есть хоть один цвет — или по цвету уже отобрано.
            root.IsVisible = current() is not null
                          || list.Any(c => ColorQuery.TryParseToken(c.Token, out _, out var hex) && hex is not null);
            Render();
        }

        return new ColorSection(root, Fill, Render);
    }

    // Пункт цвета: образец, имя цвета и число записей справа; выбранный — акцентом и галкой.
    private Button MakeColorItem(string token, int count, bool active, Action onClick)
    {
        var fg = (IBrush)this.FindResource(active ? "BrushAccent" : "BrushPrimaryText")!;
        ColorQuery.TryParseToken(token, out var aspect, out var hex);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        Control sample = aspect == StyleAspect.Text ? ColorMenu.TextSample(hex) : ColorMenu.Swatch(hex);
        sample.Margin = new Thickness(24, 0, 0, 0);
        row.Children.Add(sample);

        var name = new TextBlock
        {
            Text              = ColorQuery.Name(token),
            Foreground        = fg,
            Margin            = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        var cnt = new TextBlock
        {
            Text              = count.ToString("N0"),
            Foreground        = (IBrush)this.FindResource("BrushSecondaryText")!,
            Margin            = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(cnt, 2);
        row.Children.Add(cnt);

        if (active)
        {
            var check = MakeStrokeIcon(CheckIcon, fg);
            check.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(check, 3);
            row.Children.Add(check);
        }

        var btn = new Button
        {
            Content                    = row,
            Background                 = Brushes.Transparent,
            BorderThickness            = new Thickness(0),
            Padding                    = new Thickness(8, 4),
            FontSize                   = (double)this.FindResource("FontSizeBase")!,
            CornerRadius               = (CornerRadius)this.FindResource("RadiusBase")!,
            HorizontalAlignment        = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Cursor                     = new Cursor(StandardCursorType.Hand),
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }
}
