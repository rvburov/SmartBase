using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SmartBase.Core;
using SmartBase.Controls.Behaviors;

namespace SmartBase.Controls.Components;

/// <summary>
/// Панель идущих фоновых работ — всплывает у значка в панели разделов.
///
/// Строится в C#, а не в XAML, по той же причине, что карточки ленты: размеры и кисти берутся
/// из ресурсов и должны переживать смену масштаба шрифта, а содержимое меняется на ходу.
///
/// Оформление — как у всплывашки фильтра колонки (<see cref="ColumnFilterPanel"/>): белая
/// плашка с рамкой BrushSeparator и скруглением RadiusBase. Своего вида не заводим: двум
/// всплывающим панелям в одном приложении незачем выглядеть по-разному.
/// </summary>
public static class BackgroundJobsPanel
{
    private static Popup? _popup;
    private static StackPanel? _host;

    /// <summary>Показать панель у якоря; повторный вызов при открытой панели закрывает ее.</summary>
    public static void Show(Visual owner, Control anchor)
    {
        if (_popup is { IsOpen: true }) { Close(); return; }

        _host = new StackPanel();

        var card = new Border
        {
            Width           = 330,
            Background      = Brushes.White,
            BorderBrush     = Res<IBrush>(owner, "BrushSeparator", Brushes.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius    = Res(owner, "RadiusBase", new CornerRadius(6)),
            ClipToBounds    = true,
            Child           = _host,
        };

        _popup = new Popup
        {
            Child                 = WindowZoom.WrapPopupContent(card),
            PlacementTarget       = anchor,
            Placement             = PlacementMode.RightEdgeAlignedBottom,
            HorizontalOffset      = 8,
            IsLightDismissEnabled = true,
            // Всплывашка рендерится вне зума приложения — содержимое в зум-обертке, как у
            // остальных попапов (см. Controls/Behaviors/WindowZoom.cs).
        };

        ((ISetLogicalParent)_popup).SetParent(anchor);
        _popup.Closed += (_, _) => Detach();

        BackgroundJobs.Changed += OnJobsChanged;
        Rebuild(owner);
        _popup.IsOpen = true;
    }

    /// <summary>Закрыть панель, если она открыта, — например когда работ не осталось.</summary>
    public static void Close()
    {
        if (_popup is { IsOpen: true }) _popup.IsOpen = false;
        else Detach();
    }

    private static void Detach()
    {
        BackgroundJobs.Changed -= OnJobsChanged;
        _popup = null;
        _host  = null;
    }

    private static void OnJobsChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_popup is not { IsOpen: true } || _host is null) return;
        if (!BackgroundJobs.Any) { Close(); return; }
        Rebuild(_host);
    });

    private static void Rebuild(Visual owner)
    {
        if (_host is null) return;

        var primary   = Res<IBrush>(owner, "BrushPrimaryText",   Brushes.Black);
        var secondary = Res<IBrush>(owner, "BrushSecondaryText", Brushes.Gray);
        var separator = Res<IBrush>(owner, "BrushSeparator",     Brushes.LightGray);
        var accent    = Res<IBrush>(owner, "BrushAccent",        Brushes.DodgerBlue);
        var red       = Res<IBrush>(owner, "BrushRed",           Brushes.Red);
        var bg2       = Res<IBrush>(owner, "BrushSecondaryBg",   Brushes.WhiteSmoke);
        var bold      = Res(owner, "FontWeightBold", FontWeight.SemiBold);
        var small     = Res(owner, "FontSizeSmall",  10.0);

        _host.Children.Clear();

        _host.Children.Add(new Border
        {
            Padding         = new Thickness(12, 10, 12, 8),
            BorderBrush     = separator,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new TextBlock { Text = "Фоновые работы", FontWeight = bold, Foreground = primary },
        });

        var jobs = BackgroundJobs.All;
        for (var i = 0; i < jobs.Count; i++)
        {
            var job  = jobs[i];
            var rows = new StackPanel { Spacing = 2 };

            var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto") };
            var name = new TextBlock
            {
                Text         = job.Title,
                FontWeight   = bold,
                Foreground   = job.Failed ? red : primary,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(name, 0);
            top.Children.Add(name);

            // Ссылка справа — кликабельная строка, а не кнопка: панель узкая, и кнопка в ней
            // перетянула бы на себя все внимание с самих работ. У идущей работы она
            // останавливает, у сорвавшейся — убирает ее из списка: причину человек прочел, и
            // красный значок дальше не нужен. Без нее сорвавшаяся работа висела бы до следующего
            // запуска — может быть, до конца сеанса.
            var (linkText, act) = job.Failed
                ? ("Убрать", (Action?)(() => BackgroundJobs.Finish(job.Id)))
                : ("Остановить", job.Stop);
            if (act is not null)
            {
                var link = new TextBlock
                {
                    Text       = linkText,
                    Foreground = accent,
                    FontWeight = bold,
                    Cursor     = new Cursor(StandardCursorType.Hand),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                link.Tapped += (_, e) => { e.Handled = true; act(); };
                Grid.SetColumn(link, 2);
                top.Children.Add(link);
            }
            rows.Children.Add(top);

            // Ход идущей работы — одна строка: она меняется на каждом документе, и перенос
            // дергал бы панель. Причина сбоя — переносом: она не меняется, а обрезанная
            // многоточием не сказала бы, что случилось.
            rows.Children.Add(new TextBlock
            {
                Text         = job.Detail,
                FontSize     = small,
                Foreground   = secondary,
                TextTrimming = job.Failed ? TextTrimming.None : TextTrimming.CharacterEllipsis,
                TextWrapping = job.Failed ? TextWrapping.Wrap : TextWrapping.NoWrap,
            });

            if (job.Total > 0)
            {
                var done = Math.Clamp(job.Done, 0, job.Total);
                var bar  = new Grid
                {
                    Height = 4,
                    Margin = new Thickness(0, 7, 0, 0),
                    ColumnDefinitions =
                    [
                        new ColumnDefinition(done, GridUnitType.Star),
                        new ColumnDefinition(job.Total - done, GridUnitType.Star),
                    ],
                };
                var filled = new Border { Background = job.Failed ? red : accent, CornerRadius = new CornerRadius(2) };
                var rest   = new Border { Background = bg2, CornerRadius = new CornerRadius(2) };
                Grid.SetColumn(filled, 0);
                Grid.SetColumn(rest,   1);
                bar.Children.Add(filled);
                bar.Children.Add(rest);
                rows.Children.Add(bar);
            }

            _host.Children.Add(new Border
            {
                Padding         = new Thickness(12, 10),
                BorderBrush     = separator,
                BorderThickness = new Thickness(0, 0, 0, i < jobs.Count - 1 ? 1 : 0),
                Child           = rows,
            });
        }

        _host.Children.Add(new Border
        {
            Background = bg2,
            Padding    = new Thickness(12, 8),
            Child = new TextBlock
            {
                Text         = "Работы идут, пока открыто окно программы.",
                FontSize     = small,
                Foreground   = secondary,
                TextWrapping = TextWrapping.Wrap,
            },
        });
    }

    private static T Res<T>(Visual owner, string key, T fallback) =>
        owner.TryFindResource(key, out var res) && res is T typed ? typed : fallback;
}
