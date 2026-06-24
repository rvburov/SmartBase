using Avalonia.Controls;
using Avalonia.Threading;
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
        TableViewCtrl.PropertyDuplicateRequested += (_, fids) => _ = DuplicateProperties(fids);
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

    // Открыть коллекцию из конкретной БД: перенацеливаем модуль на её репозиторий.
    // В основной области одновременно открыта одна коллекция, поэтому достаточно
    // держать активным один репозиторий (мульти-маунт живёт в сайдбаре).
    public void OpenFrom(ConstructorRepository repository, int collectionId)
    {
        _repository = repository;
        SelectCollection(collectionId);
    }

    // БД текущей коллекции размонтирована (удалена в настройках): переключаемся на
    // запасной репозиторий и очищаем вид, чтобы не держать ссылку на закрытый контекст.
    public void DetachDatabase(ConstructorRepository fallback)
    {
        _repository = fallback;
        ClearView();
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

    // Лёгкое обновление при изменении ТОЛЬКО строк (создание/удаление/дубль/сдвиг записей,
    // видимость записей): в таблице пересобираем только строки (ReloadRows) — набор колонок
    // не трогаем, поэтому при 1000 свойств нет «моргания» и тяжёлой пересборки. Для режима
    // «Список» (без множества колонок) — пока полный путь Refresh.
    private void RefreshRows()
    {
        if (_currentCollectionId is null || _repository is null) return;

        if (TableViewCtrl.IsVisible)
        {
            TableViewCtrl.ReloadRows();
            ConstructorFooter.SetCount(_repository.CountRecords(_currentCollectionId.Value));
        }
        else
        {
            Refresh();
        }
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

        RefreshRows();
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
        RefreshRows();
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
        RefreshRows();
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
        RefreshRows();
        CollectionsChanged?.Invoke(this, EventArgs.Empty);

        if (keepVertical is { } off && TableViewCtrl.IsVisible)
            TableViewCtrl.RestoreVerticalOffset(off);
    }

    // ── Свойства (поля) ───────────────────────────────────────────────────────

    private async Task OpenPropertyDialog(int? fieldId)
    {
        if (_currentCollectionId is null || _repository is null) return;
        if (VisualRoot is not Avalonia.Controls.Window win) return;

        // Для создания (fieldId == null) запоминаем поля ДО — чтобы вычислить новое и
        // добавить ровно одну колонку, не пересобирая таблицу.
        var idsBefore = fieldId is null
            ? _repository.GetFields(_currentCollectionId.Value).Select(f => f.Id).ToHashSet()
            : null;

        var dlg = new PropertyEditorDialog(_repository, _currentCollectionId.Value, fieldId);
        var ok  = await dlg.ShowDialog<bool?>(win);
        if (ok != true) return;

        if (fieldId is { } editId)
        {
            // Изменение свойства (имя/тип/опции) — перестраиваем только эту колонку.
            TableViewCtrl.RebuildColumn(editId.ToString());
        }
        else
        {
            // Создание — новое поле добавляется в конец; вставляем одну колонку и
            // прокручиваем к правому краю (новая колонка и «+» остаются видны).
            var newField = _repository.GetFields(_currentCollectionId.Value)
                .FirstOrDefault(f => idsBefore is not null && !idsBefore.Contains(f.Id));
            if (newField?.Id is { } newId)
                TableViewCtrl.AddColumnForField(newId);
            else
                Refresh();   // не нашли новое поле — безопасный фоллбек

            if (TableViewCtrl.IsVisible)
                TableViewCtrl.ScrollToAddPropertyColumn();
        }

        CollectionsChanged?.Invoke(this, EventArgs.Empty);
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
            TableViewCtrl.ReloadColumns();   // только колонки (строки не сбрасываем); выделение работает
    }

    // Окно «Отображение записей» — менеджер видимости/уровней/порядка строк. Список
    // записей берём из БД (полный, включая скрытые — их там можно вернуть), в порядке
    // позиций. По «Сохранить» — обновляем таблицу.
    private async Task OpenRecordDisplayDialog()
    {
        if (_currentCollectionId is null || _repository is null) return;
        if (VisualRoot is not Avalonia.Controls.Window win) return;

        var records = _repository.GetRecordTitlesOrdered(_currentCollectionId.Value);
        var dlg = new RecordDisplayDialog(_repository, _currentCollectionId.Value, records);
        var ok  = await dlg.ShowDialog<bool?>(win);
        if (ok == true)
            RefreshRows();
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

        TableViewCtrl.ReloadColumns();   // только колонки, строки не сбрасываем
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
        TableViewCtrl.RebuildColumn(key);   // перестроить только этот заголовок, не всю таблицу
        CollectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Переименование коллекции ──────────────────────────────────────────────

    private void RenameCurrentCollection(string newName)
    {
        if (_currentCollectionId is null || _repository is null) return;

        var col = _repository.GetAllCollections().FirstOrDefault(c => c.Id == _currentCollectionId);
        if (col is null || col.Name == newName) return;

        // Имя занято другой коллекцией в той же группе → отклоняем (как диалоги «Создать»/«Изменить»):
        // возвращаем прежнее имя в заголовке и показываем всплывашку у самого заголовка.
        if (_repository.CollectionNameExists(newName, col.GroupId, excludeId: col.Id))
        {
            ConstructorNavbar.SetTitle(col.Name);
            ConstructorNavbar.ShowTitleError("Коллекция с таким названием уже существует");
            return;
        }

        col.Name = newName;
        _repository.UpdateCollection(col);
        CollectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task DuplicateProperties(IReadOnlyList<int> fieldIds)
    {
        if (_repository is null || _currentCollectionId is null) return;
        var ids = fieldIds.Distinct().ToList();
        if (ids.Count == 0) return;

        // Порядок колонок ДО дублирования — чтобы каждую копию поставить сразу справа от её
        // исходника (как одиночный дубль), даже если задан column_order.
        var orderBefore = TableViewCtrl.MovableKeysInOrder();
        var counts      = ids.Select(id => _repository.CountFieldValues(id)).ToList();
        var total       = counts.Sum(c => (long)c);

        var copies = new List<(int Src, int Copy)>();   // исходник → созданная копия

        // Маленький объём — мгновенно синхронно; большой — окно прогресса с % и копирование
        // значений в фоне (на отдельном подключении), по колонкам с фазой «N/M столбцов».
        if (total <= FieldBulkThreshold || VisualRoot is not Avalonia.Controls.Window win)
        {
            foreach (var id in ids)
            {
                var copy = _repository.DuplicateField(id);
                if (copy.Id is { } cid) copies.Add((id, cid));
            }
        }
        else
        {
            var many = ids.Count > 1;
            await RunFieldOpWithProgress(many ? "Дублирование столбцов" : "Дублирование столбца", total,
                (p, setPhase) =>
                {
                    long basis = 0;
                    for (var i = 0; i < ids.Count; i++)
                    {
                        setPhase(many ? $"Дублирование {i + 1}/{ids.Count} столбцов" : "Дублирование столбца");
                        var b = basis;
                        var copy = _repository.DuplicateFieldIsolated(ids[i], new IntProgress(done => p.Report((int)(b + done))));
                        if (copy.Id is { } cid) copies.Add((ids[i], cid));
                        basis += counts[i];
                    }
                    return true;
                }, win);
        }

        // Каждую копию — сразу за её исходником, одним пересохранением порядка колонок.
        if (copies.Count > 0)
        {
            var copyBySrc  = copies.ToDictionary(c => c.Src.ToString(), c => c.Copy.ToString());
            var finalOrder = new List<string>();
            foreach (var key in orderBefore)
            {
                finalOrder.Add(key);
                if (copyBySrc.TryGetValue(key, out var copyKey)) finalOrder.Add(copyKey);
            }
            _repository.SaveColumnOrder(_currentCollectionId.Value, finalOrder);
        }

        // У копий есть значения для СУЩЕСТВУЮЩИХ строк, а кэш строк их не знает (грузились до
        // создания копий) — обновляем и строки, иначе у уже отрисованных строк новые колонки
        // показывали бы ПУСТО. (У удаления/сдвига данные строк не меняются — там хватает ReloadColumns.)
        var keepV = TableViewCtrl.CurrentVerticalOffset();
        TableViewCtrl.ReloadColumns();
        TableViewCtrl.ReloadRows();
        TableViewCtrl.RestoreVerticalOffset(keepV);
        // CollectionsChanged не нужен: копии не источники календаря, список коллекций не меняется.
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

        // Надёжно (синхронно, без осиротевших данных): маленькие поля — мгновенно; большие —
        // окно прогресса с % и удаление значений в фоне (на отдельном подключении).
        var counts = deletable.Select(id => _repository.CountFieldValues(id)).ToList();
        var total  = counts.Sum(c => (long)c);
        if (total <= FieldBulkThreshold)
        {
            foreach (var id in deletable) _repository.DeleteField(id);
        }
        else
        {
            var manyCols = deletable.Count > 1;
            await RunFieldOpWithProgress(manyCols ? "Удаление столбцов" : "Удаление столбца", total,
                (p, setPhase) =>
                {
                    // Поля удаляем по очереди (каждое — на своём подключении), показывая «N/M».
                    long basis = 0;
                    for (var i = 0; i < deletable.Count; i++)
                    {
                        setPhase(manyCols ? $"Удаление {i + 1}/{deletable.Count} столбцов" : "Удаление столбца");
                        var b = basis;
                        _repository.DeleteFieldIsolated(deletable[i], new IntProgress(done => p.Report((int)(b + done))));
                        basis += counts[i];
                    }
                    return true;
                }, win);
        }

        TableViewCtrl.ReloadColumns();   // колонка исчезает сразу; строки не пересобираем

        // Кросс-БД обновление (сайдбары + календарь по всем БД) после операции лишь тормозит и
        // НУЖНО только если удалили поле-источник календаря (IsCalStart): колонки не меняют ни
        // список коллекций, ни события календаря. Этот лишний рефреш и давал «зависон» после 100%.
        if (fields.Any(f => f.IsCalStart && f.Id is { } fid && deletable.Contains(fid)))
            CollectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    // Порог значений: меньше — операция с полем идёт мгновенно синхронно (без окна); больше —
    // окно прогресса с % и работа в фоне на отдельном подключении (как bulk-операции в сайдбаре).
    private const int FieldBulkThreshold = 20000;

    // Сентинел для lastPct: фаза «Сохранение…» (коммит) показана. Не пересекается с 0..100.
    private const int SavingPhase = -2;

    // Окно прогресса + фоновая работа (внутри work — на отдельном подключении) с % по значениям.
    // Возвращает результат work (или default при ошибке/прерывании).
    private async Task<T> RunFieldOpWithProgress<T>(string windowTitle, long total,
                                                    Func<IProgress<int>, Action<string>, T> work,
                                                    Avalonia.Controls.Window win)
    {
        var dlg = new ProgressDialog { Title = windowTitle };
        dlg.SetInfo($"Значений: {total}");
        var dlgClosed = dlg.ShowDialog(win);

        var lastPct   = -1;
        var lastPhase = "";
        var phase     = windowTitle;
        T result = default!;
        try
        {
            await Task.Run(() =>
            {
                // work задаёт текст фазы (напр. «Удаление 2/3 столбцов») через setPhase.
                Action<string> setPhase = ph => phase = ph;
                IProgress<int> p = new IntProgress(done =>
                {
                    // Все значения обработаны → впереди коммит/чек-пойнт большой транзакции (на
                    // 400k+ это секунды записи на диск). Бегущая фаза «Сохранение…», чтобы 100%
                    // не выглядели зависшими.
                    if (done >= total)
                    {
                        if (lastPct == SavingPhase) return;
                        lastPct = SavingPhase;
                        Dispatcher.UIThread.Post(() => dlg.SetSaving("Сохранение…"));
                        return;
                    }
                    var pct = total == 0 ? 100 : (int)(done * 100L / total);
                    var ph  = phase;
                    if (pct == lastPct && ph == lastPhase) return;
                    lastPct = pct; lastPhase = ph;
                    Dispatcher.UIThread.Post(() => dlg.SetProgress(pct / 100.0, ph));
                });
                result = work(p, setPhase);
            });
        }
        catch { /* фон: ошибку не валим в UI */ }

        dlg.Done();
        await dlgClosed;
        return result;
    }

    // Адаптер IProgress<int> для прогресса с фонового потока (сам маршалит на UI через Dispatcher);
    // Progress<T> здесь не годится — создаётся в Task.Run и захватил бы фоновый контекст.
    private sealed class IntProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
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
