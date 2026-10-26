using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
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
public static partial class BackgroundJobsPanel
{
    private static Popup? _popup;
    private static StackPanel? _host;
    private static PanelState? _state;

    /// <summary>Показать панель у якоря; повторный вызов при открытой панели закрывает ее.</summary>
    public static void Show(Visual owner, Control anchor)
    {
        if (_popup is { IsOpen: true }) { Close(); return; }
        Detach(_state);

        _host = new StackPanel();
        var state = new PanelState(owner, _host);
        _state = state;

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
        _popup.Closed += (_, _) => Detach(state);

        BackgroundJobs.Changed += state.RequestRefresh;
        state.Render(BackgroundJobs.All);
        _popup.IsOpen = true;
    }

    /// <summary>Закрыть панель, если она открыта, — например когда работ не осталось.</summary>
    public static void Close()
    {
        if (_popup is { IsOpen: true }) _popup.IsOpen = false;
        else Detach(_state);
    }

    private static void Detach(PanelState? state)
    {
        if (state is null) return;
        state.Detach();
        if (!ReferenceEquals(_state, state)) return;
        _state = null;
        _popup = null;
        _host  = null;
    }

    private static T Res<T>(Visual owner, string key, T fallback) =>
        owner.TryFindResource(key, out var res) && res is T typed ? typed : fallback;
}
