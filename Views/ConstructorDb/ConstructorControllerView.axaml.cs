using Avalonia.Controls;
using MyPlanner.Controls.Dialogs;
using MyPlanner.Core.Models;
using MyPlanner.Database.Repositories;

namespace MyPlanner.Views.ConstructorDb;

public partial class ConstructorControllerView : UserControl
{
    private ConstructorRepository? _repository;
    private int? _currentCollectionId;

    public event EventHandler?      CollectionsChanged;

    /// <summary>Запрос открыть запись в правой панели (RecordPageView в MainWindow).</summary>
    public event EventHandler<int>? RecordPageRequested;

    /// <summary>Запрос закрыть правую панель (например, при удалении открытой записи).</summary>
    public event EventHandler?      RecordPageClosed;

    /// <summary>Запрос переключить видимость правой панели записи из навбара коллекции.</summary>
    public event EventHandler<bool>? RecordPanelToggled;

    /// <summary>Текущая открытая коллекция (для MainWindow при открытии PageView).</summary>
    public int? CurrentCollectionId => _currentCollectionId;

    /// <summary>Синхронизирует визуальное состояние toggle-кнопки записи в навбаре.</summary>
    public void SetRecordPanelOpen(bool isOpen) => ConstructorNavbar.SetRecordPanelOpen(isOpen);

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

        // Переименование коллекции прямо из заголовка навбара.
        ConstructorNavbar.TitleEdited += (_, newName) => RenameCurrentCollection(newName);

        // Toggle правой панели записи в навбаре — пробрасываем в MainWindow.
        ConstructorNavbar.RecordPanelToggled += (_, isOpen) => RecordPanelToggled?.Invoke(this, isOpen);

        // Управление ресайзом — в нижней панели.
        ConstructorFooter.ColResizeModeChanged += (_, v) => TableViewCtrl.SetColResizeMode(v);
        ConstructorFooter.RowResizeModeChanged += (_, v) => TableViewCtrl.SetRowResizeMode(v);

        // CRUD для записей — через контекстные меню в TableView/ListView.
        TableViewCtrl.RecordClicked          += (_, rid) => OpenPage(rid);
        TableViewCtrl.RecordCreateRequested  += (_, _)   => CreateAndOpen();
        TableViewCtrl.RecordDeleteRequested  += (_, rid) => DeleteRecord(rid);

        ListViewCtrl.RecordClicked           += (_, rid) => OpenPage(rid);
        ListViewCtrl.RecordCreateRequested   += (_, _)   => CreateAndOpen();
        ListViewCtrl.RecordDeleteRequested   += (_, rid) => DeleteRecord(rid);

        // CRUD для свойств — через контекстное меню заголовков колонок.
        TableViewCtrl.PropertyCreateRequested += (_, _)   => _ = OpenPropertyDialog(null);
        TableViewCtrl.PropertyEditRequested   += (_, fid) => _ = OpenPropertyDialog(fid);
        TableViewCtrl.PropertyDeleteRequested += (_, fid) => DeleteProperty(fid);
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
        RecordPageClosed?.Invoke(this, EventArgs.Empty);
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

    // ── Записи ────────────────────────────────────────────────────────────────

    private void OpenPage(int recordId) =>
        RecordPageRequested?.Invoke(this, recordId);

    private void CreateAndOpen()
    {
        if (_currentCollectionId is null || _repository is null) return;

        var count = _repository.CountRecords(_currentCollectionId.Value);
        var rec   = _repository.AddRecord(new DbRecord
        {
            CollectionId = _currentCollectionId.Value,
            Position     = (count + 1) * 1000,
        });

        Refresh();
        CollectionsChanged?.Invoke(this, EventArgs.Empty);

        if (rec.Id.HasValue)
        {
            // Прокручиваем таблицу к новой записи, чтобы кнопка «+» (add-row)
            // осталась в видимой зоне — иначе при серии создания приходится
            // каждый раз листать вниз, чтобы снова нажать «+».
            if (TableViewCtrl.IsVisible)
                TableViewCtrl.ScrollToRecord(rec.Id.Value);
            RecordPageRequested?.Invoke(this, rec.Id.Value);
        }
    }

    private void DeleteRecord(int recordId)
    {
        if (_repository is null) return;
        _repository.DeleteRecord(recordId);
        Refresh();
        CollectionsChanged?.Invoke(this, EventArgs.Empty);
        // Если удаляли открытую — попросим главное окно закрыть панель.
        RecordPageClosed?.Invoke(this, EventArgs.Empty);
    }

    // ── Свойства (поля) ───────────────────────────────────────────────────────

    private async Task OpenPropertyDialog(int? fieldId)
    {
        if (_currentCollectionId is null || _repository is null) return;
        if (VisualRoot is not Avalonia.Controls.Window win) return;

        var dlg = new PropertyEditorDialog(_repository, _currentCollectionId.Value, fieldId);
        var ok  = await dlg.ShowDialog<bool?>(win);
        if (ok == true)
        {
            Refresh();
            CollectionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ── Переименование коллекции ──────────────────────────────────────────────

    private void RenameCurrentCollection(string newName)
    {
        if (_currentCollectionId is null || _repository is null) return;

        var col = _repository.GetAllCollections().FirstOrDefault(c => c.Id == _currentCollectionId);
        if (col is null || col.Name == newName) return;

        col.Name = newName;
        _repository.UpdateCollection(col);
        CollectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DeleteProperty(int fieldId)
    {
        if (_repository is null) return;
        var field = _repository.GetFields(_currentCollectionId ?? 0).FirstOrDefault(f => f.Id == fieldId);
        if (field is null || field.IsRequired) return;

        _repository.DeleteField(fieldId);
        Refresh();
        CollectionsChanged?.Invoke(this, EventArgs.Empty);
    }
}
