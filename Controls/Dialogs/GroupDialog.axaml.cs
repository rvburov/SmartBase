using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using DataBase.Core.Models;
using DataBase.Database.Repositories;

namespace DataBase.Controls.Dialogs;

public record GroupDialogResult(string Name, int? ParentId, int DbId);

public partial class GroupDialog : Window
{
    private readonly IReadOnlyList<DbOption> _dbs;
    private List<(string Label, int? Id)> _groups = [];

    // Исходная БД группы и её id — чтобы в пределах своей БД исключать собственную ветку
    // из «Расположения» (нельзя вложить группу в себя/потомка). При переносе в другую БД
    // такого ограничения нет.
    private readonly int  _originalDbId;
    private readonly int? _excludeSubtreeRootId;

    private ConstructorRepository CurrentRepo => _dbs[Math.Max(0, DbCombo.SelectedIndex)].Repo;
    private int                   CurrentDbId => _dbs[Math.Max(0, DbCombo.SelectedIndex)].Id;

    public GroupDialog(IReadOnlyList<DbOption> dbs, int currentDbId, DbGroup? editNode = null)
    {
        InitializeComponent();
        _dbs                  = dbs;
        _originalDbId         = currentDbId;
        _excludeSubtreeRootId = editNode?.Id;

        DbCombo.ItemsSource   = _dbs.Select(d => d.Name).ToList();
        DbCombo.SelectedIndex = IndexOfDb(currentDbId);

        RebuildLocation(editNode?.ParentId);
        DbCombo.SelectionChanged += (_, _) => RebuildLocation(null);

        if (editNode is not null)
        {
            Title              = "Изменение группы";
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

    private void RebuildLocation(int? selectParentId)
    {
        // Ветку группы исключаем только в её собственной БД; при переносе — все группы доступны.
        var exclude = CurrentDbId == _originalDbId ? _excludeSubtreeRootId : null;
        _groups = GroupLocationOptions.Build(CurrentRepo.GetAllGroups(), excludeSubtreeRootId: exclude);
        LocationCombo.ItemsSource = _groups.Select(g => g.Label).ToList();
        var idx = selectParentId.HasValue ? _groups.FindIndex(g => g.Id == selectParentId) : -1;
        LocationCombo.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void TryConfirm()
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
        {
            ShowError("Укажите название группы");
            NameBox.Focus();
            return;
        }
        var parentId = _groups[Math.Max(0, LocationCombo.SelectedIndex)].Id;
        Close(new GroupDialogResult(name, parentId, CurrentDbId));
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
