using Avalonia.Controls;
using SmartBase.Database.Repositories;

namespace SmartBase.Controls.Dialogs;

// Результат окна «Расположение»: целевая БД и группа (null — корневой уровень). Название
// коллекции не меняется (в отличие от CollectionDialog) — окно только перемещает.
public record LocationDialogResult(int? GroupId, int DbId);

// Окно «Расположение» — как «Изменение коллекции», но без поля названия: только выбор целевой
// БД и расположения. Применяется к одной или нескольким выделенным коллекциям (см. Sidebar).
public partial class LocationDialog : Window
{
    private readonly IReadOnlyList<DbOption> _dbs;
    private readonly int _originalDbId;
    private readonly IReadOnlySet<int>? _excludeGroupSubtrees;
    private List<(string Label, int? Id)> _groups = [];

    private ConstructorRepository CurrentRepo => _dbs[Math.Max(0, DbCombo.SelectedIndex)].Repo;
    private int                   CurrentDbId => _dbs[Math.Max(0, DbCombo.SelectedIndex)].Id;

    // excludeGroupSubtrees — id перемещаемых групп (их ветки нельзя выбрать как назначение —
    // нельзя вложить группу в саму себя/потомка). Действует только в исходной БД.
    public LocationDialog(IReadOnlyList<DbOption> dbs, int currentDbId, int? currentGroupId,
                          IReadOnlySet<int>? excludeGroupSubtrees = null)
    {
        InitializeComponent();
        _dbs = dbs;
        _originalDbId = currentDbId;
        _excludeGroupSubtrees = excludeGroupSubtrees;

        DbCombo.ItemsSource   = _dbs.Select(d => d.Name).ToList();
        DbCombo.SelectedIndex = IndexOfDb(currentDbId);

        // «Расположение» под выбранную БД; префилл — текущая группа коллекции, по которой кликнули.
        RebuildLocation(currentGroupId);
        // Смена БД сбрасывает расположение к корню (группы у другой БД свои).
        DbCombo.SelectionChanged += (_, _) => RebuildLocation(null);

        CancelBtn.Click  += (_, _) => Close(null);
        ConfirmBtn.Click += (_, _) =>
            Close(new LocationDialogResult(_groups[Math.Max(0, LocationCombo.SelectedIndex)].Id, CurrentDbId));
    }

    private int IndexOfDb(int dbId)
    {
        for (var i = 0; i < _dbs.Count; i++)
            if (_dbs[i].Id == dbId) return i;
        return 0;
    }

    private void RebuildLocation(int? selectGroupId)
    {
        // Ветки перемещаемых групп исключаем только в их собственной БД; при переносе в другую — все доступны.
        var exclude = CurrentDbId == _originalDbId ? _excludeGroupSubtrees : null;
        _groups = GroupLocationOptions.Build(CurrentRepo.GetAllGroups(), excludeSubtreeRoots: exclude);
        LocationCombo.ItemsSource = _groups.Select(g => g.Label).ToList();
        var idx = selectGroupId.HasValue ? _groups.FindIndex(g => g.Id == selectGroupId) : -1;
        LocationCombo.SelectedIndex = idx >= 0 ? idx : 0;
    }
}
