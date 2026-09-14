using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SmartBase.Controls.Dialogs;
using SmartBase.Core.Models;
using SmartBase.Utils;
using static SmartBase.Controls.Widgets.SettingRows;

namespace SmartBase.Views.Settings;

/// <summary>
/// Раздел «Сочетания клавиш» (partial SettingsView): все действия программы одним списком, у
/// каждого — действующее сочетание. Список собирается кодом из реестра
/// <see cref="ShortcutActions"/>: появилось действие в реестре — появилась строка здесь, без
/// правки разметки.
///
/// Блок стоит приставкой в названии строки («Таблица: Копировать значения ячеек»), а не
/// заголовком над группой: список читается сплошняком, и строка сама говорит, где действует ее
/// клавиша. Пояснений под названием нет — строка это название, сочетание и две кнопки.
///
/// Строки двух видов. Команды переназначаются: справа кнопка «+» (окно захвата) и кнопка
/// сброса к стандарту, активная только пока сочетание отличается от стандартного. Справочные
/// строки (стрелки, Tab, Enter, Escape) показывают клавишу без кнопок: навигация вшита в
/// поведение таблицы, редакторов и окон, и подменять ее нечем.
///
/// Поиск живет в навбаре (NavbarSettings) и отбирает по названию строки — вместе с приставкой
/// блока — и по самому сочетанию: «ctrl+c» находит копирование так же, как слово «таблица».
/// </summary>
public partial class SettingsView
{
    // Строка отбора из навбара. Живет в пределах захода в раздел: это лупа, а не настройка.
    private string _shortcutSearch = "";

    // Подписка на изменения привязок — одна на жизнь экрана (иначе копилась бы на каждый заход).
    private bool _shortcutsSubscribed;

    private void OnShortcutSearchChanged(string text)
    {
        _shortcutSearch = text.Trim();
        if (ShortcutsPanel.IsVisible) RebuildShortcutsPanel();
    }

    private void RebuildShortcutsPanel()
    {
        if (!_shortcutsSubscribed)
        {
            // Переназначение и сброс перестраивают список сами: подписи и кнопка сброса
            // должны меняться сразу, без повторного захода в раздел.
            ShortcutSettings.Changed += () =>
            {
                if (ShortcutsPanel.IsVisible) RebuildShortcutsPanel();
            };
            _shortcutsSubscribed = true;
        }

        var rows = ShortcutsList.Children;
        rows.Clear();

        // Блок — приставка в названии строки, а не заголовок над группой: список читается
        // сплошняком, и строка сама говорит, где действует ее клавиша.
        var shown = ShortcutActions.All.Where(MatchesSearch).ToList();
        if (shown.Count == 0)
        {
            rows.Add(Row("Ничего не найдено",
                "По запросу нет ни одного действия. Попробуйте другое слово или само сочетание — например «ctrl+c»."));
            return;
        }

        for (var i = 0; i < shown.Count; i++)
            Add(rows, ActionRow(shown[i]), last: i == shown.Count - 1);
    }

    private bool MatchesSearch(ShortcutAction action)
    {
        if (_shortcutSearch.Length == 0) return true;

        var haystack = ShortcutActions.FullTitle(action) + " " + GestureLabel(action);
        return haystack.Contains(_shortcutSearch, StringComparison.CurrentCultureIgnoreCase);
    }

    // Подпись сочетания одной строкой — она же участвует в поиске.
    private static string GestureLabel(ShortcutAction action)
    {
        if (action.FixedLabel is { Length: > 0 } label) return label;

        var gestures = ShortcutSettings.GesturesFor(action.Id);
        return string.Join(", ", gestures.Select(g => g.Display()));
    }

    private Border ActionRow(ShortcutAction action)
    {
        var text = new TextBlock
        {
            Classes           = { "title" },
            Text              = ShortcutActions.FullTitle(action),
            TextWrapping      = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 32, 0),
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var keys = GestureKeys(action);
        Grid.SetColumn(keys, 1);
        grid.Children.Add(keys);

        if (action.Rebindable)
        {
            var reset = IconButton("IconRotateCcw", "Сбросить к стандартному",
                                   () => ShortcutSettings.Reset(action.Id));
            reset.IsEnabled = ShortcutSettings.IsCustomized(action.Id);
            reset.Margin    = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(reset, 2);
            grid.Children.Add(reset);

            var assign = IconButton("IconPlus", "Назначить сочетание", () => _ = AssignShortcut(action));
            assign.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(assign, 3);
            grid.Children.Add(assign);
        }

        return new Border { Classes = { "settingRow" }, Child = grid };
    }

    // Сочетание плашками по клавишам: «Ctrl» «Shift» «K». Составные справочные подписи
    // («Ctrl+Стрелки») плашками не разбираются — они и не жест, а описание.
    private static Control GestureKeys(ShortcutAction action)
    {
        var line = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            Spacing             = 4,
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        if (action.FixedLabel is { Length: > 0 } label)
        {
            line.Children.Add(KeyCap(label));
            return line;
        }

        if (ShortcutSettings.PrimaryGesture(action.Id) is not { } gesture) return line;

        foreach (var part in gesture.DisplayParts(OperatingSystem.IsMacOS()))
            line.Children.Add(KeyCap(part));

        return line;
    }

    private static Border KeyCap(string text) => new()
    {
        Background      = Ink("BrushSecondaryBg", "#F2F2F7"),
        BorderBrush     = Ink("BrushSeparator", "#E5E5EA"),
        BorderThickness = Line(),
        CornerRadius    = Radius("RadiusBase", 6),
        Padding         = new Thickness(8, 3),
        Child = new TextBlock
        {
            Text       = text,
            FontSize   = Metric("FontSizeSmall", 10),
            Foreground = Ink("BrushPrimaryText", "#1C1C1E"),
        },
    };

    // Кнопка-иконка строки — тот же вид, что у кнопок навбара (navbtn 30×30), иконка
    // размера настроек (16) с парной толщиной обводки.
    private static Button IconButton(string iconKey, string tip, Action click)
    {
        var button = new Button
        {
            Classes           = { "navbtn" },
            Width             = 30,
            Height            = 30,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius      = Radius("RadiusMedium", 8),
            Content = new Viewbox
            {
                Width  = Metric("IconSizeSmall", 16),
                Height = Metric("IconSizeSmall", 16),
                Child  = new Canvas
                {
                    Width  = 24,
                    Height = 24,
                    Children =
                    {
                        new Avalonia.Controls.Shapes.Path
                        {
                            Data            = Icon(iconKey),
                            Stroke          = Ink("BrushPrimaryText", "#1C1C1E"),
                            StrokeThickness = Metric("IconStrokeSmall", 1.8),
                            StrokeLineCap   = PenLineCap.Round,
                            StrokeJoin      = PenLineJoin.Round,
                        },
                    },
                },
            },
        };

        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    // Толщина линии — общий токен BorderLine (он объявлен Thickness, не числом).
    private static Thickness Line() =>
        Application.Current!.TryFindResource("BorderLine", out var found) && found is Thickness line
            ? line
            : new Thickness(1);

    private static Geometry? Icon(string key) =>
        Application.Current!.TryFindResource(key, out var found) ? found as Geometry : null;

    private async Task AssignShortcut(ShortcutAction action)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var dialog = new ShortcutCaptureDialog(action);
        if (await dialog.ShowDialog<ShortcutGesture?>(owner) is { } gesture)
            ShortcutSettings.SetGesture(action.Id, gesture);
    }
}
