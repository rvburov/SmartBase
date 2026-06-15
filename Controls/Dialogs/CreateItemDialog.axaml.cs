using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using DataBase.Database.Repositories;

namespace DataBase.Controls.Dialogs;

// Type: "collection" | "group"
public record CreateItemResult(string Type, string Name, int? ParentId);

public partial class CreateItemDialog : Window
{
    private readonly List<(string Label, int? Id)> _groups;
    private readonly ConstructorRepository _repository;

    public CreateItemDialog(ConstructorRepository repository,
                            string presetType = "collection",
                            int? presetParentId = null)
    {
        InitializeComponent();
        _repository = repository;

        TypeCombo.ItemsSource = new[] { "Коллекция", "Группа" };
        TypeCombo.SelectedIndex = presetType == "group" ? 1 : 0;

        // Список «Расположение» с полным путём вложенности групп (см. GroupLocationOptions).
        _groups = GroupLocationOptions.Build(repository.GetAllGroups());
        LocationCombo.ItemsSource   = _groups.Select(g => g.Label).ToList();
        LocationCombo.SelectedIndex = 0;

        if (presetParentId.HasValue)
        {
            var idx = _groups.FindIndex(g => g.Id == presetParentId);
            if (idx >= 0) LocationCombo.SelectedIndex = idx;
        }

        CancelBtn.Click     += (_, _) => Close(null);
        ConfirmBtn.Click    += (_, _) => TryConfirm();
        NameBox.KeyDown     += (_, e) => { if (e.Key == Key.Enter) TryConfirm(); };
        NameBox.TextChanged += (_, _) => HideError();
    }

    private void TryConfirm()
    {
        var name = NameBox.Text?.Trim() ?? "";
        var type = TypeCombo.SelectedIndex == 1 ? "group" : "collection";

        if (string.IsNullOrEmpty(name))
        {
            ShowError(type == "group" ? "Укажите название группы" : "Укажите название коллекции");
            NameBox.Focus();
            return;
        }

        var parentId = _groups[Math.Max(0, LocationCombo.SelectedIndex)].Id;

        var exists = type == "group"
            ? _repository.GroupNameExists(name, parentId)
            : _repository.CollectionNameExists(name, parentId);
        if (exists)
        {
            ShowError(type == "group"
                ? "Группа с таким названием уже существует"
                : "Коллекция с таким названием уже существует");
            NameBox.Focus();
            return;
        }

        Close(new CreateItemResult(type, name, parentId));
    }

    private void ShowError(string message)
    {
        var pos = Position;
        ErrorText.Text        = message;
        ErrorBanner.IsVisible = true;
        KeepWindowPosition(pos);
    }

    private void HideError()
    {
        if (!ErrorBanner.IsVisible) return;
        var pos = Position;
        ErrorBanner.IsVisible = false;
        KeepWindowPosition(pos);
    }

    private void KeepWindowPosition(PixelPoint pos)
    {
        void Restore(object? _, EventArgs __)
        {
            LayoutUpdated -= Restore;
            if (Position != pos) Position = pos;
        }
        LayoutUpdated += Restore;
    }
}
