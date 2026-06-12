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

        // Высота строк — в нижней панели; ширина столбцов — в меню заголовка.
        ConstructorFooter.RowResizeModeChanged += (_, v) => TableViewCtrl.SetRowResizeMode(v);

        // CRUD для записей — через контекстные меню в TableView/ListView.
        TableViewCtrl.RecordClicked             += (_, rid)  => OpenPage(rid);
        TableViewCtrl.RecordCreateRequested     += (_, open) => CreateAndOpen(open);
        TableViewCtrl.RecordDeleteRequested     += (_, rid)  => DeleteRecord(rid);
        TableViewCtrl.RecordDuplicateRequested  += (_, rid)  => DuplicateRecord(rid);

        ListViewCtrl.RecordClicked              += (_, rid) => OpenPage(rid);
        ListViewCtrl.RecordCreateRequested      += (_, _)   => CreateAndOpen();
        ListViewCtrl.RecordDeleteRequested      += (_, rid) => DeleteRecord(rid);

        // CRUD для свойств — через контекстное меню заголовков колонок.
        TableViewCtrl.PropertyCreateRequested    += (_, _)   => _ = OpenPropertyDialog(null);
        TableViewCtrl.PropertyEditRequested      += (_, fid) => _ = OpenPropertyDialog(fid);
        TableViewCtrl.PropertyDuplicateRequested += (_, fid) => DuplicateProperty(fid);
        TableViewCtrl.PropertyDeleteRequested    += (_, fid) => DeleteProperty(fid);
        TableViewCtrl.SystemFieldRenameRequested += (_, key) => _ = RenameSystemField(key);

        // Стартовое состояние: коллекция не выбрана — заголовок-плейсхолдер и подсказка.
        ConstructorNavbar.SetTitle("");
        SetEmptyState(true);
    }

    // Пустое состояние (коллекция не выбрана): подсказка по центру, скрыты контролы
    // навбара (вид/запись) и нижняя панель — чтобы не показывать «Всего: 0» и пр.
    private void SetEmptyState(bool empty)
    {
        EmptyStateHint.IsVisible    = empty;
        ConstructorFooter.IsVisible = !empty;
        ConstructorNavbar.ShowCollectionControls(!empty);

        // Скрываем таблицу/список, чтобы пустая шапка DataGrid не висела серой полосой
        // под навбаром. При выборе коллекции SwitchView вернёт нужную видимость.
        if (empty)
        {
            TableViewCtrl.IsVisible = false;
            ListViewCtrl.IsVisible  = false;
        }
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
        SetEmptyState(false);

        var (_, rowResize, viewMode) = _repository.GetUiSettings(collectionId);
        ConstructorFooter.LoadCollectionState(rowResize);
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
        SetEmptyState(true);
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

    // open=false — создать запись, не открывая правую панель (нажатие «+» в таблице).
    // open=true  — создать и открыть деталь (контекстное «Создать запись»).
    private void CreateAndOpen(bool open = true)
    {
        if (_currentCollectionId is null || _repository is null) return;

        var count = _repository.CountRecords(_currentCollectionId.Value);
        var rec   = _repository.AddRecord(new DbRecord
        {
            CollectionId = _currentCollectionId.Value,
            Position     = (count + 1) * 1000,
            Title        = "Без названия",
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
            if (open)
                RecordPageRequested?.Invoke(this, rec.Id.Value);
        }
    }

    private void DeleteRecord(int recordId)
    {
        if (_repository is null) return;
        _repository.DeleteRecord(recordId);
        Refresh();
        CollectionsChanged?.Invoke(this, EventArgs.Empty);
        RecordPageClosed?.Invoke(this, EventArgs.Empty);
    }

    private void DuplicateRecord(int recordId)
    {
        if (_repository is null) return;
        _repository.DuplicateRecord(recordId);
        Refresh();
        CollectionsChanged?.Invoke(this, EventArgs.Empty);
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

    // Переименование системной колонки «Название»/«Описание»: меняем только подпись
    // (DisplayName в метаданных). Физические колонки Title/Description не трогаем.
    private async Task RenameSystemField(string key)
    {
        if (_currentCollectionId is null || _repository is null) return;
        if (VisualRoot is not Avalonia.Controls.Window win) return;

        var current = TableViewCtrl.GetSystemHeader(key);
        var dlg     = new PropertyEditorDialog(current);
        var ok      = await dlg.ShowDialog<bool?>(win);
        if (ok != true || dlg.ResultName is null) return;   // отмена

        var defaultName = TableViewCtrl.GetSystemDefaultName(key);
        _repository.SetSystemFieldName(_currentCollectionId.Value, key, dlg.ResultName, defaultName);
        Refresh();
        CollectionsChanged?.Invoke(this, EventArgs.Empty);
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

    private void DuplicateProperty(int fieldId)
    {
        if (_repository is null) return;
        _repository.DuplicateField(fieldId);
        Refresh();
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
