using Avalonia.Controls;
using Avalonia.Input;
using MyPlanner.Core.Models;
using MyPlanner.Database.Repositories;

namespace MyPlanner.Controls.Dialogs;

public record CollectionDialogResult(string Name, int? GroupId);

public partial class CollectionDialog : Window
{
    private readonly List<(string Label, int? Id)> _groups;

    public CollectionDialog(ConstructorRepository repository, DbCollection? editNode = null, int? defaultGroupId = null)
    {
        InitializeComponent();

        var allGroups = repository.GetAllGroups();
        _groups = [("Корневой уровень", null), ..allGroups.Select(g => (g.Name, (int?)g.Id))];

        LocationCombo.ItemsSource   = _groups.Select(g => g.Label).ToList();
        LocationCombo.SelectedIndex = 0;

        if (defaultGroupId.HasValue)
        {
            var idx = _groups.FindIndex(g => g.Id == defaultGroupId);
            if (idx >= 0) LocationCombo.SelectedIndex = idx;
        }

        if (editNode is not null)
        {
            Title              = "Переименовать коллекцию";
            NameBox.Text       = editNode.Name;
            ConfirmBtn.Content = "Сохранить";

            var editIdx = _groups.FindIndex(g => g.Id == editNode.GroupId);
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
        var groupId = _groups[Math.Max(0, LocationCombo.SelectedIndex)].Id;
        Close(new CollectionDialogResult(name, groupId));
    }
}
