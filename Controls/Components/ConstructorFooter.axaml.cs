using Avalonia.Controls;

namespace DataBase.Controls.Components;

public partial class ConstructorFooter : UserControl
{
    public event EventHandler?       FilterClicked;
    public event EventHandler?       SortClicked;
    public event EventHandler?       PrevPageClicked;
    public event EventHandler?       NextPageClicked;

    public ConstructorFooter()
    {
        InitializeComponent();

        FilterBtn.Click      += (_, _) => FilterClicked?.Invoke(this, EventArgs.Empty);
        SortBtn.Click        += (_, _) => SortClicked?.Invoke(this, EventArgs.Empty);
        PrevPageBtn.Click    += (_, _) => PrevPageClicked?.Invoke(this, EventArgs.Empty);
        NextPageBtn.Click    += (_, _) => NextPageClicked?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Обновить счётчик записей.</summary>
    public void SetCount(int count) => CountLabel.Text = $"Всего: {count}";

    /// <summary>Обновить индикатор страниц и активность кнопок навигации.</summary>
    public void SetPage(int page, int totalPages)
    {
        PageLabel.Text        = $"{page} / {Math.Max(1, totalPages)}";
        PrevPageBtn.IsEnabled = page > 1;
        NextPageBtn.IsEnabled = page < totalPages;
    }
}
