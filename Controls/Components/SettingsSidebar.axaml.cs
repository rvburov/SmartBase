using Avalonia.Controls;
using Avalonia.Input;

namespace DataBase.Controls.Components;

public partial class SettingsSidebar : UserControl
{
    // Поднимается при выборе раздела. Аргумент — ключ раздела (Tag строки).
    public event EventHandler<string>? CategorySelected;

    private readonly Border[] _rows;

    public SettingsSidebar()
    {
        InitializeComponent();
        _rows = [GeneralRow, AppearanceRow, DatabaseRow, AboutRow];
    }

    // Текущий выбранный раздел (по умолчанию — «Общие»).
    public string SelectedKey { get; private set; } = "general";

    // Программно выбрать раздел: подсветить строку и уведомить подписчиков.
    public void Select(string key)
    {
        SelectedKey = key;
        foreach (var row in _rows)
        {
            var isSelected = (row.Tag as string) == key;
            if (isSelected) row.Classes.Add("selected");
            else            row.Classes.Remove("selected");
        }
        CategorySelected?.Invoke(this, key);
    }

    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border { Tag: string key })
            Select(key);
    }
}
