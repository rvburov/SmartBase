using Avalonia.Controls;
using Avalonia.Input;
using MyPlanner.Core.Models;
using MyPlanner.Database.Repositories;

namespace MyPlanner.Controls.Dialogs;

public record GroupDialogResult(string Name, int? ParentId);

public partial class GroupDialog : Window
{
    private readonly List<(string Label, int? Id)> _groups;

    public GroupDialog(ConstructorRepository repository, DbGroup? editNode = null, int? defaultParentId = null)
    {
        InitializeComponent();

        var allGroups = repository.GetAllGroups()
            .Where(g => editNode is null || g.Id != editNode.Id)
            .ToList();

        _groups = [("Корневой уровень", null), ..allGroups.Select(g => (g.Name, (int?)g.Id))];

        LocationCombo.ItemsSource   = _groups.Select(g => g.Label).ToList();
        LocationCombo.SelectedIndex = 0;

        if (defaultParentId.HasValue)
        {
            var idx = _groups.FindIndex(g => g.Id == defaultParentId);
            if (idx >= 0) LocationCombo.SelectedIndex = idx;
        }

        if (editNode is not null)
        {
            Title              = "Переименовать группу";
            NameBox.Text       = editNode.Name;
            ConfirmBtn.Content = "Сохранить";

            var editIdx = _groups.FindIndex(g => g.Id == editNode.ParentId);
            if (editIdx >= 0) LocationCombo.SelectedIndex = editIdx;
        }

        CancelBtn.Click  += (_, _) => Close(null);
        ConfirmBtn.Click += (_, _) => TryConfirm();
        NameBox.KeyDown  += (_, e) => { if (e.Key == Key.Enter) TryConfirm(); };
    }

    private void TryConfirm()
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name)) { NameBox.Focus(); return; }
        var parentId = _groups[Math.Max(0, LocationCombo.SelectedIndex)].Id;
        Close(new GroupDialogResult(name, parentId));
    }
}
