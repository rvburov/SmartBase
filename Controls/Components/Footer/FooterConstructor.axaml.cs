using Avalonia.Controls;
using SmartBase.Core.Models;

namespace SmartBase.Controls.Components;

/// <summary>
/// Нижняя панель конструктора — только навигация по строкам таблицы:
/// «‹‹ ‹ 10 – 50 из 125 437 › ››». Фильтр и сортировка вызываются из меню заголовка
/// колонки, отдельного счетчика записей нет (общее число стоит в самом индикаторе).
/// </summary>
public partial class FooterConstructor : UserControl
{
    /// <summary>«‹» — экран назад.</summary>
    public event EventHandler?       PrevPageClicked;
    /// <summary>«›» — экран вперед.</summary>
    public event EventHandler?       NextPageClicked;
    /// <summary>«‹‹» — в начало таблицы.</summary>
    public event EventHandler?       FirstRowClicked;
    /// <summary>«››» — в конец таблицы.</summary>
    public event EventHandler?       LastRowClicked;

    public FooterConstructor()
    {
        InitializeComponent();

        FirstRowBtn.Click    += (_, _) => FirstRowClicked?.Invoke(this, EventArgs.Empty);
        PrevPageBtn.Click    += (_, _) => PrevPageClicked?.Invoke(this, EventArgs.Empty);
        NextPageBtn.Click    += (_, _) => NextPageClicked?.Invoke(this, EventArgs.Empty);
        LastRowBtn.Click     += (_, _) => LastRowClicked?.Invoke(this, EventArgs.Empty);

        SetRange(TableViewportRange.Empty);
    }

    /// <summary>
    /// Индикатор видимых строк («10 – 50 из 125 437») и активность кнопок навигации.
    /// Кнопки к краю, у которого уже стоим, гаснут: у начала — «в начало» и «экран назад»,
    /// у конца — «экран вперед» и «в конец».
    /// </summary>
    public void SetRange(TableViewportRange range)
    {
        PageLabel.Text        = range.Label;
        FirstRowBtn.IsEnabled = !range.AtStart;
        PrevPageBtn.IsEnabled = !range.AtStart;
        NextPageBtn.IsEnabled = !range.AtEnd;
        LastRowBtn.IsEnabled  = !range.AtEnd;
    }
}
