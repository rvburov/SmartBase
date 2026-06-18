using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using DataBase.Core.Models;
using DataBase.Database.Repositories;

namespace DataBase.Controls.Dialogs;

public record CollectionDialogResult(string Name, int? GroupId, int DbId);

public partial class CollectionDialog : Window
{
    private readonly IReadOnlyList<DbOption> _dbs;
    private List<(string Label, int? Id)> _groups = [];

    private ConstructorRepository CurrentRepo => _dbs[Math.Max(0, DbCombo.SelectedIndex)].Repo;
    private int                   CurrentDbId => _dbs[Math.Max(0, DbCombo.SelectedIndex)].Id;

    public CollectionDialog(IReadOnlyList<DbOption> dbs, int currentDbId, DbCollection? editNode = null)
    {
        InitializeComponent();
        _dbs = dbs;

        DbCombo.ItemsSource   = _dbs.Select(d => d.Name).ToList();
        DbCombo.SelectedIndex = IndexOfDb(currentDbId);

        // «Расположение» под выбранную БД; при создании editNode=null → корень.
        RebuildLocation(editNode?.GroupId);
        DbCombo.SelectionChanged += (_, _) => RebuildLocation(null);

        if (editNode is not null)
        {
            Title              = "Изменение коллекции";
            NameBox.Text       = editNode.Name;
            ConfirmBtn.Content = "Сохранить";
        }

        CancelBtn.Click     += (_, _) => Close(null);
        ConfirmBtn.Click    += (_, _) => TryConfirm();
        NameBox.KeyDown     += (_, e) => { if (e.Key == Key.Enter) TryConfirm(); };
        NameBox.TextChanged += (_, _) => HideError();
    }

    private int IndexOfDb(int dbId)
    {
        for (var i = 0; i < _dbs.Count; i++)
            if (_dbs[i].Id == dbId) return i;
        return 0;
    }

    private void RebuildLocation(int? selectGroupId)
    {
        _groups = GroupLocationOptions.Build(CurrentRepo.GetAllGroups());
        LocationCombo.ItemsSource = _groups.Select(g => g.Label).ToList();
        var idx = selectGroupId.HasValue ? _groups.FindIndex(g => g.Id == selectGroupId) : -1;
        LocationCombo.SelectedIndex = idx >= 0 ? idx : 0;
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
        Close(new CollectionDialogResult(name, groupId, CurrentDbId));
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
