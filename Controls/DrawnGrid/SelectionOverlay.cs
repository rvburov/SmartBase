using Avalonia.Controls;
using Avalonia.Media;
using SmartBase.Controls.Behaviors;

namespace SmartBase.Controls.DrawnGrid;

/// <summary>
/// Рамка выделения ячеек над редактором ячейки: лежит поверх <see cref="EditorHost"/> в той же
/// клетке панели, что поверхность, и рисует рамку ее кодом (<see cref="GridSurface.RenderSelectionFrame"/>).
/// У нынешней таблицы рамка тоже на слое над правкой: в правке она не гаснет и не прячется под
/// редактор с цветом ячейки — одна рамка в одном месте в обоих состояниях (стережет
/// Tests/EditGeometry). Указатель не ловит; перерисовывается по <see cref="GridSurface.ViewChanged"/>.
/// </summary>
public sealed class SelectionOverlay : Control
{
    private readonly GridSurface _surface;

    public SelectionOverlay(GridSurface surface)
    {
        _surface         = surface;
        IsHitTestVisible = false;
        ClipToBounds     = true;
        surface.ViewChanged  += (_, _) => InvalidateVisual();
        surface.FrameChanged += (_, _) => InvalidateVisual();
    }

    public override void Render(DrawingContext ctx) =>
        _surface.RenderSelectionFrame(ctx, LineMetrics.EffectiveScale(this));
}
