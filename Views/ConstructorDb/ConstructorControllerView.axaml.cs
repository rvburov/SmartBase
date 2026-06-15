using Avalonia.Controls;
using DataBase.Controls.Dialogs;
using DataBase.Core.Models;
using DataBase.Database.Repositories;

namespace DataBase.Views.ConstructorDb;

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

        // Высота строк и ширина столбцов переключаются из контекстных меню таблицы
        // (записи и заголовка колонки соответственно).

        // CRUD для записей — через контекстные меню в TableView/ListView.
        TableViewCtrl.RecordClicked             += (_, rid)  => OpenPage(rid);
        TableViewCtrl.RecordCreateRequested     += (_, e)    => CreateAndOpen(e.Open, e.AfterId);
        TableViewCtrl.RecordsDeleteRequested    += (_, ids)  => _ = DeleteRecordsWithConfirm(ids);
        TableViewCtrl.RecordDuplicateRequested  += (_, rid)  => DuplicateRecord(rid);

        ListViewCtrl.RecordClicked              += (_, rid) => OpenPage(rid);
        ListViewCtrl.RecordCreateRequested      += (_, _)   => CreateAndOpen();
        ListViewCtrl.RecordDeleteRequested      += (_, rid) => DeleteRecord(rid);

        // CRUD для свойств — через контекстное меню заголовков колонок.
        TableViewCtrl.PropertyCreateRequested    += (_, _)   => _ = OpenPropertyDialog(null);
        TableViewCtrl.PropertyCreateAfterRequested += (_, key) => _ = CreatePropertyAfter(key);
        TableViewCtrl.PropertyEditRequested      += (_, fid) => _ = OpenPropertyDialog(fid);
        TableViewCtrl.PropertyDuplicateRequested += (_, fid) => DuplicateProperty(fid);
        TableViewCtrl.PropertiesDeleteRequested  += (_, ids) => _ = DeletePropertiesWithConfirm(ids);
        TableViewCtrl.SystemFieldRenameRequested += (_, key) => _ = RenameSystemField(key);
        TableViewCtrl.ColumnDisplayRequested     += (_, _)   => _ = OpenDisplayDialog();
        TableViewCtrl.RecordDisplayRequested     += (_, _)   => _ = OpenRecordDisplayDialog();

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

        var (_, _, viewMode) = _repository.GetUiSettings(collectionId);
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
    // afterRecordId != null — создать сразу после этой записи (меню строки); иначе в конце.
    private void CreateAndOpen(bool open = true, int? afterRecordId = null)
    {
        if (_currentCollectionId is null || _repository is null) return;

        // Создание в середине (меню строки) — сохраняем вертикальную позицию, чтобы
        // таблицу не откидывало вниз. Снимаем offset ДО пересборки.
        var keepVertical = afterRecordId is not null && TableViewCtrl.IsVisible
            ? TableViewCtrl.CurrentVerticalOffset()
            : (double?)null;

        var newRecord = new DbRecord
        {
            CollectionId = _currentCollectionId.Value,
            Title        = "Без названия",
        };

        DbRecord rec;
        if (afterRecordId is { } afterId)
        {
            rec = _repository.AddRecordAfter(newRecord, afterId);
        }
        else
        {
            var count = _repository.CountRecords(_currentCollectionId.Value);
            newRecord.Position = (count + 1) * 1000;
            rec = _repository.AddRecord(newRecord);
        }

        Refresh();
        CollectionsChanged?.Invoke(this, EventArgs.Empty);

        if (rec.Id.HasValue)
        {
            if (keepVertical is { } off)
            {
                // Создание в середине — остаёмся на месте (новая запись появляется
                // прямо под строкой-инициатором, в зоне видимости).
                if (TableViewCtrl.IsVisible)
                    TableViewCtrl.RestoreVerticalOffset(off);
            }
            else if (TableViewCtrl.IsVisible)
            {
                // Создание в конце — прокручиваем к новой записи, чтобы кнопка «+»
                // (add-row) осталась в видимой зоне при серии создания.
                TableViewCtrl.ScrollToRecord(rec.Id.Value);
            }

            if (open)
                RecordPageRequested?.Invoke(this, rec.Id.Value);
        }
    }

    private void DeleteRecord(int recordId)
    {
        if (_repository is null) return;

        // Удаление в середине таблицы — сохраняем вертикальную позицию, чтобы при
        // тысячах записей таблицу не откидывало в начало (снимаем offset ДО пересборки).
        var keepVertical = TableViewCtrl.IsVisible ? TableViewCtrl.CurrentVerticalOffset() : (double?)null;

        _repository.DeleteRecord(recordId);
        Refresh();
        CollectionsChanged?.Invoke(this, EventArgs.Empty);
        RecordPageClosed?.Invoke(this, EventArgs.Empty);

        if (keepVertical is { } off && TableViewCtrl.IsVisible)
            TableViewCtrl.RestoreVerticalOffset(off);
    }

    // Пакетное удаление записей (мультивыделение в таблице) с подтверждением — аналог
    // DeletePropertiesWithConfirm для свойств. Сохраняем вертикальную позицию, чтобы при
    // тысячах записей таблицу не откидывало в начало.
    private async Task DeleteRecordsWithConfirm(IReadOnlyList<int> recordIds)
    {
        if (_repository is null) return;
        if (VisualRoot is not Avalonia.Controls.Window win) return;

        var ids = recordIds.Distinct().ToList();
        if (ids.Count == 0) return;

        var dlg = new ConfirmDialog($"Вы удаляете {ids.Count} {RecordsPlural(ids.Count)}. Подтвердить?");
        if (await dlg.ShowDialog<bool>(win) != true) return;

        var keepVertical = TableViewCtrl.IsVisible ? TableViewCtrl.CurrentVerticalOffset() : (double?)null;

        foreach (var id in ids) _repository.DeleteRecord(id);
        Refresh();
        CollectionsChanged?.Invoke(this, EventArgs.Empty);
        RecordPageClosed?.Invoke(this, EventArgs.Empty);

        if (keepVertical is { } off && TableViewCtrl.IsVisible)
            TableViewCtrl.RestoreVerticalOffset(off);
    }

    // Русское склонение: 1 запись, 2–4 записи, 5+ записей (с учётом 11–14).
    private static string RecordsPlural(int n)
    {
        var mod100 = n % 100;
        var mod10  = n % 10;
        if (mod100 is >= 11 and <= 14) return "записей";
        return mod10 switch
        {
            1 => "запись",
            >= 2 and <= 4 => "записи",
            _ => "записей",
        };
    }

    private void DuplicateRecord(int recordId)
    {
        if (_repository is null) return;

        // Копия встаёт сразу после исходной (как создание после строки) — сохраняем
        // вертикальную позицию, чтобы таблицу не откидывало.
        var keepVertical = TableViewCtrl.IsVisible ? TableViewCtrl.CurrentVerticalOffset() : (double?)null;

        _repository.DuplicateRecord(recordId);
        Refresh();
        CollectionsChanged?.Invoke(this, EventArgs.Empty);

        if (keepVertical is { } off && TableViewCtrl.IsVisible)
            TableViewCtrl.RestoreVerticalOffset(off);
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

            // Создание нового свойства (fieldId == null) идёт через правый «+» — оно
            // добавляется в конец таблицы. Прокручиваем к правому краю, чтобы новая
            // колонка и кнопка «+» остались видны (как ScrollToRecord держит add-row «+»).
            if (fieldId is null && TableViewCtrl.IsVisible)
                TableViewCtrl.ScrollToAddPropertyColumn();

            CollectionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // Окно «Отображение» — менеджер видимости/уровней колонок. Список свойств берём
    // из таблицы (полный, включая скрытые). По «Сохранить» — обновляем таблицу.
    private async Task OpenDisplayDialog()
    {
        if (_currentCollectionId is null || _repository is null) return;
        if (VisualRoot is not Avalonia.Controls.Window win) return;

        var columns = TableViewCtrl.MovableColumnsInOrder();
        var dlg = new DisplayDialog(_repository, _currentCollectionId.Value, columns);
        var ok  = await dlg.ShowDialog<bool?>(win);
        if (ok == true)
            Refresh();
    }

    // Окно «Отображение записей» — менеджер видимости/уровней/порядка строк. Список
    // записей берём из БД (полный, включая скрытые — их там можно вернуть), в порядке
    // позиций. По «Сохранить» — обновляем таблицу.
    private async Task OpenRecordDisplayDialog()
    {
        if (_currentCollectionId is null || _repository is null) return;
        if (VisualRoot is not Avalonia.Controls.Window win) return;

        var records = _repository.GetRecords(_currentCollectionId.Value)
            .Select(r => (r.Id!.Value, r.Title ?? ""))
            .ToList();
        var dlg = new RecordDisplayDialog(_repository, _currentCollectionId.Value, records);
        var ok  = await dlg.ShowDialog<bool?>(win);
        if (ok == true)
            Refresh();
    }

    // Создать новое свойство и поставить его сразу справа от колонки afterKey
    // (колонка, на заголовке которой вызвали меню). afterKey — ключ колонки:
    // id поля как строка либо системный «__title__»/«__desc__».
    private async Task CreatePropertyAfter(string afterKey)
    {
        if (_currentCollectionId is null || _repository is null) return;
        if (VisualRoot is not Avalonia.Controls.Window win) return;

        // Порядок колонок и набор полей ДО создания — чтобы вычислить новое поле.
        var orderBefore = TableViewCtrl.MovableKeysInOrder();
        var idsBefore   = _repository.GetFields(_currentCollectionId.Value).Select(f => f.Id).ToHashSet();

        var dlg = new PropertyEditorDialog(_repository, _currentCollectionId.Value, null);
        var ok  = await dlg.ShowDialog<bool?>(win);
        if (ok != true) return;

        // Новое поле — то, которого не было до открытия диалога.
        var newField = _repository.GetFields(_currentCollectionId.Value)
            .FirstOrDefault(f => !idsBefore.Contains(f.Id));

        if (newField?.Id is { } newId)
        {
            // movableKeys для MoveColumnAfter = прежний порядок + новый ключ в конце
            // (как его кладёт AddField). Затем метод переставит его за afterKey.
            var movable = new List<string>(orderBefore) { newId.ToString() };
            _repository.MoveColumnAfter(_currentCollectionId.Value, newId.ToString(), afterKey, movable);
        }

        Refresh();
        CollectionsChanged?.Invoke(this, EventArgs.Empty);
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
        if (_repository is null || _currentCollectionId is null) return;

        // Порядок колонок ДО дублирования — чтобы поставить копию сразу справа от
        // исходной (как «Создать новое свойство»), а не в конец, если задан column_order.
        var orderBefore = TableViewCtrl.MovableKeysInOrder();

        var copy = _repository.DuplicateField(fieldId);

        if (copy.Id is { } copyId)
        {
            var movable = new List<string>(orderBefore) { copyId.ToString() };
            _repository.MoveColumnAfter(_currentCollectionId.Value, copyId.ToString(), fieldId.ToString(), movable);
        }

        Refresh();
        CollectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    // Пакетное удаление выделенных свойств (клавиша Delete) с подтверждением —
    // аналог DeleteRecordsWithConfirm в календаре. Обязательные поля отфильтровываются.
    private async Task DeletePropertiesWithConfirm(IReadOnlyList<int> fieldIds)
    {
        if (_currentCollectionId is null || _repository is null) return;
        if (VisualRoot is not Avalonia.Controls.Window win) return;

        var fields  = _repository.GetFields(_currentCollectionId.Value);
        var deletable = fieldIds
            .Distinct()
            .Where(id => fields.FirstOrDefault(f => f.Id == id) is { IsRequired: false })
            .ToList();
        if (deletable.Count == 0) return;

        var dlg = new ConfirmDialog($"Вы удаляете {deletable.Count} {PropertiesPlural(deletable.Count)}. Подтвердить?");
        if (await dlg.ShowDialog<bool>(win) != true) return;

        foreach (var id in deletable) _repository.DeleteField(id);
        Refresh();
        CollectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    // Русское склонение: 1 свойство, 2–4 свойства, 5+ свойств (с учётом 11–14).
    private static string PropertiesPlural(int n)
    {
        var mod100 = n % 100;
        var mod10  = n % 10;
        if (mod100 is >= 11 and <= 14) return "свойств";
        return mod10 switch
        {
            1 => "свойство",
            >= 2 and <= 4 => "свойства",
            _ => "свойств",
        };
    }
}
