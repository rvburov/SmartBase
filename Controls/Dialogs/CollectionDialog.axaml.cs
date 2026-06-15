using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using DataBase.Core.Models;
using DataBase.Database.Repositories;

namespace DataBase.Controls.Dialogs;

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
            Title              = "Изменение коллекции";
            NameBox.Text       = editNode.Name;
            ConfirmBtn.Content = "Сохранить";

            var editIdx = _groups.FindIndex(g => g.Id == editNode.GroupId);
            if (editIdx >= 0) LocationCombo.SelectedIndex = editIdx;
        }

        CancelBtn.Click   += (_, _) => Close(null);
        ConfirmBtn.Click  += (_, _) => TryConfirm();
        NameBox.KeyDown   += (_, e) => { if (e.Key == Key.Enter) TryConfirm(); };
        NameBox.TextChanged += (_, _) => HideError();
    }

    private void TryConfirm()
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
        {
            ShowError("Укажите название коллекции");
            NameBox.Focus();
            return;
        }
        var groupId = _groups[Math.Max(0, LocationCombo.SelectedIndex)].Id;
        Close(new CollectionDialogResult(name, groupId));
    }

    private void ShowError(string message)
    {
        var pos = Position;            // баннер увеличит высоту — держим позицию
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

    // Держим позицию окна при пересчёте высоты (SizeToContent), чтобы оно не «прыгало».
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
