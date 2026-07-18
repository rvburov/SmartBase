using Avalonia.Controls;
using SmartBase.Database.Repositories;

namespace SmartBase.Controls.Dialogs;

// Результат окна «Расположение» записи: целевая БД, целевая коллекция и флаг переноса всех
// свойств (создавать в цели отсутствующие свойства). Аналог LocationDialogResult, но для записи
// цель — коллекция, а не группа.
public record RecordLocationResult(int DbId, int CollectionId, bool CreateMissingFields);

// Окно «Расположение» для записи — как «Расположение» коллекции в сайдбаре (LocationDialog), но
// второй список выбирает целевую КОЛЛЕКЦИЮ (а не группу). Плюс «Перенести все свойства»:
// при переносе в коллекцию с другим набором свойств решает, отбрасывать несовпадающие свойства
// или создавать их в цели. Только выбирает цель; сам перенос делает контроллер.
public partial class RecordLocationDialog : Window
{
    private readonly IReadOnlyList<DbOption> _dbs;
    private List<(string Name, int Id)> _collections = [];

    private ConstructorRepository CurrentRepo => _dbs[Math.Max(0, DbCombo.SelectedIndex)].Repo;
    private int                   CurrentDbId => _dbs[Math.Max(0, DbCombo.SelectedIndex)].Id;

    public RecordLocationDialog(IReadOnlyList<DbOption> dbs, int currentDbId, int? currentCollectionId)
    {
        InitializeComponent();
        _dbs = dbs;

        DbCombo.ItemsSource   = _dbs.Select(d => d.Name).ToList();
        DbCombo.SelectedIndex = IndexOfDb(currentDbId);

        // Префилл — текущая коллекция, по которой кликнули; смена БД сбрасывает выбор (коллекции свои).
        RebuildCollections(currentCollectionId);
        DbCombo.SelectionChanged += (_, _) => RebuildCollections(null);

        CancelBtn.Click  += (_, _) => Close(null);
        ConfirmBtn.Click += (_, _) =>
        {
            if (_collections.Count == 0) { Close(null); return; }   // в целевой БД нет коллекций — некуда
            var col = _collections[Math.Max(0, CollectionCombo.SelectedIndex)];
            Close(new RecordLocationResult(CurrentDbId, col.Id, CopyAllCheck.IsChecked == true));
        };
    }

    private int IndexOfDb(int dbId)
    {
        for (var i = 0; i < _dbs.Count; i++)
            if (_dbs[i].Id == dbId) return i;
        return 0;
    }

    private void RebuildCollections(int? selectCollectionId)
    {
        _collections = CurrentRepo.GetAllCollections().Select(c => (c.Name, c.Id!.Value)).ToList();
        CollectionCombo.ItemsSource = _collections.Select(c => c.Name).ToList();
        var idx = selectCollectionId.HasValue ? _collections.FindIndex(c => c.Id == selectCollectionId) : -1;
        CollectionCombo.SelectedIndex = idx >= 0 ? idx : (_collections.Count > 0 ? 0 : -1);
    }
}
