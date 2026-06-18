using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using DataBase.Database.Repositories;

namespace DataBase.Controls.Dialogs;

// Type: "collection" | "group"
public record CreateItemResult(string Type, string Name, int? ParentId, int DbId);

public partial class CreateItemDialog : Window
{
    private readonly IReadOnlyList<DbOption> _dbs;
    private List<(string Label, int? Id)> _groups = [];

    private ConstructorRepository CurrentRepo => _dbs[Math.Max(0, DbCombo.SelectedIndex)].Repo;
    private int                   CurrentDbId => _dbs[Math.Max(0, DbCombo.SelectedIndex)].Id;

    public CreateItemDialog(IReadOnlyList<DbOption> dbs,
                            int currentDbId,
                            string presetType = "collection",
                            int? presetParentId = null)
    {
        InitializeComponent();
        _dbs = dbs;

        TypeCombo.ItemsSource   = new[] { "Коллекция", "Группа" };
        TypeCombo.SelectedIndex = presetType == "group" ? 1 : 0;

        // Список баз; по умолчанию — та БД, из которой вызвано создание.
        DbCombo.ItemsSource   = _dbs.Select(d => d.Name).ToList();
        DbCombo.SelectedIndex = IndexOfDb(currentDbId);

        // «Расположение» строится под выбранную БД; смена БД — перестраивает его (в корень).
        RebuildLocation(presetParentId);
        DbCombo.SelectionChanged += (_, _) => RebuildLocation(null);

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

    // Перестроить «Расположение» под выбранную БД; selectParentId — какую группу выделить.
    private void RebuildLocation(int? selectParentId)
    {
        _groups = GroupLocationOptions.Build(CurrentRepo.GetAllGroups());
        LocationCombo.ItemsSource = _groups.Select(g => g.Label).ToList();
        var idx = selectParentId.HasValue ? _groups.FindIndex(g => g.Id == selectParentId) : -1;
        LocationCombo.SelectedIndex = idx >= 0 ? idx : 0;
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
        var repo     = CurrentRepo;   // проверка уникальности — в выбранной БД

        var exists = type == "group"
            ? repo.GroupNameExists(name, parentId)
            : repo.CollectionNameExists(name, parentId);
        if (exists)
        {
            ShowError(type == "group"
                ? "Группа с таким названием уже существует"
                : "Коллекция с таким названием уже существует");
            NameBox.Focus();
            return;
        }

        Close(new CreateItemResult(type, name, parentId, CurrentDbId));
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
