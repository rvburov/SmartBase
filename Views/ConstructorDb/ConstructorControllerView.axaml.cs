using Avalonia.Controls;
using Avalonia.Threading;
using SmartBase.Controls.Components;
using SmartBase.Controls.Dialogs;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Integrations;
using SmartBase.Utils;

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
    /// панели «Страница записи» (MainWindow обновит ее, если это та же запись).</summary>
    public event EventHandler<int>? RecordValueEdited;

    /// <summary>Состав/имена полей коллекции изменились (добавление/правка/дубль/удаление свойства) —
    /// открытая панель «Страница записи» должна перестроить список свойств.</summary>
    public event EventHandler? FieldsChanged;

    private void NotifyFieldsChanged() { Touch(); FieldsChanged?.Invoke(this, EventArgs.Empty); }

    /// <summary>
    /// Момент, на который показанное соответствует базе (<see cref="ChangeBus.Now"/>). Ставится
    /// при каждом чтении коллекции и после каждой своей правки, уже отраженной на месте. Главное
    /// окно по своим коммитам (панель записи, календарь, задачи, расширения) перечитывает
    /// таблицу, только если коммит новее этой отметки, — своя правка лишней перезагрузки не дает.
    /// </summary>
    public long DataStamp { get; private set; }

    private void Touch() => DataStamp = ChangeBus.Now();

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

        // Переключатель вида остается в верхнем навбаре.
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
        TableViewCtrl.RecordValueEdited         += (_, rid)  => { Touch(); RecordValueEdited?.Invoke(this, rid); };
        TableViewCtrl.RecordCreateRequested     += (_, e)    => _ = CreateAndOpen(e.Open, e.AfterId);
        TableViewCtrl.RecordsDeleteRequested    += (_, ids)  => _ = DeleteRecordsWithConfirm(ids);
        TableViewCtrl.RecordDuplicateRequested  += (_, ids)  => _ = DuplicateRecords(ids);
        TableViewCtrl.RecordRelocateRequested   += (_, ids)  => _ = RelocateRecords(ids);

        ListViewCtrl.RecordClicked              += (_, rid) => OpenPage(rid);
        ListViewCtrl.RecordCreateRequested      += (_, _)   => _ = CreateAndOpen();
        ListViewCtrl.RecordDeleteRequested      += (_, rid) => _ = DeleteRecord(rid);

        // CRUD для свойств — через контекстное меню заголовков колонок.
        TableViewCtrl.PropertyCreateRequested    += (_, _)   => _ = OpenPropertyDialog(null);
        TableViewCtrl.PropertyCreateAfterRequested += (_, key) => _ = CreatePropertyAfter(key);
        TableViewCtrl.PropertyEditRequested      += (_, fid) => _ = OpenPropertyDialog(fid);
        TableViewCtrl.PropertyDuplicateRequested += (_, fids) => _ = DuplicateProperties(fids);
        TableViewCtrl.PropertiesDeleteRequested  += (_, ids) => _ = DeletePropertiesWithConfirm(ids);
        TableViewCtrl.SystemFieldRenameRequested += (_, key) => _ = RenameSystemField(key);
        TableViewCtrl.ColumnDisplayRequested     += (_, _)   => _ = OpenDisplayDialog();
        TableViewCtrl.RecordDisplayRequested     += (_, _)   => _ = OpenRecordDisplayDialog();

        // Навигация по строкам из нижней панели. Таблица виртуализирована и прокручивается
        // непрерывно, поэтому «‹›» двигают окно ровно на экран, а «‹‹››» ставят его к краю;
        // индикатор («10 – 50 из 125 437») таблица отдает сама, когда окно съехало.
        FooterConstructor.FirstRowClicked += (_, _) => TableViewCtrl.ScrollRowsToStart();
        FooterConstructor.PrevPageClicked += (_, _) => TableViewCtrl.ScrollRowsPageBack();
        FooterConstructor.NextPageClicked += (_, _) => TableViewCtrl.ScrollRowsPageForward();
        FooterConstructor.LastRowClicked  += (_, _) => TableViewCtrl.ScrollRowsToEnd();
        TableViewCtrl.VisibleRangeChanged += (_, range) => FooterConstructor.SetRange(range);
        TableViewCtrl.CellSelectionChanged += (_, label) => FooterConstructor.SetSelection(label);

        // Точки вызова обмена: список собирается здесь, а показывает его таблица. Охват здесь —
        // ВЫДЕЛЕННЫЕ записи, поэтому сценариев в списке нет: у сценария нет понятия «эти три
        // записи». Вся коллекция целиком — из контекстного меню сайдбара.
        TableViewCtrl.DataActions = DataActions;
        TableViewCtrl.DatabaseId  = () => CurrentDbId;

        // Стартовое состояние: коллекция не выбрана — заголовок-плейсхолдер и подсказка.
        NavbarConstructor.SetTitle("");
        SetEmptyState(true);
    }

    /// <summary>
    /// Подключить обмен (docs/ROADMAP-API.md, шаг 6). Не вызвали — точек вызова просто нет:
    /// раздел данных обязан работать и без настроенных интеграций.
    /// </summary>
    /// <param name="integrations">Реестр интеграций из app.db.</param>
    /// <param name="databases">Открытые базы — их спрашивают на момент запуска, а не при
    /// подключении: база могла закрыться, пока коллекция висела на экране.</param>
    public void AttachIntegrations(Database.IntegrationsRegistry?        integrations,
                                   Func<IReadOnlyList<DatabaseManager>>? databases)
    {
        _integrations   = integrations;
        _integrationDbs = databases;
    }

    private Database.IntegrationsRegistry?        _integrations;
    private Func<IReadOnlyList<DatabaseManager>>? _integrationDbs;

    /// <summary>
    /// Действия обмена для открытой коллекции; пусто — показывать нечего.
    ///
    /// Сценариев здесь нет намеренно: охват меню строки — ВЫДЕЛЕННЫЕ записи, а у сценария нет
    /// понятия «эти три записи». Коллекция целиком запускается из сайдбара.
    /// </summary>
    private IReadOnlyList<DataAction> DataActions() =>
        VisualRoot is Window window && _currentCollectionId is int collection
            ? IntegrationActions.Build(window, _integrations, _integrationDbs?.Invoke() ?? [],
                                       scenarioRun: null, CurrentDbId, collection,
                                       scenarios: false)
            : [];

    // Пустое состояние (коллекция не выбрана): подсказка по центру, скрыты контролы
    // навбара (вид/запись) и нижняя панель — показывать навигацию по несуществующей
    // таблице не над чем.
    private void SetEmptyState(bool empty)
    {
        EmptyStateHint.IsVisible    = empty;
        FooterConstructor.IsVisible = !empty;
        NavbarConstructor.ShowCollectionControls(!empty);

        // Скрываем таблицу/список, чтобы пустая шапка DataGrid не висела серой полосой
        // под навбаром. При выборе коллекции SwitchView вернет нужную видимость.
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

    // Открыть коллекцию из конкретной БД: перенацеливаем модуль на ее репозиторий.
    // В основной области одновременно открыта одна коллекция, поэтому достаточно
    // держать активным один репозиторий (мульти-маунт живет в сайдбаре).
    public void OpenFrom(ConstructorRepository repository, int collectionId) =>
        _ = OpenAsync(repository, collectionId);

    // Открытие по клику в сайдбаре: данные коллекции читаются в фоне одним снимком, UI-поток
    // только строит таблицу. На медленной сетевой папке прежний синхронный путь замораживал окно
    // на секунды («Интерфейс не отвечал 6,7 с — выполнялось: открытие коллекции»). Пока снимок
    // читается, человек может кликнуть другую коллекцию — устаревший результат отбрасывается по
    // счетчику поколений. Снимок не прочитался (база занята/пропала) — синхронный путь как раньше,
    // он покажет пустой вид или ошибку тем же порядком.
    private int _openGeneration;

    private async Task OpenAsync(ConstructorRepository repository, int collectionId)
    {
        var gen = ++_openGeneration;
        _repository = repository;
        NavbarConstructor.SetTitle("Открываю…");

        CollectionOpenSnapshot? snapshot = null;
        using (SmartBase.Logging.Diag.Measure("чтение коллекции", SmartBase.Logging.Diag.UiOpen,
                                              $"{SmartBase.Utils.DbErrors.Display(repository.DbPath)}, коллекция {collectionId}"))
        {
            try { snapshot = await repository.ReadOpenSnapshotAsync(collectionId); }
            catch (Exception ex) { SmartBase.Utils.DbErrors.HandleRead(ex, repository.DbPath); }
        }

        if (gen != _openGeneration || !ReferenceEquals(_repository, repository)) return;
        SelectCollection(collectionId, snapshot);
    }

    // Синхронный путь (Refresh, смена вида): читает базу сам.
    public void SelectCollection(int collectionId) => SelectCollection(collectionId, null);

    private void SelectCollection(int collectionId, CollectionOpenSnapshot? snapshot)
    {
        var col = snapshot is not null
            ? snapshot.Collection
            : _repository!.GetAllCollections().FirstOrDefault(c => c.Id == collectionId);
        if (col is null)
        {
            // Коллекция отсутствует (например, удалена) — очищаем view.
            ClearView();
            return;
        }

        Touch();
        _currentCollectionId = collectionId;
        NavbarConstructor.SetTitle(col.Name);
        SetEmptyState(false);

        var (_, _, viewMode) = snapshot?.UiSettings ?? _repository!.GetUiSettings(collectionId);
        NavbarConstructor.SetViewMode(viewMode);
        SwitchView(viewMode, snapshot);


        // Нижняя панель: диапазон видимых строк приедет от таблицы, когда она разложится
        // (VisibleRangeChanged) — до тех пор кнопки погашены. Своего счетчика записей у
        // панели нет, поэтому и CountRecords при открытии коллекции больше не считаем:
        // на большой коллекции это был заметный запрос на UI-потоке.
        FooterConstructor.SetRange(TableViewportRange.Empty);
    }

    // БД текущей коллекции размонтирована (удалена в настройках): переключаемся на
    // запасной репозиторий и очищаем вид, чтобы не держать ссылку на закрытый контекст.
    public void DetachDatabase(ConstructorRepository fallback)
    {
        _repository = fallback;
        ClearView();
    }

    public void Refresh()
    {
        if (_currentCollectionId.HasValue)
            SelectCollection(_currentCollectionId.Value);
    }

    /// <summary>Пользователь сейчас редактирует ячейку таблицы — фоновая синхронизация
    /// чужих правок (ChangePoller) откладывает обновление, чтобы не затирать ввод.</summary>
    public bool IsCellEditing => TableViewCtrl.IsVisible && TableViewCtrl.IsCellEditing;

    /// <summary>Закрыть открытую правку ячейки с записью значения (закрытие окна, уход в фон).</summary>
    public void CommitCellEdit() => TableViewCtrl.CommitCellEdit();

    /// <summary>Чужие правки записей из другой копии приложения (мультипользовательская
    /// синхронизация): обновить строки открытой коллекции на месте — без пересборки
    /// колонок и мелькания (число записей могло измениться → структурный путь).</summary>
    public void RefreshRowsRemote() => RefreshRows(structural: true);

    // Легкое обновление при изменении ТОЛЬКО строк (создание/удаление/дубль/сдвиг записей,
    // видимость записей): в таблице пересобираем только строки (ReloadRows) — набор колонок
    // не трогаем, поэтому при 1000 свойств нет «моргания» и тяжелой пересборки. Для режима
    // «Список» (без множества колонок) — пока полный путь Refresh.
    // structural=true — изменилось ЧИСЛО записей (создание/удаление/дубль): строки обновляем на месте
    // без пересоздания списка и мелькания «Загрузка…» (ReloadRowsStructural). false — изменилась
    // ВИДИМОСТЬ записей (окно «Отображение»): набор фильтра по скрытым другой → нужен полный ReloadRows.
    private void RefreshRows(bool structural = false)
    {
        if (_currentCollectionId is null || _repository is null) return;
        Touch();

        // Число записей нижней панели больше не нужно (счетчика нет) — его пересчет
        // отсюда убран; индикатор диапазона таблица обновит сама, когда разложит строки.
        if (TableViewCtrl.IsVisible)
        {
            if (structural) TableViewCtrl.ReloadRowsStructural();
            else            TableViewCtrl.ReloadRows();
        }
        else
        {
            Refresh();
        }
    }

    // Точечное обновление ОДНОЙ строки после правки записи в правой панели (имя/описание/значения):
    // перечитываем только ее данные и заполняем строку на месте. Прежде правка дергала полный
    // Refresh (пересборка всей таблицы) — на больших коллекциях с множеством колонок это «вешало» UI.
    // Число записей не меняется → счетчик и набор колонок не трогаем. В «Списке» — полный путь.
    public void RefreshRecordRow(int recordId)
    {
        if (_currentCollectionId is null || _repository is null) return;
        Touch();
        if (TableViewCtrl.IsVisible) TableViewCtrl.RefreshRecordRow(recordId);
        else                         Refresh();
    }

    private void ClearView()
    {
        _currentCollectionId = null;
        NavbarConstructor.SetTitle("");
        TableViewCtrl.Clear();
        ListViewCtrl.Clear();
        FooterConstructor.SetRange(TableViewportRange.Empty);
        SetEmptyState(true);
        RecordPageClosed?.Invoke(this, EventArgs.Empty);
    }

    private void SwitchView(string mode, CollectionOpenSnapshot? snapshot = null)
    {
        TableViewCtrl.IsVisible = mode != "list";
        ListViewCtrl.IsVisible  = mode == "list";

        // Навигация нижней панели двигает таблицу — в «Списке» ее гасим (индикатор от
        // прошлой таблицы врал бы про видимые строки). В таблицу диапазон вернется
        // первым же проходом разметки.
        if (mode == "list")
        {
            FooterConstructor.SetRange(TableViewportRange.Empty);
            FooterConstructor.SetSelection(null);   // выделение ячеек — у таблицы, в «Списке» его нет
        }

        if (_currentCollectionId.HasValue && _repository is not null)
        {
            if (mode == "list")
                ListViewCtrl.Load(_repository, _currentCollectionId.Value);
            else
                TableViewCtrl.Load(_repository, _currentCollectionId.Value, snapshot);
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
        // (как у удаления/дубля нескольких) дает гриду чистый кадр → без мерцания.
        var rec = await Task.Run(() => _repository.AddRecordIsolated(newRecord, afterRecordId));

        RefreshRows(structural: true);
        // CollectionsChanged НЕ зовем: новая пустая запись не меняет ни список коллекций/групп в
        // сайдбарах, ни события календаря (даты еще нет). Прежний кросс-БД рефреш на каждое создание
        // давал заметную задержку (~1 с) на мультибазе. Появится дата — ее правка обновит календарь сама.

        if (rec.Id.HasValue)
        {
            if (keepVertical is { } off)
            {
                // Создание в середине — остаемся на месте (новая запись появляется
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

    private async Task DeleteRecord(int recordId)
    {
        if (_repository is null) return;

        // Предупреждаем ТОЛЬКО если удаление оборвет входящие связи (иначе — мгновенно, как раньше):
        // на эту запись ссылались другие → у них станет «связи нет».
        if (RelationDeleteImpact.ForRecords(_repository, CurrentDbId, [recordId])
            && VisualRoot is Avalonia.Controls.Window confirmWin)
        {
            var warnDlg = new ConfirmDialog("Удалить запись? На нее ссылаются связи — они будут удалены.");
            if (await warnDlg.ShowDialog<bool>(confirmWin) != true) return;
        }

        // Удаление в середине таблицы — сохраняем вертикальную позицию, чтобы при
        // тысячах записей таблицу не откидывало в начало (снимаем offset ДО пересборки).
        var keepVertical = TableViewCtrl.IsVisible ? TableViewCtrl.CurrentVerticalOffset() : (double?)null;

        // Право могли отозвать, пока меню было открыто: DbErrors покажет причину окном.
        try { _repository.DeleteRecord(recordId); }
        catch (Exception ex) when (DbErrors.Handle(ex, _repository.DbPath)) { return; }
        RefreshRows(structural: true);
        TableViewCtrl.ClearRecordSelection();   // удаленная строка не должна оставаться «подсвеченной»
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

        // Предупреждаем, что удаление оборвет ВХОДЯЩИЕ связи на эти записи (внутрибазовые + межбазовые
        // из открытых баз). Best-effort: связи из закрытых баз чистятся лениво. Показываем лишь ФАКТ.
        var linkWarn = RelationDeleteImpact.ForRecords(_repository, CurrentDbId, ids)
            ? " На них ссылаются связи из других записей — они будут удалены." : "";
        // Рутинный вопрос снимает настройка «Спрашивать подтверждение при удалении», а вот
        // предупреждение об обрыве связей она НЕ снимает: это про потерю чужих данных, о
        // которой из самого действия не догадаться.
        if (ConstructorSettings.ConfirmDelete || linkWarn.Length > 0)
        {
            var dlg = new ConfirmDialog($"Вы удаляете {ids.Count} {RecordsPlural(ids.Count)}.{linkWarn} Подтвердить?");
            if (await dlg.ShowDialog<bool>(win) != true) return;
        }

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
        TableViewCtrl.ClearRecordSelection();   // удаленные строки не должны оставаться «подсвеченными»
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
            // (как у дубля нескольких) дает гриду чистый кадр.
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
    // Перенос идет на изолированных подключениях в фоне (мелкий объем — без окна прогресса), как
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

        // Все потери — в ОДНО окно: два-три подтверждения подряд просто «прокликивают».
        var warnings = new List<string>();

        // Свойства без пары в целевой коллекции значения не переносят, а несовместимое одноименное
        // поле-связь теряет связи — раньше и то и другое происходило МОЛЧА. Считается по схеме
        // (два списка полей), поэтому дешево независимо от числа переносимых записей.
        var (dropped, linksDropped) = targetRepo.FieldsLostOnMove(
            _repository, _currentCollectionId.Value, res.CollectionId,
            res.CreateMissingFields, CurrentDbId, res.DbId);
        if (dropped.Count > 0)
            warnings.Add($"• не перенесутся свойства: {NameList(dropped)}");
        if (linksDropped.Count > 0)
            warnings.Add($"• потеряются связи в полях: {NameList(linksDropped)}");

        // Перенос = копия + удаление (у записи новый id), поэтому ВХОДЯЩИЕ связи на нее обрываются
        // (за записью не следуют).
        if (RelationDeleteImpact.ForRecords(_repository, CurrentDbId, ids))
            warnings.Add("• оборвутся связи, ссылающиеся на переносимые записи (станут «связи нет»)");

        if (warnings.Count > 0)
        {
            var warnDlg = new ConfirmDialog(
                "При переносе:\n" + string.Join("\n", warnings) + "\n\nПродолжить?",
                confirmText: "Перенести");
            if (await warnDlg.ShowDialog<bool>(win) != true) return;
        }

        var keepVertical = TableViewCtrl.IsVisible ? TableViewCtrl.CurrentVerticalOffset() : (double?)null;

        var sourcePath = _repository.DbPath;
        var sourceCollectionId = _currentCollectionId.Value;
        var many = ids.Count > 1;
        await RunBulkOp(many ? "Перемещение записей" : "Перемещение записи",
            (p, setPhase) =>
            {
                setPhase(many ? "Перемещение записей" : "Перемещение записи");
                targetRepo.MoveRecordsToCollectionIsolated(sourcePath, sourceCollectionId, ids, res.CollectionId,
                    res.CreateMissingFields, CurrentDbId, res.DbId, new IntProgress(done => p.Report((done, ids.Count))));
                p.Report((ids.Count, ids.Count));   // финал → «Сохранение…»
            });

        RefreshRows(structural: true);
        TableViewCtrl.ClearRecordSelection();   // перенесенные строки исчезли — снимаем подсветку
        CollectionsChanged?.Invoke(this, EventArgs.Empty);
        RecordPageClosed?.Invoke(this, EventArgs.Empty);

        if (keepVertical is { } off && TableViewCtrl.IsVisible)
            TableViewCtrl.RestoreVerticalOffset(off);
    }

    // Имена свойств для окна: первые 8 + «и еще N». Ограничение чисто визуальное (при коллекции на
    // сотню полей окно растянулось бы на экран) — на стоимость проверки не влияет.
    private static string NameList(IReadOnlyList<string> names) =>
        names.Count <= 8
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(8)) + $" и еще {names.Count - 8}";

    // Русское склонение: 1 запись, 2–4 записи, 5+ записей (с учетом 11–14).
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

        // Копия встает сразу после исходной (как создание после строки) — сохраняем
        // вертикальную позицию, чтобы таблицу не откидывало.
        var keepVertical = TableViewCtrl.IsVisible ? TableViewCtrl.CurrentVerticalOffset() : (double?)null;

        try { _repository.DuplicateRecord(recordId); }
        catch (Exception ex) when (DbErrors.Handle(ex, _repository.DbPath)) { return; }
        RefreshRows(structural: true);
        CollectionsChanged?.Invoke(this, EventArgs.Empty);

        if (keepVertical is { } off && TableViewCtrl.IsVisible)
            TableViewCtrl.RestoreVerticalOffset(off);
    }

    // ── Свойства (поля) ───────────────────────────────────────────────────────

    /// <summary>
    /// Создать новое свойство из панели записи («Добавить свойство»). Идет тем же путем,
    /// что и «+» в таблице: открывает редактор свойства, добавляет колонку в таблицу и
    /// поднимает FieldsChanged — открытая панель записи перечитывает поля и сразу показывает новое.
    /// </summary>
    public void CreatePropertyFromPanel() => _ = OpenPropertyDialog(null);

    /// <summary>
    /// Подключить панель записи к операциям над свойствами: контекстное меню ее карточек и
    /// клавиша удаления ведут ровно туда же, куда меню заголовка колонки, — в эти же
    /// обработчики. Иначе два места, где живут свойства, разошлись бы поведением.
    ///
    /// Порядок и цвет свойств выполняет ТАБЛИЦА (там column_meta, кеши стилей и точечная
    /// перестройка грида), поэтому панель их не делает сама, а просит через контроллер.
    /// Панель живет в MainWindow — отсюда явное подключение, а не подписка в разметке.
    /// </summary>
    public void AttachRecordPanel(RecordPageView panel)
    {
        panel.PropertyEditRequested        += (_, fid)  => _ = OpenPropertyDialog(fid);
        panel.PropertyDuplicateRequested   += (_, fids) => _ = DuplicateProperties(fids);
        panel.PropertiesDeleteRequested    += (_, fids) => _ = DeletePropertiesWithConfirm(fids);
        panel.PropertyCreateAfterRequested += (_, key)  => _ = CreatePropertyAfter(key);
        panel.PropertyDisplayRequested     += (_, _)    => _ = OpenDisplayDialog();

        panel.PropertyMoveRequested += (_, m) =>
        {
            TableViewCtrl.MoveProperty(m.FieldId, m.Up);
            NotifyFieldsChanged();   // панель перечитает свойства в новом порядке
        };
        panel.PropertyColorRequested += (_, c) =>
            TableViewCtrl.ApplyPropertyStyle(c.FieldIds, c.Aspect, c.Hex);
        panel.PropertyAutoFileRefreshRequested += (_, fid) =>
        {
            TableViewCtrl.RefreshAutoFileProperty(fid);
            NotifyFieldsChanged();   // панель перечитает значения, пересобранные по папке
        };

        panel.PropertyStyleProvider   = fid       => TableViewCtrl.PropertyStyle(fid);
        panel.CanMovePropertyProvider = (fid, up) => TableViewCtrl.CanMoveProperty(fid, up);

        // Карточка «Название» — как заголовок его колонки в таблице.
        panel.TitleRenameRequested += (_, _) => _ = RenameSystemField(ViewModels.ConstructorDb.TableRowVm.TitleKey);
        panel.TitleMoveRequested   += (_, up) =>
        {
            TableViewCtrl.MoveTitle(up);
            NotifyFieldsChanged();   // панель перечитает свойства в новом порядке
        };
        panel.TitleColorRequested  += (_, c) => TableViewCtrl.ApplyTitleStyle(c.Aspect, c.Hex);
        panel.TitleStyleProvider   = ()  => TableViewCtrl.TitleStyle();
        panel.CanMoveTitleProvider = up => TableViewCtrl.CanMoveTitle(up);
    }

    /// <summary>
    /// «Добавить описание» из панели записи: заводит коллекции поле с ролью «Описание» (тип
    /// «Текст») без диалога — имя и тип у него предопределены, настраивать нечего. Дальше — тот
    /// же путь, что у обычного создания свойства: одна новая колонка в таблице и FieldsChanged,
    /// по которому панель перечитывается и открывает нижний блок уже на описании.
    /// </summary>
    public void CreateDescriptionFieldFromPanel()
    {
        if (_currentCollectionId is null || _repository is null) return;

        var colId  = _currentCollectionId.Value;
        var before = _repository.GetFields(colId).Select(f => f.Id).ToHashSet();

        int fieldId;
        try { fieldId = _repository.EnsureDescriptionField(colId); }
        catch (Exception ex) when (DbErrors.Handle(ex, _repository.DbPath)) { return; }

        // Поле могло не создаться, а лишь получить роль (одноименное уже было) — колонку в
        // таблице добавляем только для действительно нового.
        if (!before.Contains(fieldId)) TableViewCtrl.AddColumnForField(fieldId);
        else                           TableViewCtrl.RebuildColumn(fieldId.ToString());

        NotifyFieldsChanged();
        CollectionsChanged?.Invoke(this, EventArgs.Empty);
    }

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

    // Окно «Отображение» — менеджер видимости/уровней колонок. Список свойств берем
    // из таблицы (полный, включая скрытые). По «Сохранить» — обновляем таблицу.
    private async Task OpenDisplayDialog()
    {
        if (_currentCollectionId is null || _repository is null) return;
        if (VisualRoot is not Avalonia.Controls.Window win) return;

        var columns = TableViewCtrl.MovableColumnsInOrder();
        var dlg = new DisplayDialog(_repository, _currentCollectionId.Value, columns);
        var ok  = await dlg.ShowDialog<bool?>(win);
        if (ok == true)
        {
            Touch();
            TableViewCtrl.ReloadColumns();   // только колонки (строки не сбрасываем); выделение работает
        }
    }

    // Окно «Отображение записей» — менеджер видимости/уровней/порядка строк. Список
    // записей берем из БД (полный, включая скрытые — их там можно вернуть), в порядке
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
    // id поля как строка либо системный «__title__».
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
            // (как его кладет AddField). Затем метод переставит его за afterKey.
            var movable = new List<string>(orderBefore) { newId.ToString() };
            _repository.MoveColumnAfter(_currentCollectionId.Value, newId.ToString(), afterKey, movable);
        }

        TableViewCtrl.ReloadColumns();   // только колонки, строки не сбрасываем
        NotifyFieldsChanged();
        CollectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    // Переименование системной колонки «Название»: меняем только подпись (DisplayName
    // в метаданных). Физическую колонку Title не трогаем.
    private async Task RenameSystemField(string key)
    {
        if (_currentCollectionId is null || _repository is null) return;
        if (VisualRoot is not Avalonia.Controls.Window win) return;

        var current = TableViewCtrl.GetSystemHeader(key);
        var dlg     = new PropertyEditorDialog(current);
        var ok      = await dlg.ShowDialog<bool?>(win);
        if (ok != true || dlg.ResultName is null) return;   // отмена

        var defaultName = TableViewCtrl.GetSystemDefaultName(key);
        try { _repository.SetSystemFieldName(_currentCollectionId.Value, key, dlg.ResultName, defaultName); }
        catch (Exception ex) when (DbErrors.Handle(ex, _repository.DbPath)) { return; }
        TableViewCtrl.RebuildColumn(key);   // перестроить только этот заголовок, не всю таблицу
        NotifyFieldsChanged();              // подпись карточки «Название» в панели записи
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
        try { _repository.UpdateCollection(col); }
        catch (Exception ex) when (DbErrors.Handle(ex, _repository.DbPath))
        {
            NavbarConstructor.SetTitle(col.Name);   // заголовок вернем к сохраненному имени
            return;
        }
        CollectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task DuplicateProperties(IReadOnlyList<int> fieldIds)
    {
        if (_repository is null || _currentCollectionId is null) return;
        var ids = fieldIds.Distinct().ToList();
        if (ids.Count == 0) return;

        // Порядок колонок ДО дублирования — чтобы каждую копию поставить сразу справа от ее
        // исходника (как одиночный дубль), даже если задан column_order.
        var orderBefore = TableViewCtrl.MovableKeysInOrder();

        var copies = new List<(int Src, int Copy)>();   // исходник → созданная копия
        var many   = ids.Count > 1;

        // Всю тяжелую работу (подсчет значений + копирование) делаем в фоне на изолированных
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

        // Каждую копию — сразу за ее исходником, одним пересохранением порядка колонок.
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

    // Столбцы с ролью удаляются как обычные — но молча уносят с собой раздел: без исполнителя
    // коллекция целиком выпадает из задач, без «Описания» пропадают все тексты. Называем
    // потерю в том же окне подтверждения (пустая строка — ролей среди удаляемых нет).
    private static string RoleLossWarning(IReadOnlyList<DbField> fields, IReadOnlyList<int> deletable,
                                          bool hasTasks, bool hasCalendar)
    {
        var lines = deletable
            .Select(id => fields.FirstOrDefault(f => f.Id == id))
            .OfType<DbField>()
            .Select(f => (Field: f, Impact: FieldRoleText.DeletionImpact(f, hasTasks, hasCalendar)))
            .Where(x => x.Impact is not null)
            .Select(x => $"«{x.Field.Name}» — {x.Impact}")
            .ToList();

        return lines.Count == 0 ? "" : "\n\n" + string.Join("\n", lines) + ".";
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

        var roleWarn = RoleLossWarning(fields, deletable,
                                       _repository.HasTaskExecField(_currentCollectionId.Value),
                                       _repository.HasCalendarDateField(_currentCollectionId.Value));
        // Как и у записей: рутину снимает настройка, а предупреждение о потере роли раздела
        // («коллекция выпадет из задач/календаря») остается — оно о последствии, а не о рутине.
        if (ConstructorSettings.ConfirmDelete || roleWarn.Length > 0)
        {
            var message = $"Вы удаляете {deletable.Count} {PropertiesPlural(deletable.Count)}. Подтвердить?" + roleWarn;
            if (await new ConfirmDialog(message).ShowDialog<bool>(win) != true) return;
        }

        // Надежно (без осиротевших данных) и без фриза UI: подсчет значений и удаление идут
        // в фоне на изолированных подключениях, на главном потоке ничего не считаем. Окно
        // прогресса покажется само, если работа не уложится в короткий момент (RunFieldOp).
        var manyCols = deletable.Count > 1;
        await RunBulkOp(manyCols ? "Удаление столбцов" : "Удаление столбца",
            (p, setPhase) =>
            {
                var counts = _repository.CountFieldValuesIsolated(deletable);
                var total  = counts.Sum(c => (long)c);
                // Поля удаляем по очереди (каждое — на своем подключении), показывая «N/M».
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
    // укладываются раньше — для них окно не появляется вовсе. На больших объемах окно всплывает
    // почти сразу, но UI при этом НЕ висит: вся работа уже в фоне.
    private const int FieldOpDialogDelayMs = 200;

    // Выполняет тяжелую bulk-операцию (удаление/дубль свойств ИЛИ записей) ПОЛНОСТЬЮ в фоне (подсчет +
    // сама работа — на изолированных подключениях; главный контекст из фонового потока не трогаем).
    // Окно прогресса показываем, только если работа не уложилась в FieldOpDialogDelayMs: раньше счет/
    // удаление шли СИНХРОННО на UI-потоке (на сотнях тысяч строк — фриз на несколько секунд, и лишь
    // потом индикатор). work репортит (готово, всего); пока всего неизвестно/0 — индикатор крутится
    // «Подсчет значений…». Окно МОДАЛЬНОЕ — вторую операцию нельзя начать, пока текущая не закрылась.
    private async Task RunBulkOp(string windowTitle,
                                 Action<IProgress<(long Done, long Total)>, Action<string>> work)
    {
        ProgressDialog? dlg = null;
        var lastShown = -1;          // -1 еще не показывали %, -2 «Сохранение…», 0..100 — проценты
        var phase     = windowTitle;

        // Обновление окна (на UI-потоке). dlg может быть еще/уже null — тогда отчет игнорируем.
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
            dlg.SetSaving("Подсчет значений…");   // пока total неизвестен — бегущая полоса
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

    // Адаптер IProgress<int> для прогресса с фонового потока (передается в *Isolated-методы).
    private sealed class IntProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    // Прогресс (готово, всего) с фонового потока; report сам маршалит обновление окна на UI.
    private sealed class PairProgress(Action<long, long> report) : IProgress<(long Done, long Total)>
    {
        public void Report((long Done, long Total) v) => report(v.Done, v.Total);
    }

    // Русское склонение: 1 свойство, 2–4 свойства, 5+ свойств (с учетом 11–14).
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
