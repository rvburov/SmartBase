using Avalonia.Controls;
using MyPlanner.Controls.Dialogs;
using MyPlanner.Database.Repositories;

namespace MyPlanner.Views.ConstructorDb;

public partial class ConstructorControllerView : UserControl
{
    private ConstructorRepository? _repository;
    private int? _currentCollectionId;

    public event EventHandler? CollectionsChanged;

    public ConstructorControllerView() { InitializeComponent(); }

    public ConstructorControllerView(ConstructorRepository repository)
    {
        InitializeComponent();
        Initialize(repository);
    }

    public void Initialize(ConstructorRepository repository)
    {
        _repository = repository;

        ConstructorNavbar.ViewChanged          += (_, mode) => SwitchView(mode);
        ConstructorNavbar.AddClicked           += (_, _)    => _ = OpenAddRecordDialog();
        ConstructorNavbar.AddPropertyClicked   += (_, _)    => _ = OpenPropertyManagerDialog();
        ConstructorNavbar.ColResizeModeChanged += (_, v)    => TableViewCtrl.SetColResizeMode(v);
        ConstructorNavbar.RowResizeModeChanged += (_, v)    => TableViewCtrl.SetRowResizeMode(v);

        TableViewCtrl.RecordClicked += (_, rid) => _ = OpenEditRecordDialog(rid);
        ListViewCtrl.RecordClicked  += (_, rid) => _ = OpenEditRecordDialog(rid);
    }

    public void SelectCollection(int collectionId)
    {
        _currentCollectionId = collectionId;
        var cols = _repository!.GetAllCollections();
        var col  = cols.FirstOrDefault(c => c.Id == collectionId);
        if (col is null) return;

        ConstructorNavbar.SetTitle(col.Name);

        var (colResize, rowResize, viewMode) = _repository.GetUiSettings(collectionId);
        ConstructorNavbar.LoadCollectionState(colResize, rowResize, viewMode);
        SwitchView(viewMode);
    }

    public void Refresh()
    {
        if (_currentCollectionId.HasValue)
            SelectCollection(_currentCollectionId.Value);
    }

    private void SwitchView(string mode)
    {
        TableViewCtrl.IsVisible = mode != "list";
        ListViewCtrl.IsVisible  = mode == "list";

        if (_currentCollectionId.HasValue && _repository is not null)
        {
            if (mode == "list")
                ListViewCtrl.Load(_repository, _currentCollectionId.Value);
            else
                TableViewCtrl.Load(_repository, _currentCollectionId.Value);
        }
    }

    private async Task OpenAddRecordDialog()
    {
        if (_currentCollectionId is null || _repository is null) return;
        if (VisualRoot is not Avalonia.Controls.Window win) return;
        var dlg = new RecordDialog(_repository, _currentCollectionId.Value);
        await dlg.ShowDialog<bool?>(win);
        Refresh();
        CollectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task OpenEditRecordDialog(int recordId)
    {
        if (_currentCollectionId is null || _repository is null) return;
        if (VisualRoot is not Avalonia.Controls.Window win) return;
        var dlg = new RecordDialog(_repository, _currentCollectionId.Value, recordId);
        await dlg.ShowDialog<bool?>(win);
        Refresh();
    }

    private async Task OpenPropertyManagerDialog()
    {
        if (_currentCollectionId is null || _repository is null) return;
        if (VisualRoot is not Avalonia.Controls.Window win) return;
        var dlg = new PropertyManagerDialog(_repository, _currentCollectionId.Value);
        await dlg.ShowDialog<bool?>(win);
        Refresh();
        CollectionsChanged?.Invoke(this, EventArgs.Empty);
    }
}
