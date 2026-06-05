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

        // Переключатель вида остаётся в верхнем навбаре.
        ConstructorNavbar.ViewChanged += (_, mode) => SwitchView(mode);

        // Остальные кнопки управления — в нижней панели (футере).
        ConstructorFooter.AddClicked           += (_, _)    => _ = OpenAddRecordDialog();
        ConstructorFooter.AddPropertyClicked   += (_, _)    => _ = OpenPropertyManagerDialog();
        ConstructorFooter.ColResizeModeChanged += (_, v)    => TableViewCtrl.SetColResizeMode(v);
        ConstructorFooter.RowResizeModeChanged += (_, v)    => TableViewCtrl.SetRowResizeMode(v);

        TableViewCtrl.RecordClicked += (_, rid) => _ = OpenEditRecordDialog(rid);
        ListViewCtrl.RecordClicked  += (_, rid) => _ = OpenEditRecordDialog(rid);

        // Навигация по страницам — заготовка (пагинация будет добавлена позже).
    }

    public void SelectCollection(int collectionId)
    {
        var cols = _repository!.GetAllCollections();
        var col  = cols.FirstOrDefault(c => c.Id == collectionId);
        if (col is null)
        {
            // Коллекция отсутствует (например, удалена) — очищаем view.
            ClearView();
            return;
        }

        _currentCollectionId = collectionId;
        ConstructorNavbar.SetTitle(col.Name);

        var (colResize, rowResize, viewMode) = _repository.GetUiSettings(collectionId);
        ConstructorFooter.LoadCollectionState(colResize, rowResize);
        ConstructorNavbar.SetViewMode(viewMode);
        SwitchView(viewMode);

        // Нижняя панель: счётчик записей и страницы (пока 1 страница).
        var count = _repository.CountRecords(collectionId);
        ConstructorFooter.SetCount(count);
        ConstructorFooter.SetPage(1, 1);
    }

    public void Refresh()
    {
        if (_currentCollectionId.HasValue)
            SelectCollection(_currentCollectionId.Value);
    }

    private void ClearView()
    {
        _currentCollectionId = null;
        ConstructorNavbar.SetTitle("");
        TableViewCtrl.Clear();
        ListViewCtrl.Clear();
        ConstructorFooter.SetCount(0);
        ConstructorFooter.SetPage(1, 1);
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
