using Avalonia.Controls;
using Avalonia.Threading;
using SmartBase.Controls.Dialogs;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Views.ConstructorDb;

public partial class ConstructorControllerView : UserControl
{
    private ConstructorRepository? _repository;
    private int? _currentCollectionId;

    // Все смонтированные БД — для окна «Расположение» записи (выбор целевой БД/коллекции,
    // в т.ч. перенос между БД). Заполняется из MainWindow (SetDatabases).
    private readonly List<DbOption> _dbs = [];

    public event EventHandler?      CollectionsChanged;

    /// <summary>Запрос открыть запись в правой панели (RecordPageView в MainWindow).</summary>
    public event EventHandler<int>? RecordPageRequested;

    /// <summary>Запрос открыть запись в ДРУГОЙ БД (чип межбазовой связи): (DbId цели, Id записи).</summary>
    public event EventHandler<(int DbId, int RecordId)>? CrossDbRecordPageRequested;

    /// <summary>Запрос закрыть правую панель (например, при удалении открытой записи).</summary>
    public event EventHandler?      RecordPageClosed;

    /// <summary>Запрос переключить видимость правой панели записи из навбара коллекции.</summary>
    public event EventHandler<bool>? RecordPanelToggled;

    /// <summary>Значение записи отредактировано прямо в таблице — для живой синхронизации открытой
    /// панели «Страница записи» (MainWindow обновит её, если это та же запись).</summary>
    public event EventHandler<int>? RecordValueEdited;

    /// <summary>Состав/имена полей коллекции изменились (добавление/правка/дубль/удаление свойства) —
    /// открытая панель «Страница записи» должна перестроить список свойств.</summary>
    public event EventHandler? FieldsChanged;

    private void NotifyFieldsChanged() => FieldsChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Текущая открытая коллекция (для MainWindow при открытии PageView).</summary>
    public int? CurrentCollectionId => _currentCollectionId;

    /// <summary>Синхронизирует визуальное состояние toggle-кнопки записи в навбаре.</summary>
    public void SetRecordPanelOpen(bool isOpen) => NavbarConstructor.SetRecordPanelOpen(isOpen);

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
        NavbarConstructor.ViewChanged += (_, mode) => SwitchView(mode);

        // Переименование коллекции прямо из заголовка навбара.
        NavbarConstructor.TitleEdited += (_, newName) => RenameCurrentCollection(newName);

        // Toggle правой панели записи в навбаре — пробрасываем в MainWindow.
        NavbarConstructor.RecordPanelToggled += (_, isOpen) => RecordPanelToggled?.Invoke(this, isOpen);

        // Высота строк и ширина столбцов переключаются из контекстных меню таблицы
        // (записи и заголовка колонки соответственно).

        // CRUD для записей — через контекстные меню в TableView/ListView.
        TableViewCtrl.RecordClicked             += (_, rid)  => OpenPage(rid);
        TableViewCtrl.CrossDbRecordRequested    += (_, t)    => CrossDbRecordPageRequested?.Invoke(this, t);
        TableViewCtrl.RecordValueEdited         += (_, rid)  => RecordValueEdited?.Invoke(this, rid);
        TableViewCtrl.RecordCreateRequested     += (_, e)    => _ = CreateAndOpen(e.Open, e.AfterId);
        TableViewCtrl.RecordsDeleteRequested    += (_, ids)  => _ = DeleteRecordsWithConfirm(ids);
        TableViewCtrl.RecordDuplicateRequested  += (_, ids)  => _ = DuplicateRecords(ids);
        TableViewCtrl.RecordRelocateRequested   += (_, ids)  => _ = RelocateRecords(ids);

        ListViewCtrl.RecordClicked              += (_, rid) => OpenPage(rid);
        ListViewCtrl.RecordCreateRequested      += (_, _)   => _ = CreateAndOpen();
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
        NavbarConstructor.SetTitle("");
        SetEmptyState(true);
    }

    // Пустое состояние (коллекция не выбрана): подсказка по центру, скрыты контролы
    // навбара (вид/запись) и нижняя панель — чтобы не показывать «Всего: 0» и пр.
    private void SetEmptyState(bool empty)
    {
        EmptyStateHint.IsVisible    = empty;
        FooterConstructor.IsVisible = !empty;
        NavbarConstructor.ShowCollectionControls(!empty);

        // Скрываем таблицу/список, чтобы пустая шапка DataGrid не висела серой полосой
        // под навбаром. При выборе коллекции SwitchView вернёт нужную видимость.
        if (empty)
        {
            TableViewCtrl.IsVisible = false;
            ListViewCtrl.IsVisible  = false;
        }
    }

    // Набор смонтированных БД для окна «Расположение» записи (выбор целевой БД/коллекции и
    // перенос между БД). Вызывается из MainWindow при старте и после монтирования/размонтирования.
    public void SetDatabases(IReadOnlyList<DatabaseManager> databases)
    {
        _dbs.Clear();
        foreach (var d in databases)
            _dbs.Add(new DbOption(d.DbId, string.IsNullOrWhiteSpace(d.Name) ? "База данных" : d.Name, d.Constructor));
    }

    private ConstructorRepository? RepoFor(int dbId) => _dbs.FirstOrDefault(d => d.Id == dbId)?.Repo;

    // БД текущей открытой коллекции — по ссылке на активный репозиторий (его экземпляр стабилен).
    private int CurrentDbId =>
        _dbs.FirstOrDefault(d => ReferenceEquals(d.Repo, _repository))?.Id ?? (_dbs.Count > 0 ? _dbs[0].Id : 0);

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
        NavbarConstructor.SetTitle(col.Name);
        SetEmptyState(false);

        var (_, _, viewMode) = _repository.GetUiSettings(collectionId);
        NavbarConstructor.SetViewMode(viewMode);
        SwitchView(viewMode);

        // Нижняя панель: счётчик записей и страницы (пока 1 страница).
        var count = _repository.CountRecords(collectionId);
        FooterConstructor.SetCount(count);
        FooterConstructor.SetPage(1, 1);
    }

    public void Refresh()
    {
        if (_currentCollectionId.HasValue)
            SelectCollection(_currentCollectionId.Value);
    }

    /// <summary>Пользователь сейчас редактирует ячейку таблицы — фоновая синхронизация
    /// чужих правок (ChangePoller) откладывает обновление, чтобы не затирать ввод.</summary>
    public bool IsCellEditing => TableViewCtrl.IsVisible && TableViewCtrl.IsCellEditing;

    /// <summary>Чужие правки записей из другой копии приложения (мультипользовательская
    /// синхронизация): обновить строки открытой коллекции на месте — без пересборки
    /// колонок и мелькания (число записей могло измениться → структурный путь).</summary>
    public void RefreshRowsRemote() => RefreshRows(structural: true);

    // Лёгкое обновление при изменении ТОЛЬКО строк (создание/удаление/дубль/сдвиг записей,
    // видимость записей): в таблице пересобираем только строки (ReloadRows) — набор колонок
    // не трогаем, поэтому при 1000 свойств нет «моргания» и тяжёлой пересборки. Для режима
    // «Список» (без множества колонок) — пока полный путь Refresh.
    // structural=true — изменилось ЧИСЛО записей (создание/удаление/дубль): строки обновляем на месте
    // без пересоздания списка и мелькания «Загрузка…» (ReloadRowsStructural). false — изменилась
    // ВИДИМОСТЬ записей (окно «Отображение»): набор фильтра по скрытым другой → нужен полный ReloadRows.
    private void RefreshRows(bool structural = false)
    {
        if (_currentCollectionId is null || _repository is null) return;

        if (TableViewCtrl.IsVisible)
        {
            if (structural) TableViewCtrl.ReloadRowsStructural();
            else            TableViewCtrl.ReloadRows();
            FooterConstructor.SetCount(_repository.CountRecords(_currentCollectionId.Value));
        }
        else
        {
            Refresh();
        }
    }

    // Точечное обновление ОДНОЙ строки после правки записи в правой панели (имя/описание/значения):
    // перечитываем только её данные и заполняем строку на месте. Прежде правка дёргала полный
    // Refresh (пересборка всей таблицы) — на больших коллекциях с множеством колонок это «вешало» UI.
    // Число записей не меняется → счётчик и набор колонок не трогаем. В «Списке» — полный путь.
    public void RefreshRecordRow(int recordId)
    {
        if (_currentCollectionId is null || _repository is null) return;
        if (TableViewCtrl.IsVisible) TableViewCtrl.RefreshRecordRow(recordId);
        else                         Refresh();
    }

    private void ClearView()
    {
        _currentCollectionId = null;
        NavbarConstructor.SetTitle("");
        TableViewCtrl.Clear();
        ListViewCtrl.Clear();
        FooterConstructor.SetCount(0);
        FooterConstructor.SetPage(1, 1);
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
    private async Task CreateAndOpen(bool open = true, int? afterRecordId = null)
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

        // Вставку делаем в ФОНЕ (изолированное подключение) — синхронная вставка-плюс-RefreshStructural
        // в одном кадре UI заставляла грид моргать при добавлении одной строки. Фоновый путь + await
        // (как у удаления/дубля нескольких) даёт гриду чистый кадр → без мерцания.
        var rec = await Task.Run(() => _repository.AddRecordIsolated(newRecord, afterRecordId));

        RefreshRows(structural: true);
        // CollectionsChanged НЕ зовём: новая пустая запись не меняет ни список коллекций/групп в
        // сайдбарах, ни события календаря (даты ещё нет). Прежний кросс-БД рефреш на каждое создание
        // давал заметную задержку (~1 с) на мультибазе. Появится дата — её правка обновит календарь сама.

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

            // Выделяем голубым ТОЛЬКО созданную запись (после Reset грид иначе оставляет
            // съехавшее/лишнее выделение по старым индексам).
            if (TableViewCtrl.IsVisible)
                TableViewCtrl.SelectRecords([rec.Id.Value]);

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
        RefreshRows(structural: true);
        TableViewCtrl.ClearRecordSelection();   // удалённая строка не должна оставаться «подсвеченной»
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

        // Bulk-удаление мимо EF (один каскадный DELETE пакетами) в фоне на изолированном подключении —
        // прежний цикл DeleteRecord с SaveChanges на каждую запись вешал UI на 1000+ строк. Индикатор
        // (RunBulkOp) появляется отложенно: мелкое удаление проходит без окна.
        var many = ids.Count > 1;
        await RunBulkOp(many ? "Удаление записей" : "Удаление записи",
            (p, setPhase) =>
            {
                setPhase(many ? "Удаление записей" : "Удаление записи");
                _repository.DeleteRecordsIsolated(ids, new IntProgress(done => p.Report((done, ids.Count))));
                p.Report((ids.Count, ids.Count));   // финал → «Сохранение…»
            });

        RefreshRows(structural: true);
        TableViewCtrl.ClearRecordSelection();   // удалённые строки не должны оставаться «подсвеченными»
        CollectionsChanged?.Invoke(this, EventArgs.Empty);
        RecordPageClosed?.Invoke(this, EventArgs.Empty);

        if (keepVertical is { } off && TableViewCtrl.IsVisible)
            TableViewCtrl.RestoreVerticalOffset(off);
    }

    // Дубль выбранных записей. Одна запись (меню строки) — копия сразу после исходной (как было),
    // мгновенно. Несколько — bulk-SQL в фоне с индикатором (как удаление), копии встают сразу после
    // нижней выделенной записи; иначе по-записный DuplicateRecord с SaveChanges на каждую вешал бы UI.
    private async Task DuplicateRecords(IReadOnlyList<int> recordIds)
    {
        if (_repository is null || _currentCollectionId is null) return;
        var ids = recordIds.Distinct().ToList();
        if (ids.Count == 0) return;

        var keepVertical = TableViewCtrl.IsVisible ? TableViewCtrl.CurrentVerticalOffset() : (double?)null;

        var newIds = new List<int>();   // id копий — выделим их голубым после операции
        if (ids.Count == 1)
        {
            // Копия сразу после исходной (по середине, без сдвига хвоста) — но в ФОНЕ (изолированно)
            // + await: синхронная вставка одной строки с немедленным refresh моргала; фоновый путь
            // (как у дубля нескольких) даёт гриду чистый кадр.
            var copy = await Task.Run(() => _repository.DuplicateRecordIsolated(ids[0]));
            if (copy.Id is { } cid) newIds.Add(cid);
        }
        else
        {
            var colId = _currentCollectionId.Value;
            await RunBulkOp("Дублирование записей",
                (p, setPhase) =>
                {
                    setPhase("Дублирование записей");
                    newIds = _repository.DuplicateRecordsIsolated(colId, ids, new IntProgress(done => p.Report((done, ids.Count))));
                    p.Report((ids.Count, ids.Count));   // финал → «Сохранение…»
                });
        }

        RefreshRows(structural: true);
        CollectionsChanged?.Invoke(this, EventArgs.Empty);

        if (keepVertical is { } off && TableViewCtrl.IsVisible)
            TableViewCtrl.RestoreVerticalOffset(off);

        // Выделяем голубым КОПИИ (а не исходники, которые оставались выделены раньше).
        if (TableViewCtrl.IsVisible)
            TableViewCtrl.SelectRecords(newIds);
    }

    // «Расположение»: перенос выбранных записей в другую коллекцию/БД. Окно (как у коллекций в
    // сайдбаре) выбирает целевую БД и коллекцию + «Перенести все свойства». Значения переносятся
    // грамотно (по имени поля, с приведением типов; см. ConstructorRepository.MoveRecordsToCollectionIsolated).
    // Перенос идёт на изолированных подключениях в фоне (мелкий объём — без окна прогресса), как
    // дубль/удаление. Запись исчезает из исходной коллекции (move = copy + delete).
    private async Task RelocateRecords(IReadOnlyList<int> recordIds)
    {
        if (_repository is null || _currentCollectionId is null || _dbs.Count == 0) return;
        if (VisualRoot is not Avalonia.Controls.Window win) return;

        var ids = recordIds.Distinct().ToList();
        if (ids.Count == 0) return;

        var dlg = new RecordLocationDialog(_dbs, CurrentDbId, _currentCollectionId);
        var res = await dlg.ShowDialog<RecordLocationResult?>(win);
        if (res is null || RepoFor(res.DbId) is not { } targetRepo) return;

        // Та же коллекция — переносить некуда (no-op).
        if (res.DbId == CurrentDbId && res.CollectionId == _currentCollectionId.Value) return;

        var keepVertical = TableViewCtrl.IsVisible ? TableViewCtrl.CurrentVerticalOffset() : (double?)null;

        var sourcePath = _repository.DbPath;
        var sourceCollectionId = _currentCollectionId.Value;
        var many = ids.Count > 1;
        await RunBulkOp(many ? "Перемещение записей" : "Перемещение записи",
            (p, setPhase) =>
            {
                setPhase(many ? "Перемещение записей" : "Перемещение записи");
                targetRepo.MoveRecordsToCollectionIsolated(sourcePath, sourceCollectionId, ids, res.CollectionId,
                    res.CreateMissingFields, new IntProgress(done => p.Report((done, ids.Count))));
                p.Report((ids.Count, ids.Count));   // финал → «Сохранение…»
            });

        RefreshRows(structural: true);
        TableViewCtrl.ClearRecordSelection();   // перенесённые строки исчезли — снимаем подсветку
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
        RefreshRows(structural: true);
        CollectionsChanged?.Invoke(this, EventArgs.Empty);

        if (keepVertical is { } off && TableViewCtrl.IsVisible)
            TableViewCtrl.RestoreVerticalOffset(off);
    }

    // ── Свойства (поля) ───────────────────────────────────────────────────────

    /// <summary>
    /// Создать новое свойство из панели записи («Добавить свойство»). Идёт тем же путём,
    /// что и «+» в таблице: открывает редактор свойства, добавляет колонку в таблицу и
    /// поднимает FieldsChanged — открытая панель записи перечитывает поля и сразу показывает новое.
    /// </summary>
    public void CreatePropertyFromPanel() => _ = OpenPropertyDialog(null);

    private async Task OpenPropertyDialog(int? fieldId)
    {
        if (_currentCollectionId is null || _repository is null) return;
        if (VisualRoot is not Avalonia.Controls.Window win) return;

        // Для создания (fieldId == null) запоминаем поля ДО — чтобы вычислить новое и
        // добавить ровно одну колонку, не пересобирая таблицу.
        var idsBefore = fieldId is null
            ? _repository.GetFields(_currentCollectionId.Value).Select(f => f.Id).ToHashSet()
            : null;

        var dlg = new PropertyEditorDialog(_repository, _currentCollectionId.Value, fieldId, CurrentDbId);
        var ok  = await dlg.ShowDialog<bool?>(win);
        if (ok != true) return;

        if (fieldId is { } editId)
        {
            // Изменение свойства (имя/тип/опции) — перестраиваем только эту колонку.
            TableViewCtrl.RebuildColumn(editId.ToString());
            // Авто-привязка файлов материализовала значения поля — перечитать строки, чтобы
            // подставленные файлы отобразились сразу (RebuildColumn обновляет лишь UI колонки).
            var edited = _repository.GetFields(_currentCollectionId.Value).FirstOrDefault(x => x.Id == editId);
            if (edited?.FieldType == "file" && FileAutoRule.FromConfig(edited.ConfigJson) is not null)
                TableViewCtrl.ReloadRows();
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

        NotifyFieldsChanged();
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

        // Список записей (полный, в порядке позиций) на больших коллекциях — сотни тысяч строк;
        // грузим его в ФОНЕ на отдельном подключении, чтобы открытие окна не подвешивало UI.
        // Если загрузка дольше короткого мгновения — показываем индикатор «Загрузка записей…».
        var colId = _currentCollectionId.Value;
        var loadTask = Task.Run(() => _repository.GetRecordTitlesOrderedIsolated(colId));

        ProgressDialog? busy = null;
        Task? busyClosed = null;
        if (await Task.WhenAny(loadTask, Task.Delay(FieldOpDialogDelayMs)) != loadTask)
        {
            busy = new ProgressDialog { Title = "Отображение записей" };
            busy.SetSaving("Загрузка записей…");
            busyClosed = busy.ShowDialog(win);
        }

        List<(int Id, string Title)> records;
        try { records = await loadTask; }
        finally { busy?.Done(); }
        if (busyClosed is not null) await busyClosed;

        var dlg = new RecordDisplayDialog(_repository, colId, records);
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

        var dlg = new PropertyEditorDialog(_repository, _currentCollectionId.Value, null, CurrentDbId);
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
        NotifyFieldsChanged();
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
            NavbarConstructor.SetTitle(col.Name);
            NavbarConstructor.ShowTitleError("Коллекция с таким названием уже существует");
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

        var copies = new List<(int Src, int Copy)>();   // исходник → созданная копия
        var many   = ids.Count > 1;

        // Всю тяжёлую работу (подсчёт значений + копирование) делаем в фоне на изолированных
        // подключениях — на главном потоке ничего не считаем, поэтому UI не «висит» до окна.
        // Окно прогресса покажется само, если работа не уложится в короткий момент (RunFieldOp).
        await RunBulkOp(many ? "Дублирование столбцов" : "Дублирование столбца",
            (p, setPhase) =>
            {
                var counts = _repository.CountFieldValuesIsolated(ids);
                var total  = counts.Sum(c => (long)c);
                long basis = 0;
                for (var i = 0; i < ids.Count; i++)
                {
                    setPhase(many ? $"Дублирование {i + 1}/{ids.Count} столбцов" : "Дублирование столбца");
                    var b = basis;
                    var copy = _repository.DuplicateFieldIsolated(ids[i],
                        new IntProgress(done => p.Report((b + done, total))));
                    if (copy.Id is { } cid) copies.Add((ids[i], cid));
                    basis += counts[i];
                }
                p.Report((total, total));   // финал → «Сохранение…»
            });

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
        NotifyFieldsChanged();
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

        // Надёжно (без осиротевших данных) и без фриза UI: подсчёт значений и удаление идут
        // в фоне на изолированных подключениях, на главном потоке ничего не считаем. Окно
        // прогресса покажется само, если работа не уложится в короткий момент (RunFieldOp).
        var manyCols = deletable.Count > 1;
        await RunBulkOp(manyCols ? "Удаление столбцов" : "Удаление столбца",
            (p, setPhase) =>
            {
                var counts = _repository.CountFieldValuesIsolated(deletable);
                var total  = counts.Sum(c => (long)c);
                // Поля удаляем по очереди (каждое — на своём подключении), показывая «N/M».
                long basis = 0;
                for (var i = 0; i < deletable.Count; i++)
                {
                    setPhase(manyCols ? $"Удаление {i + 1}/{deletable.Count} столбцов" : "Удаление столбца");
                    var b = basis;
                    _repository.DeleteFieldIsolated(deletable[i],
                        new IntProgress(done => p.Report((b + done, total))));
                    basis += counts[i];
                }
                p.Report((total, total));   // финал → «Сохранение…»
            });

        TableViewCtrl.ReloadColumns();   // колонка исчезает сразу; строки не пересобираем
        NotifyFieldsChanged();

        // Кросс-БД обновление (сайдбары + календарь по всем БД) после операции лишь тормозит и
        // НУЖНО только если удалили поле-источник календаря (IsCalStart): колонки не меняют ни
        // список коллекций, ни события календаря. Этот лишний рефреш и давал «зависон» после 100%.
        if (fields.Any(f => f.IsCalStart && f.Id is { } fid && deletable.Contains(fid)))
            CollectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    // Сентинел для lastShown: фаза «Сохранение…» (коммит) показана. Не пересекается с 0..100.
    private const int SavingPhase = -2;

    // Через сколько мс работы показывать окно прогресса. Мелкие операции (пара значений/записей)
    // укладываются раньше — для них окно не появляется вовсе. На больших объёмах окно всплывает
    // почти сразу, но UI при этом НЕ висит: вся работа уже в фоне.
    private const int FieldOpDialogDelayMs = 200;

    // Выполняет тяжёлую bulk-операцию (удаление/дубль свойств ИЛИ записей) ПОЛНОСТЬЮ в фоне (подсчёт +
    // сама работа — на изолированных подключениях; главный контекст из фонового потока не трогаем).
    // Окно прогресса показываем, только если работа не уложилась в FieldOpDialogDelayMs: раньше счёт/
    // удаление шли СИНХРОННО на UI-потоке (на сотнях тысяч строк — фриз на несколько секунд, и лишь
    // потом индикатор). work репортит (готово, всего); пока всего неизвестно/0 — индикатор крутится
    // «Подсчёт значений…». Окно МОДАЛЬНОЕ — вторую операцию нельзя начать, пока текущая не закрылась.
    private async Task RunBulkOp(string windowTitle,
                                 Action<IProgress<(long Done, long Total)>, Action<string>> work)
    {
        ProgressDialog? dlg = null;
        var lastShown = -1;          // -1 ещё не показывали %, -2 «Сохранение…», 0..100 — проценты
        var phase     = windowTitle;

        // Обновление окна (на UI-потоке). dlg может быть ещё/уже null — тогда отчёт игнорируем.
        void Update(long done, long total)
        {
            if (dlg is null || total <= 0) return;
            if (done >= total)
            {
                if (lastShown == SavingPhase) return;
                lastShown = SavingPhase;
                dlg.SetSaving("Сохранение…");
                return;
            }
            var pct = (int)(done * 100L / total);
            if (pct == lastShown) return;
            lastShown = pct;
            dlg.SetProgress(pct / 100.0, phase);
        }

        // PairProgress сам маршалит на UI-поток через Dispatcher (Report вызывается из фона).
        IProgress<(long, long)> p = new PairProgress((done, total) =>
            Dispatcher.UIThread.Post(() => Update(done, total)));
        Action<string> setPhase = ph => phase = ph;

        var bg = Task.Run(() => { try { work(p, setPhase); } catch { /* фон: ошибку не валим в UI */ } });

        // Успело быстро — окна не показываем вовсе.
        if (await Task.WhenAny(bg, Task.Delay(FieldOpDialogDelayMs)) == bg) { await bg; return; }

        if (VisualRoot is Avalonia.Controls.Window win)
        {
            dlg = new ProgressDialog { Title = windowTitle };
            dlg.SetSaving("Подсчёт значений…");   // пока total неизвестен — бегущая полоса
            var dlgClosed = dlg.ShowDialog(win);
            await bg;
            dlg.Done();
            await dlgClosed;
        }
        else
        {
            await bg;   // некуда показать окно — просто дожидаемся фон
        }
    }

    // Адаптер IProgress<int> для прогресса с фонового потока (передаётся в *Isolated-методы).
    private sealed class IntProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    // Прогресс (готово, всего) с фонового потока; report сам маршалит обновление окна на UI.
    private sealed class PairProgress(Action<long, long> report) : IProgress<(long Done, long Total)>
    {
        public void Report((long Done, long Total) v) => report(v.Done, v.Total);
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
