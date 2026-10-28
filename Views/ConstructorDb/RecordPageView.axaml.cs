using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SmartBase.Controls.Components;
using SmartBase.Controls.Behaviors;
using SmartBase.Controls.Dialogs;
using SmartBase.Controls.DrawnGrid;
using SmartBase.Controls.Widgets;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Utils;
using SmartBase.ViewModels.ConstructorDb;

namespace SmartBase.Views.ConstructorDb;

/// <summary>
/// Правая панель «Страница записи» (Obsidian-стиль): свойства сверху + markdown снизу.
/// Сохранение значений идет сразу в БД при изменении (как в TableView).
///
/// Список свойств — рисованный (этап 7 плана drawn-grid.md, <see cref="PropertyList"/>): карточки
/// описываются моделью (<see cref="PropertyCard"/>: название, тип, значение из строки), рисуются
/// поверхностью только в окне прокрутки, контролов у них нет. Настоящие редакторы — те же контролы
/// значений, что стояли в карточках (<see cref="MakeInputForField"/>), — ставятся над карточкой под
/// указателем и при фокусе и снимаются, когда указатель ушел и фокуса нет; все правила правки,
/// автосохранения и синхронизации с нижним блоком остались у них. Состояние живет в модели: набор
/// выделенных свойств (<see cref="_selectedFieldIds"/>) и выделение «Названия»
/// (<see cref="_titleSelected"/>), общая ширина колонки названий (<see cref="_nameColWidth"/>), цели
/// нижнего блока (<see cref="_fieldTargets"/>, заводятся при показе записи), значения — в
/// <see cref="PropertyRowVm"/>. Открытие записи и прокрутка не зависят от числа свойств.
/// </summary>
public partial class RecordPageView : UserControl
{
    public event EventHandler<int>? RecordChanged;

    /// <summary>
    /// Момент, на который панель соответствует базе (<see cref="Database.ChangeBus.Now"/>):
    /// ставится при показе и перечитывании записи и после каждой своей записи в базу. Главное
    /// окно по своим коммитам перечитывает панель, только если коммит новее, — иначе набор
    /// текста в панели (запись на каждую букву) перечитывал бы ее же на каждой букве.
    /// </summary>
    public long DataStamp { get; private set; }

    private void Touch() => DataStamp = Database.ChangeBus.Now();

    // Своя запись в базу состоялась и уже отражена на месте — сообщить таблице (RecordChanged)
    // и отметить панель свежей.
    private void NotifyRecordChanged()
    {
        if (_vm is null) return;
        Touch();
        RecordChanged?.Invoke(this, _vm.RecordId);
    }
    public event EventHandler<bool>? CollectionPanelToggled;

    /// <summary>Клик по чипу поля-связи: открыть связанную запись (ее Id; коллекция может быть
    /// другой). Обрабатывает MainWindow → OpenRecordPanel (коллекция резолвится у самой записи).</summary>
    public event EventHandler<int>? RelatedRecordRequested;

    /// <summary>Переход к записи в ДРУГОЙ БД (чип межбазовой связи): (DbId цели, Id записи).</summary>
    public event EventHandler<(int DbId, int RecordId)>? CrossDbRelatedRecordRequested;

    /// <summary>Клик по «Добавить свойство»: создать новое поле текущей коллекции.
    /// Обрабатывает MainWindow → контроллер (тот же путь, что и «+» в таблице),
    /// чтобы новое поле сразу появилось и в таблице, и в панели.</summary>
    public event EventHandler? AddPropertyRequested;

    // ── Операции над свойствами (контекстное меню карточки, как у заголовка колонки) ──
    //
    // Панель их не выполняет сама: порядок, цвет и видимость колонок живут в таблице
    // (column_meta + кеши + точечная перестройка грида), а диалоги и пакетные операции —
    // в контроллере. Панель поднимает те же события, что таблица, и MainWindow ведет их в
    // те же обработчики — иначе поведение двух мест разъедется.

    /// <summary>«Изменить» — открыть диалог свойства.</summary>
    public event EventHandler<int>? PropertyEditRequested;

    /// <summary>«Дублировать» — набор выделенных свойств либо одно, по которому кликнули.</summary>
    public event EventHandler<IReadOnlyList<int>>? PropertyDuplicateRequested;

    /// <summary>«Удалить» и клавиши Delete/Backspace — тот же пакетный путь с подтверждением.</summary>
    public event EventHandler<IReadOnlyList<int>>? PropertiesDeleteRequested;

    /// <summary>«Создать новое свойство» — новое поле встает ПОСЛЕ этого (ключ = id поля).</summary>
    public event EventHandler<string>? PropertyCreateAfterRequested;

    /// <summary>«Отображение» — окно видимости и порядка колонок.</summary>
    public event EventHandler? PropertyDisplayRequested;

    /// <summary>«Сдвинуть вверх/вниз» — тот же порядок, что у колонок таблицы (вверх = влево).</summary>
    public event EventHandler<(int FieldId, bool Up)>? PropertyMoveRequested;

    /// <summary>«Изменить цвет» — цвет свойства (он же цвет колонки в таблице).</summary>
    public event EventHandler<(IReadOnlyList<int> FieldIds, StyleAspect Aspect, string? Hex)>? PropertyColorRequested;

    /// <summary>«Обновить привязку» — только у «Файла» с авто-правилом: пересканировать папку.</summary>
    public event EventHandler<int>? PropertyAutoFileRefreshRequested;

    /// <summary>Текущий цвет свойства — чтобы отметить его в меню. Источник — таблица.</summary>
    public Func<int, RowStyle>? PropertyStyleProvider { get; set; }

    /// <summary>Есть ли куда двигать свойство: у крайнего пункт меню не показываем.</summary>
    public Func<int, bool, bool>? CanMovePropertyProvider { get; set; }

    // Карточка системного «Название» — меню как у его колонки в таблице: без дубля и удаления,
    // а «Изменить» переименовывает подпись колонки. «Создать новое свойство» и «Отображение»
    // идут общими событиями (ключ колонки названия — TableRowVm.TitleKey).

    /// <summary>«Изменить» у «Названия» — переименовать подпись колонки.</summary>
    public event EventHandler? TitleRenameRequested;

    /// <summary>«Сдвинуть вверх/вниз» у «Названия» (true — вверх).</summary>
    public event EventHandler<bool>? TitleMoveRequested;

    /// <summary>«Изменить цвет» у «Названия».</summary>
    public event EventHandler<(StyleAspect Aspect, string? Hex)>? TitleColorRequested;

    /// <summary>Текущий цвет колонки «Название».</summary>
    public Func<RowStyle>? TitleStyleProvider { get; set; }

    /// <summary>Есть ли куда двигать «Название» (true — вверх).</summary>
    public Func<bool, bool>? CanMoveTitleProvider { get; set; }

    /// <summary>Клик по «Добавить описание» в крошке нижнего блока: завести полю коллекции
    /// роль «Описание». Тот же путь наверх, что и у «Добавить свойство» — контроллер создает
    /// поле, добавляет колонку в таблицу и шлет FieldsChanged, по которому панель перечитывается
    /// и открывает нижний блок уже на описании.</summary>
    public event EventHandler? AddDescriptionRequested;

    private RecordPageViewModel? _vm;
    private ConstructorRepository? _repo;   // для перечитывания записи из БД (ReloadFromDb)

    /// <summary>
    /// Действия обмена над ПОКАЗЫВАЕМОЙ записью (docs/ROADMAP-API.md, шаг 6). Аргументы —
    /// база и коллекция записи: панель может быть открыта межбазовым переходом, и спрашивать
    /// «текущую базу приложения» здесь нельзя.
    ///
    /// Панель об интеграциях ничего не знает: она показывает то, что ей дали, и зовет то, что
    /// ей дали. Собирает список раздел (<see cref="IntegrationActions"/>).
    /// </summary>
    public Func<int, int, IReadOnlyList<DataAction>>? DataActions { get; set; }

    // База ПОКАЗЫВАЕМОЙ записи (0 — активная). Панель может быть открыта межбазовым переходом, и тогда
    // «текущая база» для нее и для приложения — РАЗНЫЕ. Без этого внутрибазовая связь чужой записи
    // уводила бы в активную базу (см. NavigateTo).
    private int _dbId;

    // Гасит авто-сейв описания на время служебной установки текста (Show/ReloadFromDb), чтобы
    // программное MarkdownEditor.Text не приняли за правку пользователя (иначе перечитанное из БД
    // значение тут же писалось бы обратно и дергало таблицу). Имя гасит сам NavbarPage (SetTitle).
    //
    // ВАЖНО: TextBox поднимает TextChanged ОТЛОЖЕННО, через диспетчер, — событие приходит уже
    // после снятия флага, в том числе на установку текста при построении карточки. Поэтому флаг —
    // не единственная защита: обработчики еще сверяют текст со значением и молчат, если оно не
    // изменилось. Без этой сверки каждое построение карточек писало то же значение в базу и
    // поднимало RecordChanged, а таблица по нему отменяла открытую правку ячейки — набор текста в
    // таблице обрывался на первой паузе.
    private bool _suppressAutoSave;

    // ── Нижний блок = редактор активного ТЕКСТОВОГО свойства ───────────────────
    // Снизу виден один редактор (Markdown/обычный текст). Что он редактирует, задает
    // _bottomTarget: по умолчанию — поле с ролью «Описание», дальше выбор текстовой карточки
    // переключает цель. Нет ни одного текстового свойства (в т.ч. описания) — снизу пусто.
    // Не-текстовые карточки нижний блок не трогают.
    private sealed class BottomTextTarget
    {
        public int?            FieldId;          // id текстового поля-цели (у «Названия» — null)
        // Системное «Название»: одна строка (Enter не переносит), в «Просмотре» простой текст,
        // сохранение и синхронизация с шапкой — ApplyTitleEdit.
        public bool            IsTitle;
        public string          Name = "";
        public string          IconKey = "IconFieldText";   // иконка типа (для заголовка-крошки)
        public Func<string>    Read  = () => "";
        public Action<string>  Write = _ => { }; // сохранить значение (+ уведомить таблицу)
        public TextBox?        Inline;           // инлайн-поле в карточке (текстовые свойства) — для синхронизации
        public int             Index = -1;       // индекс карточки цели в списке (−1 — нет)
    }

    private BottomTextTarget? _bottomTarget;
    private int?              _bottomFieldId;    // id поля, открытого снизу — для восстановления
    private readonly Dictionary<int, BottomTextTarget> _fieldTargets = new();
    // Файл-редакторы по FieldId — чтобы обновить показ авто-file поля сразу при правке столбца-источника.
    private readonly Dictionary<int, Controls.Widgets.FileEditor> _fileEditors = new();
    private BottomTextTarget? _descTarget;      // цель по умолчанию — поле с ролью «Описание»
    private BottomTextTarget? _titleTarget;     // карточка «Название» (есть у каждой записи)
    private bool              _bottomIsTitle;   // снизу открыто «Название» — для восстановления
    private int               _titleIndex = -1; // индекс карточки «Название» в списке
    private bool              _titleSelected;   // «Название» выделено (одиночно, вне набора свойств)
    // Строки свойств по id поля — побочные изменения (авто-файл) пишутся в модель: список рисует
    // значение из строки при каждом кадре.
    private readonly Dictionary<int, PropertyRowVm> _rowsByField = new();
    // Текст связи по id поля — названия целей считаются один раз на сборку панели и после правки связи.
    private readonly Dictionary<int, string> _relationText = new();
    private readonly Dictionary<int, bool> _relationHasValues = new();
    private bool _focusBottomAfterReload;       // описание только что создано — поставить каретку в редактор
    private bool _suppressBottomSync;            // гасит обратную синхронизацию инлайн↔нижний при программной установке

    // Прокрутка редактора живет ВНУТРИ шаблона TextBox, а полоса блока вынесена наружу
    // (она идет от линии до линии, включая строку крошки) — значит связать их можно только
    // после применения шаблона. У превью свой, обычный ScrollViewer, искать его не нужно.
    private ScrollViewer? _editorScroll;

    public RecordPageView()
    {
        InitializeComponent();

        // Список свойств рисованный (PropertyList): карточки — описания из модели, редакторы —
        // прежние контролы значений (MakeInputForField), которые ставятся над карточкой под
        // указателем и снимаются, когда он ушел и фокуса нет; выделение — из модели (IsCardSelected).
        PropertiesHost.EditorFactory          = BuildEditor;
        PropertiesHost.EditorOpened          += (_, e) => OnEditorOpened(e.Index, e.Editor);
        PropertiesHost.EditorClosed          += (_, e) => OnEditorClosed(e.Index, e.Editor);
        PropertiesHost.CardPressed           += (_, e) => OnCardPressed(e);
        PropertiesHost.NameColumnResizeEnded += (_, w) => OnNameColumnResized(w);
        PropertiesHost.Surface.IsSelected     = IsCardSelected;
        PropertiesHost.NameColumnWidth        = _nameColWidth;

        FooterBar.PreviewToggled += (_, _) => UpdatePreviewVisibility();
        FooterBar.HideEmptyPropertiesToggled += (_, _) =>
        {
            PropertiesHost.CloseEditor();
            UpdatePropertyCards();
        };

        MarkdownEditor.TemplateApplied += (_, e) =>
        {
            _editorScroll = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
            // Высота строки — та же, что у абзаца «Просмотра» (MarkupTheme.LineHeight). Без этого
            // переключение правка/просмотр меняло межстрочие, и текст на экране ехал тем сильнее,
            // чем ниже строка. Ставим на презентер: у TextBox своего LineHeight нет.
            if (e.NameScope.Find<Avalonia.Controls.Presenters.TextPresenter>("PART_TextPresenter") is { } presenter)
                presenter.LineHeight = Controls.Markup.MarkupTheme.LineHeight;
            UpdateBottomScrollBar();
        };

        // Delete/Backspace по выделенным свойствам — как по выделенным колонкам в таблице.
        // Туннелем: иначе клавишу раньше разберут поля ввода внутри карточек.
        AddHandler(KeyDownEvent, OnPanelKeyDown, RoutingStrategies.Tunnel);

        // Живая запись имени по символу — как ячейка таблицы: правка имени в навбаре панели
        // сразу видна в строке таблицы (RecordChanged → RefreshRecordRow). NavbarPage сам гасит
        // событие при программной установке имени (SetTitle), поэтому здесь — только правки пользователя.
        HeaderBar.TitleChanged += (_, text) => ApplyTitleEdit(text, TitleSource.Header);

        // Живая запись активного текстового свойства по символу. Цель (поле «Описание», другое
        // текстовое поле или «Название») задает _bottomTarget. Guard _suppressAutoSave — для
        // служебной установки текста. Параллельно синхронизируем инлайн-поле карточки той же цели
        // («Название» синхронизирует его само — сохраненным значением, см. ApplyTitleEdit).
        MarkdownEditor.TextChanged += (_, _) =>
        {
            if (_vm is null || _suppressAutoSave || _bottomTarget is null) return;
            var text = MarkdownEditor.Text ?? "";
            if (text == _bottomTarget.Read()) return;   // отложенное TextChanged от установки текста — не правка
            _bottomTarget.Write(text);   // сохраняет значение и уведомляет таблицу

            if (!_bottomTarget.IsTitle && _bottomTarget.Inline is { } inline) ApplyInlineText(inline, text);
        };

        HeaderBar.CollectionPanelToggled += (_, open) => CollectionPanelToggled?.Invoke(this, open);

        FooterBar.ActionsRequested += (_, anchor) => ShowActionsMenu(anchor);
        InitializeDatePeriodRuntime();
    }

    /// <summary>Синхронизирует визуальное состояние кнопки с фактической видимостью блока коллекции.</summary>
    public void SetCollectionPanelOpen(bool isOpen) => HeaderBar.SetCollectionPanelOpen(isOpen);

    /// <param name="dbId">База показываемой записи: 0 — активная, &gt;0 — открыта межбазовым переходом.</param>
    public void Show(ConstructorRepository repository, int recordId, int collectionId, int dbId = 0)
    {
        _datePeriodRefreshCancellation?.Cancel();
        // Порог медленности: список свойств рисованный, и открытие не зависит от числа свойств;
        // сюда уходит только чтение записи и полей.
        using var slow = Logging.Diag.Measure("открытие страницы записи", Logging.Diag.UiOpen,
                                              $"{Utils.DbErrors.Display(repository.DbPath)}, коллекция {collectionId}, запись {recordId}");

        Touch();
        // Та же запись открыта снова — прежние ссылки видны сразу, пока читаются свежие.
        if (!Shows(repository) || _vm?.RecordId != recordId) ResetBacklinks();
        _reloadAfterList = false;   // показ строит панель заново — отложенное перечитывание не нужно
        _repo = repository;
        _dbId = dbId;
        _vm = new RecordPageViewModel(repository, recordId, collectionId);
        DataContext = _vm;

        HeaderBar.SetTitle(_vm.EditableTitle);

        // Начальный вид описания — из «Настройки → Конструктор базы данных»; переключатель
        // внизу панели работает как и раньше, настройка задает только состояние при открытии.
        FooterBar.IsPreview = ConstructorSettings.DescriptionPreview;

        BuildProperties();
        ApplyPropsHeight();
        RefreshActions();
        LoadBacklinks();

        // Новая запись — нижний блок по умолчанию на «Описании»; описания у коллекции нет —
        // снизу пусто, а в крошке снова видна ссылка + «Добавить описание» (выбор другого
        // текстового свойства ее прячет, но живет он только до следующего открытия записи).
        _bottomFieldId = null;
        _bottomIsTitle = false;
        RestoreBottom();
    }

    /// <summary>
    /// Перечитать ТЕКУЩУЮ запись из БД и обновить панель на месте — когда ту же запись поправили
    /// прямо в таблице. Обновляются значения свойств, имя и описание. Поле, которое пользователь
    /// сейчас редактирует в панели (заголовок в режиме правки или markdown-редактор в фокусе),
    /// не трогаем — чтобы не затереть набираемый текст; режим просмотра markdown сохраняется.
    /// WireAutoSave повторно НЕ вызываем (его LostFocus-подписки читают актуальный _vm).
    /// </summary>
    public void ReloadFromDb()
    {
        if (_vm is null || _repo is null || DeferReloadForList()) return;
        Touch();

        var recordId     = _vm.RecordId;
        var collectionId = _vm.CollectionId;
        var preview      = FooterBar.IsPreview;

        _vm = new RecordPageViewModel(_repo, recordId, collectionId);
        DataContext = _vm;

        // Заголовок — только если он не в режиме редактирования (SetTitle сам не дергает сохранение).
        if (!HeaderBar.IsTitleEditing)
            HeaderBar.SetTitle(_vm.EditableTitle);

        BuildProperties();
        ApplyPropsHeight();
        LoadBacklinks();

        FooterBar.IsPreview = preview;

        // Возвращаем нижний блок на ту же цель (по id), если она еще есть; иначе — «Описание».
        // Текст не перечитываем, пока редактор в фокусе (не затереть набираемое).
        RestoreBottom();
    }

    public void Clear()
    {
        _datePeriodRefreshCancellation?.Cancel();
        ResetBacklinks();
        _reloadAfterList = false;
        _vm = null;
        _repo = null;
        DataContext = null;
        HeaderBar.SetTitle("");
        MarkdownEditor.Text  = "";
        MarkdownPreview.Markdown = "";
        BottomTitleHost.Children.Clear();
        PropertiesHost.SetCards([]);
        _cards         = [];
        _titleIndex    = -1;
        _titleSelected = false;
        _bottomTarget  = null;
        _bottomFieldId = null;
        _bottomIsTitle = false;
        _descTarget    = null;
        _titleTarget   = null;
        _fieldTargets.Clear();
        _fileEditors.Clear();
        _cardIndexByField.Clear();
        _rowsByField.Clear();
        _relationText.Clear();
        _relationHasValues.Clear();
        _selectedFieldIds.Clear();
    }

    private void UpdatePreviewVisibility()
    {
        var preview = FooterBar.IsPreview;
        var plain   = _bottomTarget?.IsTitle == true;
        MarkdownPreviewBox.IsVisible = preview;
        MarkdownEditor.IsVisible     = !preview;
        MarkdownPreview.IsVisible    = !plain;
        PlainPreview.IsVisible       = plain;

        if (preview && plain)
        {
            PlainPreview.LineHeight = Controls.Markup.MarkupTheme.LineHeight;   // как абзац Markdown
            PlainPreview.Text       = MarkdownEditor.Text ?? "";
        }
        else if (preview)
            MarkdownPreview.Markdown = MarkdownEditor.Text ?? "";

        UpdateBottomScrollBar();
    }

    // Режим редактора под цель: «Название» — одна строка (Enter не переносит) и свой
    // плейсхолдер, текстовое свойство — Markdown с переносами.
    private void ApplyBottomMode(BottomTextTarget target)
    {
        _bottomIsTitle               = target.IsTitle;
        MarkdownEditor.AcceptsReturn = !target.IsTitle;
        MarkdownEditor.Watermark     = target.IsTitle ? PlaceholderTitle : "Пишите в Markdown...";
    }

    private const string PlaceholderTitle = TitleText.Placeholder;

    /// <summary>
    /// Заморозить (и отпустить) ширину нижнего блока — на время перетаскивания ручки ширины
    /// панели, ее ведет <c>MainWindow</c>.
    ///
    /// Зачем: пока ручку тянут, текст не должен переверстываться. Markdown-описание на 14 КБ
    /// пересчитывает переносы 22 мс НА КАЖДОЕ движение мыши (обычный текст — 9 мс, описание на
    /// 28 КБ — 47 мс), и перетаскивание идет рывками; с замороженной шириной кадр стоит 0,1 мс
    /// при любом объеме текста. Дорога именно ширина: у markdown это дерево из сотен блоков
    /// (абзацы, пункты списков, ячейки таблиц), и каждый меряет переносы заново. Высота так не
    /// стоит ничего — вертикальная ручка между свойствами и описанием ничего не замораживает.
    ///
    /// Плата: пока ширина заморожена, текст не перетекает — при сужении подрезается по краю
    /// (блок клипует содержимое), при расширении справа остается поле. Отпустили ручку — встал
    /// по новой ширине за один проход.
    /// </summary>
    public void FreezeContentWidth(bool freeze)
    {
        Freeze(MarkdownEditor);
        Freeze(MarkdownPreview);
        Freeze(PlainPreview);

        void Freeze(Control control) =>
            control.Width = freeze && control.Bounds.Width > 0 ? control.Bounds.Width : double.NaN;
    }

    /// <summary>
    /// Вынесенная полоса блока описания обслуживает тот контрол, который сейчас виден:
    /// в режиме просмотра — прокрутку превью, в режиме правки — прокрутку редактора.
    /// Полоса одна, потому что и блок один: видно всегда ровно одно из двух.
    /// </summary>
    private void UpdateBottomScrollBar() =>
        ScrollBarLink.SetSource(BottomScrollBar,
                                FooterBar.IsPreview ? MarkdownPreviewBox : _editorScroll);

    // ── Перетаскивание ручки между свойствами и описанием ──────────────────────
    //
    // По умолчанию свойства и описание делят высоту поровну (обе строки 1*, 50/50);
    // ручка-пилюля сидит на границе. Перетаскивание задает строке свойств пиксельную
    // высоту: тянем вниз — свойства растут (описание сжимается, оно в Star-строке),
    // тянем вверх — свойства сжимаются, и если их больше, чем влезает, ScrollViewer
    // дает прокрутку. Выбранная высота помнится в пределах сессии и переносится
    // между записями; на диск она уходит, только если включено «Запоминать раскладку
    // панели записи» (стандартно выключено — у разных записей разное число свойств,
    // и одна и та же высота не всем подходит).

    private bool    _propsGripDragging;
    private double  _propsGripStartY;
    private double  _propsGripStartHeight;
    private double? _propsHeight = StoredPropsHeight();   // высота блока свойств (null = Auto)

    // Запомненная раскладка читается один раз, при создании панели: настройку могли включить
    // уже после запуска, но забытая раскладка стирается при выключении (см. ConstructorSettings),
    // так что читать ее на каждом открытии записи незачем.
    private static double? StoredPropsHeight()
    {
        if (!ConstructorSettings.RememberLayout) return null;
        var h = AppSettings.LoadConstructorPropsHeight();
        return h > 0 ? h : null;
    }

    private const double PropsHeightMin = 48;

    // Строка свойств в PageGrid (Auto,Auto,Auto,*,Auto): индекс 1.
    private RowDefinition PropsRow => PageGrid.RowDefinitions[1];

    // По умолчанию (ручку не двигали) свойства и описание делят высоту поровну: строка
    // свойств = 1*, описание тоже 1* → 50/50. Свойств больше, чем влезает → ScrollViewer
    // прокручивает. После перетаскивания ручки — фиксированная пиксельная высота свойств.
    private void ApplyPropsHeight() =>
        PropsRow.Height = _propsHeight is double h
            ? new GridLength(h, GridUnitType.Pixel)
            : new GridLength(1, GridUnitType.Star);

    private void OnPropsGripPressed(object? sender, PointerPressedEventArgs e)
    {
        _propsGripDragging    = true;
        _propsGripStartY      = e.GetPosition(this).Y;
        _propsGripStartHeight = PropsRow.ActualHeight;   // текущая высота (при Auto = высота контента)
        e.Pointer.Capture((IInputElement?)sender);
        e.Handled = true;
    }

    private void OnPropsGripMoved(object? sender, PointerEventArgs e)
    {
        if (!_propsGripDragging) return;

        var dy = e.GetPosition(this).Y - _propsGripStartY;
        // Не даем свойствам съесть все описание: оставляем ему запас (шапка+футер+минимум описания).
        var max = Math.Max(PropsHeightMin, PageGrid.Bounds.Height - 260);
        var newHeight = Math.Clamp(_propsGripStartHeight + dy, PropsHeightMin, max);

        PropsRow.Height = new GridLength(newHeight, GridUnitType.Pixel);
        _propsHeight = newHeight;
    }

    private void OnPropsGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        _propsGripDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
        // Пишем в конце перетаскивания, а не на каждом движении мыши: иначе одно движение
        // ручки дало бы сотню записей в app.db.
        if (ConstructorSettings.RememberLayout && _propsHeight is double h)
            AppSettings.SaveConstructorPropsHeight(h);
    }

    // Общая ширина колонки названий (px) — делится всеми карточками; меняется перетаскиванием
    // ручки-разделителя ⋮ (ее тянет поверхность списка). Живет в пределах сессии (переносится
    // между записями).
    private const double NameColDefault = PropertySurface.NameColDefault;
    private double _nameColWidth = StoredNameColWidth();
    private const double NameColMin = PropertySurface.NameColMin;

    private static double StoredNameColWidth()
    {
        if (!ConstructorSettings.RememberLayout) return NameColDefault;
        var w = AppSettings.LoadConstructorNameColWidth();
        return w >= NameColMin ? w : NameColDefault;
    }

    // Выделенные свойства — набор, как выделение колонок в таблице: над ним работают
    // «Дублировать», «Изменить цвет», «Удалить» и клавиши Delete/Backspace. Карточки списка — в
    // порядке панели, индекс карточки по id поля — для выделения и цели нижнего блока.
    private readonly List<int> _selectedFieldIds = [];
    private List<PropertyCard> _cards = [];
    private readonly Dictionary<int, int> _cardIndexByField = [];

    // Допущенные пользователи базы — ОДИН запрос на сборку панели, а не на каждое поле типа
    // «пользователь»: список у всех таких полей одинаков и отличается только текущим значением
    // поля, которое подмешивается уже в памяти. На записи с десятками таких свойств это были
    // десятки одинаковых запросов подряд с UI-потока (на PostgreSQL — столько же обращений к
    // серверу, и там это и есть разница между «открылась сразу» и «думала секунды»).
    private List<string>? _userOptions;

    private List<string> UserOptions(string? current)
    {
        _userOptions ??= _repo?.GetUserOptions() ?? [];
        if (string.IsNullOrEmpty(current) || _userOptions.Contains(current)) return _userOptions;
        return [.. _userOptions, current];   // историческое имя видно, но общий список не портит
    }

    private void BuildProperties()
    {
        PropertiesHost.SetCards([]);   // редактор снимается — его ссылки отпускаются (OnEditorClosed)
        _fieldTargets.Clear();
        _fileEditors.Clear();
        _cardIndexByField.Clear();
        _rowsByField.Clear();
        _relationText.Clear();
        _relationHasValues.Clear();
        _selectedFieldIds.Clear();
        _cards         = [];
        _descTarget    = null;
        _titleTarget   = null;
        _titleIndex    = -1;
        _titleSelected = false;
        _bottomTarget  = null;
        _userOptions   = null;   // состав читаем заново на каждую сборку панели, но ОДИН раз
        if (_vm is null) return;

        // Цели нижнего блока заводятся здесь, по модели: открыть свойство снизу и вернуть его после
        // перечитывания нужно и без редактора на карточке. Инлайн-поле цели подставляется, пока над
        // ее карточкой стоит редактор (OnEditorOpened).
        foreach (var row in _vm.Properties)
        {
            if (row.Field.Id is not int fid) continue;
            _rowsByField[fid] = row;
            if (row.Field.FieldType != "text") continue;
            var captured = row;
            _fieldTargets[fid] = new BottomTextTarget
            {
                FieldId = fid,
                Name    = row.Field.Name,
                IconKey = IconKeyFor(row.Field),
                Read    = () => captured.Value ?? "",
                Write   = t => { captured.Value = t; PersistProperty(captured); },
            };
        }
        _titleTarget = new BottomTextTarget
        {
            IsTitle = true,
            Name    = _vm.TitleLabel,
            Read    = () => _vm?.EditableTitle ?? "",
            Write   = t => ApplyTitleEdit(t, TitleSource.Bottom),
        };

        // Цель нижнего блока по умолчанию — поле с ролью «Описание» (если оно есть и текстовое:
        // после смены типа цели у него нет, и блок остается пустым, пока не выберут другое свойство).
        _descTarget = _vm.DescriptionFieldId is int did && _fieldTargets.TryGetValue(did, out var dt)
            ? dt : null;

        UpdatePropertyCards();
    }

    // Перестраивается только показ: цели нижнего редактора и значения скрытых полей остаются.
    // Индексы карточек меняются вместе с фильтром; редактор снимаем, пока действуют старые индексы.
    private void UpdatePropertyCards()
    {
        if (_vm is null) return;
        var items = _vm.OrderedItems.Where(item => item is not FieldItem fi ||
            !FooterBar.HideEmptyProperties || !IsEmptyProperty(fi.Row)).ToList();
        // Набор текста обычно не меняет состав списка: не создаем карточки и их делегаты заново.
        // Свои карточки — до «Добавить свойство», хвост ссылок — из того же ответа.
        if (_cards.FindIndex(c => c.Kind == PropertyCardKind.Add) == items.Count
            && ReferenceEquals(_cardsBacklinks, _backlinks) && _cardsHideEmpty == FooterBar.HideEmptyProperties
            && items.Select((item, index) => item switch
            {
                FieldItem fi => ReferenceEquals(_cards[index].Tag, fi.Row),
                _ => _cards[index].Kind == PropertyCardKind.Title,
            }).All(same => same)) return;

        var cards = new List<PropertyCard>(items.Count + 1);
        var indices = new Dictionary<int, int>();
        var titleIndex = -1;

        // Порядок строго как в таблице: VM уже отсортировала элементы по эффективной позиции
        // колонки. «Описание» — обычное поле и идет в этом же списке, отдельной карточки у него
        // нет; «Название» — системная колонка, у него своя карточка на ее месте. «Добавить
        // свойство» стоит внизу своих свойств, под ним — блок ссылок на запись (BacklinkCards); обе
        // входят в общий ресайз колонки названий (ручка ⋮). Карточки — описания для рисованного
        // списка; контролы значений строятся только под указателем (BuildEditor).
        foreach (var item in items)
        {
            switch (item)
            {
                case FieldItem fi:
                    if (fi.Row.Field.Id is int cardFid) indices[cardFid] = cards.Count;
                    cards.Add(CardFor(fi.Row));
                    break;
                case TitleItem:
                    titleIndex = cards.Count;
                    cards.Add(new PropertyCard
                    {
                        Name        = _vm.TitleLabel,
                        IconKey     = "IconFieldText",
                        Kind        = PropertyCardKind.Title,
                        Value       = () => _vm?.EditableTitle ?? "",
                        Placeholder = PlaceholderTitle,
                    });
                    break;
            }
        }
        cards.Add(new PropertyCard { Name = "Добавить свойство", IconKey = "IconPlus", Kind = PropertyCardKind.Add });
        cards.AddRange(BacklinkCards());
        PropertiesHost.CloseEditor();
        _cards = cards;
        _titleIndex = titleIndex;
        _cardIndexByField.Clear();
        foreach (var (fid, index) in indices) _cardIndexByField[fid] = index;
        _selectedFieldIds.RemoveAll(fid => !indices.ContainsKey(fid));
        if (_titleTarget is not null) _titleTarget.Index = _titleIndex;
        foreach (var (fid, target) in _fieldTargets) target.Index = _cardIndexByField.GetValueOrDefault(fid, -1);
        PropertiesHost.SetCards(cards);
    }

    private bool IsEmptyProperty(PropertyRowVm row)
    {
        if (row.Field.FieldType == "relation" && row.Field.Id is int fid && _repo is not null && _vm is not null)
        {
            // У связанной записи название может быть пустым: заполненность задают ребра связи.
            if (!_relationHasValues.TryGetValue(fid, out var hasValues))
                _relationHasValues[fid] = hasValues = _repo.GetLinkEdges(fid, _vm.RecordId).Count > 0;
            return !hasValues;
        }
        if (row.Field.FieldType == "multiselect")
            return MultiSelectValue.Parse(row.Value).All(string.IsNullOrWhiteSpace);
        return string.IsNullOrWhiteSpace(row.Value);
    }

    private bool _propertyFilterScheduled;

    private void SchedulePropertyFilter()
    {
        if (!FooterBar.HideEmptyProperties || _vm is null || _propertyFilterScheduled) return;
        _propertyFilterScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _propertyFilterScheduled = false;
            // Инлайн-поле не исчезает из-под каретки при очистке перед новым набором. Его закрытие
            // запустит проверку снова. Нижний редактор фильтр не перестраивает и фокус не теряет.
            if (FooterBar.HideEmptyProperties && !PropertiesHost.IsEditorBusy)
                UpdatePropertyCards();
        }, DispatcherPriority.Background);
    }

    // Описание карточки свойства для рисованного списка: что показывать без контрола. Значение
    // читается из строки модели при каждом кадре; связь — названия целей (RelationText).
    private PropertyCard CardFor(PropertyRowVm row)
    {
        var f         = row.Field;
        var computed  = FieldTypeInfo.IsComputed(f.FieldType);
        var relCfg    = f.FieldType == "relation" ? RelationConfig.FromConfig(f.ConfigJson) : null;
        var relBroken = f.FieldType == "relation" && (relCfg is null || _repo is null || f.Id is null);
        Func<string>? display = null;
        if (computed)       display = () => FieldTypeInfo.FormatComputedDisplay(row.Value, f.FieldType);
        else if (relBroken) display = () => "Связь не настроена";
        else if (relCfg is not null && f.Id is int relFid) display = () => RelationText(relFid, relCfg);
        return new PropertyCard
        {
            Name           = f.Name,
            IconKey        = IconKeyFor(f),
            FieldType      = f.FieldType,
            Tag            = row,
            Value          = () => row.Value ?? "",
            Placeholder    = f.FieldType == "url" ? "https://" : "",
            Muted          = computed || relBroken,
            NumberFormat   = NumberFormatValue.FromConfig(f.ConfigJson),
            Separator      = MultiSelectValue.SeparatorFromConfig(f.ConfigJson),
            FileAutoFolder = FileAutoRule.FromConfig(f.ConfigJson)?.Folder ?? "",
            Display        = display,
        };
    }

    private string RelationText(int fieldId, RelationConfig cfg)
    {
        if (_relationText.TryGetValue(fieldId, out var text)) return text;
        text = _repo is not null && _vm is not null ? RelationSelectBox.DisplayText(_repo, fieldId, _vm.RecordId, cfg) : "";
        _relationText[fieldId] = text;
        return text;
    }

    // ── Редакторы под указателем ───────────────────────────────────────────────
    //
    // Контрол значения — тот же, что стоял в карточке (MakeInputForField), но строится только когда
    // указатель вошел в полосу значения или карточку нажали, и снимается, когда ушел и фокуса нет
    // (PropertyList). У карточки нет цены контрола, пока к ней не тянутся.

    private Control? BuildEditor(int index)
    {
        if (index < 0 || index >= _cards.Count) return null;
        var card = _cards[index];
        Control? input = card.Kind switch
        {
            PropertyCardKind.Field when card.Tag is PropertyRowVm row => MakeInputForField(row),
            PropertyCardKind.Title => MakeTitleInput(),
            PropertyCardKind.Backlink when card.Tag is RelationBacklinkGroup group => MakeBacklinkBox(group),
            _ => null,
        };
        if (input is null) return null;
        input.VerticalAlignment   = VerticalAlignment.Center;   // как в сетке карточки: по центру строки
        input.HorizontalAlignment = HorizontalAlignment.Stretch;
        return input;
    }

    // Поле карточки «Название» — как у текстового свойства: в карточке первая строка, полный текст
    // правится в нижнем блоке. Правка в любом из трех мест — шапке, карточке, нижнем блоке —
    // сохраняется по символу и сразу видна в двух других и в строке таблицы (ApplyTitleEdit).
    private TextBox MakeTitleInput()
    {
        var tb = FlatValueBox(watermark: PlaceholderTitle);
        ApplyInlineText(tb, _vm?.EditableTitle ?? "");
        tb.TextChanged += (_, _) =>
        {
            // Многострочное название в карточке усечено до первой строки и не правится (см.
            // ApplyInlineText); установка текста кодом приходит сюда отложенно — сверка с
            // сохраненным значением отличает ее от правки.
            if (_suppressBottomSync || tb.IsReadOnly) return;
            var text = tb.Text ?? "";
            if (text == (_vm?.EditableTitle ?? "")) return;
            ApplyTitleEdit(text, TitleSource.Inline);
        };
        return tb;
    }

    // Редактор встал над карточкой: инлайн-поле текстовой цели нижнего блока — оно (синхронизация
    // с большим редактором в обе стороны).
    private void OnEditorOpened(int index, Control editor)
    {
        editor.LostFocus += OnPropertyEditorLostFocus;
        if (TargetAt(index) is { } target && editor is TextBox tb) target.Inline = tb;
    }

    private void OnPropertyEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        SchedulePropertyFilter();
        SchedulePendingBacklinks();
    }

    // Редактор снят: ссылки на него отпускаются, карточка снова рисуется из модели.
    private void OnEditorClosed(int index, Control editor)
    {
        editor.LostFocus -= OnPropertyEditorLostFocus;
        if (TargetAt(index) is { } target && ReferenceEquals(target.Inline, editor)) target.Inline = null;
        if (index >= 0 && index < _cards.Count && _cards[index].Tag is PropertyRowVm { Field.Id: int fid }
            && _fileEditors.TryGetValue(fid, out var fe) && ReferenceEquals(fe, editor))
            _fileEditors.Remove(fid);
        PropertiesHost.RefreshValues();
        SchedulePropertyFilter();
        SchedulePendingBacklinks();
    }

    private BottomTextTarget? TargetAt(int index)
    {
        if (index < 0 || index >= _cards.Count) return null;
        var card = _cards[index];
        if (card.Kind == PropertyCardKind.Title) return _titleTarget;
        return card.Tag is PropertyRowVm { Field.Id: int fid } && _fieldTargets.TryGetValue(fid, out var t) ? t : null;
    }

    // Нажатие на карточке (или под ее редактором). Зоны — как у прежней карточки: ЗНАЧЕНИЕ — правка,
    // она же сбрасывает набор до одного, как клик по ячейке снимает выделение колонок; РУЧКА ⋮ сюда
    // не приходит (ее тянет поверхность); ВСЕ ОСТАЛЬНОЕ — название, иконка типа и пустое место
    // карточки — жест ВЫДЕЛЕНИЯ, с Shift он набирает несколько свойств. Именно «все остальное», а
    // не «попал в подпись»: клик правее текста тоже целится в это свойство.
    //
    // Текстовое свойство открывается снизу обычным кликом В ЛЮБУЮ зону, кроме ручки. Клик мимо
    // значения каретку в текст не ставит — фокус на списке, и клавиши работают по выделению. Shift
    // нижний блок не трогает. Правый клик выделение НЕ трогает: он ставит меню карточки на
    // поверхность (откроется на отпускании) — иначе правый клик по одному из выделенных свойств
    // сбрасывал бы набор ровно перед тем, как применить к нему операцию (в таблице то же самое).
    private void OnCardPressed(PropertyPointerEventArgs e)
    {
        var index = e.Hit.Index;
        if (index < 0 || index >= _cards.Count) return;
        var card    = _cards[index];
        var surface = PropertiesHost.Surface;
        var props   = e.Pointer.GetCurrentPoint(surface).Properties;

        if (props.IsRightButtonPressed)
        {
            // Меню — на правом НАЖАТИИ, а не заранее на каждую карточку: показывается оно на
            // отпускании кнопки, так что к этому моменту уже на месте.
            surface.ContextMenu = card.Kind switch
            {
                PropertyCardKind.Field when card.Tag is PropertyRowVm row && row.Field.Id is not null => BuildPropertyMenu(row),
                PropertyCardKind.Title => BuildTitleMenu(),
                _ => null,
            };
            return;
        }
        if (!props.IsLeftButtonPressed) return;

        if (card.Kind == PropertyCardKind.Backlink)
        {
            PressBacklink(index, e);
            return;
        }

        if (card.Kind == PropertyCardKind.Add)
        {
            e.Pointer.Handled = true;
            AddPropertyRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        var target = TargetAt(index);
        if (e.Hit.Zone == PropertyZone.Value)
        {
            SelectValue(index, target);
            // Нажатие пришло на саму поверхность, без наведения (сенсорный ввод, клик после
            // прокрутки колесом): редактора еще нет — ставим и отдаем ему нажатие; галочку
            // переключаем сами.
            if (PropertiesHost.EditingIndex != index)
            {
                if (card.FieldType == "checkbox" && card.Tag is PropertyRowVm cb)
                {
                    cb.Value = cb.Value is "1" or "true" or "True" ? "0" : "1";
                    PersistProperty(cb);
                }
                else PropertiesHost.OpenEditorAt(index, e.Pointer.GetPosition(surface));
                e.Pointer.Handled = true;
            }
            return;
        }

        var shift = e.Pointer.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (card.Kind == PropertyCardKind.Title)
        {
            // Shift здесь работает как обычный клик: «Название» в набор не входит.
            SelectTitle();
            if (_titleTarget is not null) OpenBottom(_titleTarget);
        }
        else if (card.Tag is PropertyRowVm { Field.Id: int fid })
        {
            SelectProperty(fid, shift);
            if (!shift && target is not null) OpenBottom(target);
        }
        e.Pointer.Handled = true;
    }

    private enum TitleSource { Header, Inline, Bottom }

    // Сохранить набранное название и показать сохраненное в остальных местах. Источник правки не
    // трогаем: человек в нем печатает, и крайний пробел законно живет там, пока слово не допечатано
    // (сохраняется название без него — правило шапки, RecordPageViewModel.SaveTitleInput).
    private void ApplyTitleEdit(string text, TitleSource source)
    {
        if (_vm is null) return;
        ApplyFileSideEffects(_vm.SaveTitleInput(text));   // «Название» — источник авто-file: обновить файл-ячейки
        NotifyRecordChanged();

        var shown = _vm.EditableTitle;
        if (source != TitleSource.Header) HeaderBar.SetTitle(shown);
        if (source != TitleSource.Inline && _titleTarget?.Inline is { } inline) ApplyInlineText(inline, shown);
        PropertiesHost.RefreshValues();
        if (source != TitleSource.Bottom && _bottomTarget is { IsTitle: true } && !MarkdownEditor.IsFocused)
            SetBottomText(shown);
    }

    // Служебная установка текста нижнего блока (без записи обратно в цель).
    private void SetBottomText(string text)
    {
        if ((MarkdownEditor.Text ?? "") == text) return;
        _suppressAutoSave = true;
        MarkdownEditor.Text = text;
        _suppressAutoSave = false;
        UpdatePreviewVisibility();
    }

    // Клик по подписи «Названия»: выделить его карточку (одиночно, вне набора свойств). Фокус — на
    // список, как у свойства: иначе горячие клавиши меню ушли бы в поле ввода.
    private void SelectTitle()
    {
        ClearPropertySelection();
        _titleSelected = true;
        PropertiesHost.RefreshValues();
        PropertiesHost.Surface.Focus();
    }

    private bool TitleSelected => _titleSelected;

    // Выделена ли карточка — модель выделения для рисованного списка.
    private bool IsCardSelected(int index)
    {
        if (index < 0 || index >= _cards.Count) return false;
        if (index == _titleIndex) return _titleSelected;
        return _cards[index].Tag is PropertyRowVm { Field.Id: int fid } && _selectedFieldIds.Contains(fid);
    }

    // ── Выделение свойств (одиночное и набор по Shift) ──────────────────────────
    //
    // Один в один с выделением колонок таблицы: обычный клик — только это свойство,
    // Shift+клик — добавить или убрать одно, не трогая остальные. Порядок держим по порядку
    // свойств в панели — он же порядок колонок, и в этом порядке уходит список на удаление.

    private void SelectProperty(int fieldId, bool shift, bool focus = true)
    {
        if (shift)
        {
            if (!_selectedFieldIds.Remove(fieldId)) _selectedFieldIds.Add(fieldId);
        }
        else
        {
            _selectedFieldIds.Clear();
            _selectedFieldIds.Add(fieldId);
        }

        // Фокус уводим из поля значения на список: иначе Delete по выделенным свойствам не
        // сработает — обработчик клавиши намеренно молчит, пока курсор в тексте (там Delete правит
        // буквы). Так же ведет себя таблица: клик по заголовку колонки выводит фокус из ячейки. При
        // восстановлении после перечитывания (focus=false) фокус не трогаем — человек может
        // печатать в нижнем блоке.
        if (focus) PropertiesHost.Surface.Focus();

        if (_vm is not null)
        {
            var order = _vm.OrderedItems.OfType<FieldItem>().Select(i => i.Row.Field.Id).ToList();
            _selectedFieldIds.Sort((a, b) => order.IndexOf(a).CompareTo(order.IndexOf(b)));
        }

        ApplyPropertySelection();
    }

    private void ClearPropertySelection()
    {
        if (_selectedFieldIds.Count == 0) return;
        _selectedFieldIds.Clear();
        ApplyPropertySelection();
    }

    // Вид выделенной карточки — один и тот же у набора и у цели нижнего блока: состояние одно
    // («это свойство сейчас в работе»), два разных подсвета на одной карточке читались бы как
    // поломка. Список читает набор при кадре (IsCardSelected).
    private void ApplyPropertySelection()
    {
        // Одиночная подсветка «Названия» уступает набору свойств.
        _titleSelected = false;
        PropertiesHost.RefreshValues();
    }

    /// <summary>Свойства, к которым применяется операция меню: весь набор, если поле в него
    /// входит, иначе только оно (как дубль, цвет и удаление колонок в таблице).</summary>
    private List<int> TargetFieldIds(int fieldId) =>
        _selectedFieldIds.Contains(fieldId) && _selectedFieldIds.Count > 1
            ? [.. _selectedFieldIds]
            : [fieldId];

    // Клавиша удаления работает по ВЫДЕЛЕННЫМ свойствам и только когда курсор не в тексте:
    // иначе Delete в поле значения или в редакторе описания сносил бы свойства вместо букв.
    // Путь тот же, что у меню «Удалить», — пакетный, с подтверждением у контроллера.
    private void OnPanelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox || (e.Source is Visual v && v.FindAncestorOfType<TextBox>() is not null)) return;

        if (TitleSelected)
        {
            OnTitleKeyDown(e);
            return;
        }

        // Создание свойства цели не требует — остальные пункты меню работают по выделению.
        if (_selectedFieldIds.Count == 0) return;
        var fieldId = _selectedFieldIds.First();

        // Новое свойство встает после выделенного — как пункт меню у этого свойства.
        if (ShortcutSettings.Matches("record.createProperty", e))
        {
            PermissionNotice.Run(this, Can(PermAction.CreateField), PermAction.CreateField,
                () => PropertyCreateAfterRequested?.Invoke(this, fieldId.ToString()));
            e.Handled = true;
            return;
        }

        if (ShortcutSettings.Matches("record.deleteProperties", e))
        {
            PropertiesDeleteRequested?.Invoke(this, [.. _selectedFieldIds]);
            e.Handled = true;
            return;
        }

        if (ShortcutSettings.Matches("record.editProperty", e))
        {
            PermissionNotice.Run(this, Can(PermAction.EditField), PermAction.EditField,
                () => PropertyEditRequested?.Invoke(this, fieldId));
            e.Handled = true;
            return;
        }

        if (ShortcutSettings.Matches("record.duplicateProperty", e))
        {
            PermissionNotice.Run(this, Can(PermAction.DuplicateField), PermAction.DuplicateField,
                () => PropertyDuplicateRequested?.Invoke(this, [.. _selectedFieldIds]));
            e.Handled = true;
            return;
        }

        // Сдвиг живет, только пока соседа есть куда двигать — как и пункт в меню.
        var up = ShortcutSettings.Matches("record.movePropertyUp", e);
        if ((up || ShortcutSettings.Matches("record.movePropertyDown", e))
            && (CanMovePropertyProvider?.Invoke(fieldId, up) ?? false))
        {
            PropertyMoveRequested?.Invoke(this, (fieldId, up));
            e.Handled = true;
        }
    }

    // Клавиши выделенного «Названия» — те же пункты, что в его меню. Удаления и дубля у него нет,
    // поэтому их клавиши молчат.
    private void OnTitleKeyDown(KeyEventArgs e)
    {
        if (ShortcutSettings.Matches("record.createProperty", e))
        {
            PermissionNotice.Run(this, Can(PermAction.CreateField), PermAction.CreateField,
                () => PropertyCreateAfterRequested?.Invoke(this, TableRowVm.TitleKey));
            e.Handled = true;
            return;
        }

        if (ShortcutSettings.Matches("record.editProperty", e))
        {
            PermissionNotice.Run(this, Can(PermAction.EditField), PermAction.EditField,
                () => TitleRenameRequested?.Invoke(this, EventArgs.Empty));
            e.Handled = true;
            return;
        }

        var up = ShortcutSettings.Matches("record.movePropertyUp", e);
        if ((up || ShortcutSettings.Matches("record.movePropertyDown", e))
            && (CanMoveTitleProvider?.Invoke(up) ?? false))
        {
            TitleMoveRequested?.Invoke(this, up);
            e.Handled = true;
        }
    }

    // ── Контекстное меню свойства ───────────────────────────────────────────────
    //
    // Состав и порядок — как у заголовка колонки в таблице (BuildHeaderContextMenu), с двумя
    // отличиями по существу: свойства в панели идут сверху вниз, поэтому сдвиг называется
    // «вверх/вниз», а пункта «Ручная ширина столбцов» нет вовсе — столбцов в панели нет.
    // Пункты собираются из общих MenuIcons/ColorMenu, поэтому вид у двух меню один.

    private bool Can(PermAction action) =>
        _repo?.Permissions?.Can(action, _vm?.CollectionId ?? 0) ?? true;

    // Запрещенное действие не гасим, а объясняем по клику — как в таблице.
    private void Guarded(MenuItem item, PermAction action, Action run) =>
        item.Click += (_, _) => Controls.Dialogs.PermissionNotice.Run(this, Can(action), action, run);

    // ── Действия обмена над записью ───────────────────────────────────────────

    /// <summary>Показать кнопку действий, если для этой коллекции что-то настроено.</summary>
    private void RefreshActions() =>
        FooterBar.ShowActions(Actions().Count > 0);

    private IReadOnlyList<DataAction> Actions() =>
        _vm is null ? [] : DataActions?.Invoke(_dbId > 0 ? RelationDbResolver.RegistryIdFor(_dbId) : 0, _vm.CollectionId) ?? [];

    /// <summary>
    /// Список действий под кнопкой. Собирается на КАЖДОЕ открытие: обмен могли настроить, пока
    /// панель висела на экране, а собранное однажды меню показывало бы вчерашний список.
    /// </summary>
    private void ShowActionsMenu(Control anchor)
    {
        if (_vm is null) return;

        var actions = Actions();
        if (actions.Count == 0) return;

        var record = _vm.RecordId;
        // Раскрывается ВВЕРХ: кнопка стоит в нижней полосе, и меню вниз ушло бы за экран.
        var menu   = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedLeft };

        foreach (var action in actions)
        {
            var item = new MenuItem
            {
                Header = Controls.Components.MenuIcons.IntegrationHeader(action.Title),
            };

            var run = action.Run;
            item.Click += (_, _) => _ = run([record]);
            menu.Items.Add(item);
        }

        // Окно закроет меню, если потеряет фокус (см. TransientPopups).
        Controls.Behaviors.TransientPopups.Track(menu);
        menu.ShowAt(anchor);
    }

    private ContextMenu BuildPropertyMenu(PropertyRowVm row)
    {
        var fieldId = row.Field.Id!.Value;
        var menu    = new ContextMenu();

        menu.Items.Add(new MenuItem
        {
            Header    = Controls.Components.MenuIcons.PropertyMenuHeader(row.Field.Name, row.Field.FieldType),
            IsEnabled = false,
            Focusable = false,
        });
        menu.Items.Add(new Separator());

        var miEdit = new MenuItem { Header = Controls.Components.MenuIcons.EditHeader() }
            .WithGesture("record.editProperty", this);
        Guarded(miEdit, PermAction.EditField, () => PropertyEditRequested?.Invoke(this, fieldId));
        menu.Items.Add(miEdit);

        var miDuplicate = new MenuItem { Header = Controls.Components.MenuIcons.DuplicateHeader() }
            .WithGesture("record.duplicateProperty", this);
        Guarded(miDuplicate, PermAction.DuplicateField,
                () => PropertyDuplicateRequested?.Invoke(this, TargetFieldIds(fieldId)));
        menu.Items.Add(miDuplicate);

        var miCreate = new MenuItem { Header = Controls.Components.MenuIcons.CreatePropertyHeader() }
            .WithGesture("record.createProperty", this);
        Guarded(miCreate, PermAction.CreateField,
                () => PropertyCreateAfterRequested?.Invoke(this, fieldId.ToString()));
        menu.Items.Add(miCreate);

        var miDisplay = new MenuItem { Header = Controls.Components.MenuIcons.DisplayHeader() };
        miDisplay.Click += (_, _) => PropertyDisplayRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(miDisplay);

        AddColorItems(menu,
            () => PropertyStyleProvider?.Invoke(fieldId) ?? RowStyle.None,
            () =>
            {
                var ids = TargetFieldIds(fieldId);
                return (aspect, hex) => PropertyColorRequested?.Invoke(this, (ids, aspect, hex));
            });

        // «Файл» с авто-привязкой: пересканировать папку и пересобрать значения — файлы на
        // диске могли поменяться. Пункт есть только у таких полей, как и в таблице.
        if (FileAutoRule.FromConfig(row.Field.ConfigJson) is not null)
        {
            var miRefresh = new MenuItem { Header = "Обновить привязку" };
            Guarded(miRefresh, PermAction.EditRecord,
                    () => PropertyAutoFileRefreshRequested?.Invoke(this, fieldId));
            menu.Items.Add(miRefresh);
        }
        if (row.Field.FieldType == "date_period"
            && DatePeriodConfig.FromConfig(row.Field.ConfigJson) is { Automatic: false })
        {
            var recalculate = new MenuItem { Header = "Пересчитать" };
            Guarded(recalculate, PermAction.EditRecord,
                () => _ = RecalculateDatePeriodsAsync(TargetFieldIds(fieldId)));
            menu.Items.Add(recalculate);
        }

        AddMoveItems(menu,
            up => CanMovePropertyProvider?.Invoke(fieldId, up) ?? false,
            up => PropertyMoveRequested?.Invoke(this, (fieldId, up)));

        menu.Items.Add(new Separator());

        var miDelete = new MenuItem
        {
            Header    = Controls.Components.MenuIcons.DeleteHeader(),
            IsEnabled = !row.Field.IsRequired,
        }.WithGesture("record.deleteProperties", this);
        Guarded(miDelete, PermAction.DeleteField,
                () => PropertiesDeleteRequested?.Invoke(this, TargetFieldIds(fieldId)));
        menu.Items.Add(miDelete);

        return menu;
    }

    // Меню карточки «Название» — как у его колонки в таблице (BuildSystemHeaderContextMenu):
    // Изменить (подпись колонки) · Создать новое свойство · Отображение · Изменить цвет · сдвиг.
    // Дублировать и удалить системное название нельзя — этих пунктов нет вовсе.
    private ContextMenu BuildTitleMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(new MenuItem
        {
            Header    = Controls.Components.MenuIcons.PropertyMenuHeader(_vm?.TitleLabel ?? PlaceholderTitle, "text"),
            IsEnabled = false,
            Focusable = false,
        });
        menu.Items.Add(new Separator());

        var miEdit = new MenuItem { Header = Controls.Components.MenuIcons.EditHeader() }
            .WithGesture("record.editProperty", this);
        Guarded(miEdit, PermAction.EditField, () => TitleRenameRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(miEdit);

        var miCreate = new MenuItem { Header = Controls.Components.MenuIcons.CreatePropertyHeader() }
            .WithGesture("record.createProperty", this);
        Guarded(miCreate, PermAction.CreateField,
                () => PropertyCreateAfterRequested?.Invoke(this, TableRowVm.TitleKey));
        menu.Items.Add(miCreate);

        var miDisplay = new MenuItem { Header = Controls.Components.MenuIcons.DisplayHeader() };
        miDisplay.Click += (_, _) => PropertyDisplayRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(miDisplay);

        AddColorItems(menu,
            () => TitleStyleProvider?.Invoke() ?? RowStyle.None,
            () => (aspect, hex) => TitleColorRequested?.Invoke(this, (aspect, hex)));

        AddMoveItems(menu,
            up => CanMoveTitleProvider?.Invoke(up) ?? false,
            up => TitleMoveRequested?.Invoke(this, up));

        return menu;
    }

    // Цвет свойства — тот же аспект column_meta, что видимость и подпись, поэтому право как
    // у правки свойства. Состав пересчитывается в Opening: и отметка текущего цвета (current),
    // и применение (makeApply) — у свойств оно берет НАБОР, а выделение к моменту клика могло
    // смениться.
    private void AddColorItems(ContextMenu menu, Func<RowStyle> current,
                               Func<Action<StyleAspect, string?>> makeApply)
    {
        var host = new MenuItem { Header = Controls.Components.MenuIcons.ColorHeader() };
        Controls.Components.MenuIcons.FixSubmenuGap(host);
        host.IsEnabled = Can(PermAction.EditField);

        menu.Opening += (_, _) =>
        {
            host.Items.Clear();
            var apply = makeApply();
            var cur   = current();
            host.Items.Add(Controls.Components.ColorMenu.Build("Фон колонки", cur.Bg,
                hex => apply(StyleAspect.Background, hex)));
            host.Items.Add(Controls.Components.ColorMenu.Build("Цвет текста колонки", cur.Fg,
                hex => apply(StyleAspect.Text, hex)));
        };

        menu.Items.Add(host);
    }

    // «Сдвинуть вверх/вниз» с разделителем перед ними; неприменимое направление (крайнее
    // свойство) скрываем. Порядок свойств в панели = порядок колонок, поэтому сдвиг виден и
    // в таблице. Доступность пересчитываем в Opening: порядок мог смениться.
    private void AddMoveItems(ContextMenu menu, Func<bool, bool> canMove, Action<bool> move)
    {
        var separator = new Separator();
        var miUp   = new MenuItem { Header = Controls.Components.MenuIcons.MoveUpHeader() }
            .WithGesture("record.movePropertyUp", this);
        var miDown = new MenuItem { Header = Controls.Components.MenuIcons.MoveDownHeader() }
            .WithGesture("record.movePropertyDown", this);
        miUp.Click   += (_, _) => move(true);
        miDown.Click += (_, _) => move(false);

        menu.Opening += (_, _) =>
        {
            var up   = canMove(true);
            var down = canMove(false);
            separator.IsVisible = up || down;
            miUp.IsVisible      = up;
            miDown.IsVisible    = down;
        };

        menu.Items.Add(separator);
        menu.Items.Add(miUp);
        menu.Items.Add(miDown);
    }

    // Выделяем карточку всегда (одиночно); нижний блок переключаем только на ТЕКСТОВУЮ цель —
    // не-текстовые свойства его не трогают (остается как было).
    private void SelectValue(int index, BottomTextTarget? target)
    {
        HighlightIndex(index, focus: true);
        if (target is not null) OpenBottom(target);
    }

    // Подсветить карточку как единственную выделенную: у свойства это набор из одного элемента (тот
    // же, что у меню и клавиш) — иначе после клика по значению «Удалить» из меню унесло бы прежний
    // набор; у «Названия» — одиночная подсветка вне набора.
    private void HighlightIndex(int index, bool focus)
    {
        if (index == _titleIndex)
        {
            if (!_titleSelected)
            {
                ClearPropertySelection();
                _titleSelected = true;
                PropertiesHost.RefreshValues();
            }
            if (focus) PropertiesHost.Surface.Focus();
            return;
        }
        if (index >= 0 && index < _cards.Count && _cards[index].Tag is PropertyRowVm { Field.Id: int fid })
            SelectProperty(fid, shift: false, focus);
    }

    // Выделить цель нижнего блока по модели — карточка может быть и не в окне.
    private void HighlightTarget(BottomTextTarget target, bool focus)
    {
        if (target.Index < 0) return;   // пустое свойство может быть открыто снизу, но скрыто в списке
        if (target.IsTitle) { HighlightIndex(_titleIndex, focus); return; }
        if (target.FieldId is int fid) SelectProperty(fid, shift: false, focus);
    }

    // Переключить нижний блок на цель: заголовок + текст в редактор (служебно, под guard'ом).
    private void OpenBottom(BottomTextTarget target)
    {
        _bottomTarget  = target;
        _bottomFieldId = target.FieldId;
        ApplyBottomMode(target);
        UpdateBottomHeader(target);

        _suppressAutoSave = true;
        MarkdownEditor.Text = target.Read();
        _suppressAutoSave = false;
        UpdatePreviewVisibility();
    }

    // Заголовок-крошка нижнего блока: «Свойство» › Имя. Статичную часть («Свойство» +
    // стрелка) рисует XAML; здесь — имя активного свойства, серым. Цели нет (коллекция без
    // описания, запись только что открыта) — вместо имени зовущая ссылка + «Добавить описание».
    private void UpdateBottomHeader(BottomTextTarget? target)
    {
        BottomTitleHost.Children.Clear();
        if (target is not null)
        {
            BottomTitleHost.Children.Add(new TextBlock
            {
                Text              = target.Name,
                Foreground        = (IBrush)this.FindResource("BrushSecondaryText")!,
                VerticalAlignment = VerticalAlignment.Center,
            });
            return;
        }

        if (AddDescriptionLink() is { } link) BottomTitleHost.Children.Add(link);
    }

    // Ссылка «+ «Добавить описание»» — только пока поля с ролью «Описание» у коллекции нет и есть
    // право создавать свойства. Показывается вместо имени свойства, то есть лишь когда снизу
    // пусто: стоит выбрать текстовую карточку — крошка занята ее именем, и ссылка уходит до
    // следующего открытия записи. Клик уходит наверх (AddDescriptionRequested): поле заводит
    // контроллер, чтобы колонка появилась и в таблице.
    private Control? AddDescriptionLink()
    {
        if (_vm is null || _vm.DescriptionFieldId is not null) return null;
        if (_repo?.Permissions is { } perms && !perms.Can(PermAction.CreateField, _vm.CollectionId)) return null;

        // Плюс — часть самой подписи, а не иконка: один TextBlock, поэтому символ идет
        // ровно в размер текста крошки и не требует отдельной подгонки.
        var link = new TextBlock
        {
            Text              = "+ «Добавить описание»",
            Foreground        = (IBrush)this.FindResource("BrushAccent")!,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor            = new Cursor(StandardCursorType.Hand),
            Background        = Brushes.Transparent,   // иначе клик мимо букв не ловится
        };
        link.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            _focusBottomAfterReload = true;   // после пересборки каретка встанет в редактор
            AddDescriptionRequested?.Invoke(this, EventArgs.Empty);
        };
        return link;
    }

    // Вернуть нижний блок на ранее открытую цель («Название» или поле по id) после перестроения
    // списка; если ее больше нет — на поле «Описание». Текст не перечитываем, пока пользователь
    // печатает в редакторе.
    private void RestoreBottom()
    {
        var target = _bottomIsTitle ? _titleTarget
                   : _bottomFieldId is int id && _fieldTargets.TryGetValue(id, out var t) ? t
                   : _descTarget;
        if (target is null)
        {
            // Ни описания, ни другого текстового свойства — снизу пусто (редактор чистим, чтобы
            // не показывать текст прошлой записи), в крошке остается «Добавить описание».
            _bottomTarget  = null;
            _bottomFieldId = null;
            _bottomIsTitle = false;
            MarkdownEditor.AcceptsReturn = true;
            MarkdownEditor.Watermark     = "Пишите в Markdown...";
            UpdateBottomHeader(null);
            _suppressAutoSave = true;
            MarkdownEditor.Text = "";
            _suppressAutoSave = false;
            UpdatePreviewVisibility();
            return;
        }

        _bottomTarget  = target;
        _bottomFieldId = target.FieldId;
        ApplyBottomMode(target);
        UpdateBottomHeader(target);

        if (!MarkdownEditor.IsFocused)
        {
            _suppressAutoSave = true;
            MarkdownEditor.Text = target.Read();
            _suppressAutoSave = false;
        }
        UpdatePreviewVisibility();

        // Подсветить цель, не трогая фокус, если человек печатает снизу: перечитывание не должно
        // выдергивать каретку из редактора.
        HighlightTarget(target, focus: !MarkdownEditor.IsFocused);

        // Описание только что создали ссылкой в крошке — сразу отдаем каретку редактору,
        // чтобы можно было писать, не кликая по блоку.
        if (_focusBottomAfterReload)
        {
            _focusBottomAfterReload = false;
            if (!FooterBar.IsPreview) MarkdownEditor.Focus();
        }
    }

    // Правка в инлайн-поле карточки → обновить большой редактор, если это поле открыто снизу.
    private void SyncBottomFromInline(TextBox tb)
    {
        if (_bottomTarget?.Inline != tb) return;
        if ((MarkdownEditor.Text ?? "") == (tb.Text ?? "")) return;
        _suppressAutoSave = true;
        MarkdownEditor.Text = tb.Text;
        _suppressAutoSave = false;
        UpdatePreviewVisibility();
    }

    // Ключ иконки типа свойства в реестре Icons.axaml.
    private static string IconKeyFor(DbField f) => f.FieldType switch
    {
        "number"   => "IconFieldNumber",
        "date"     => "IconCalendarDate",
        "select"   => "IconFieldSelect",
        "status"   => "IconFieldStatus",
        "multiselect" => "IconFieldMultiSelect",
        "checkbox" => "IconFieldCheckbox",
        "url"      => "IconFieldUrl",
        "file"     => "IconFieldFile",
        "relation" => "IconFieldRelation",
        "lookup"   => "IconFieldRelation",
        "rollup"   => "IconFieldNumber",
        "user"     => "IconUser",
        // Вычисляемые типы: та же иконка, что и у дат/пользователя (различаются подписью).
        "created_at" or "updated_at" => "IconCalendarDate",
        "date_period" => "IconClock",
        "created_by" or "updated_by" => "IconUser",
        _          => "IconFieldText",
    };

    // Ручка ⋮: общую ширину колонки названий тянет поверхность списка; здесь — запоминание в
    // конце перетаскивания (как и высоты свойств: не на каждое движение мыши).
    private void OnNameColumnResized(double width)
    {
        _nameColWidth = width;
        if (ConstructorSettings.RememberLayout)
            AppSettings.SaveConstructorNameColWidth(width);
    }

    // Сохранить значение свойства И сообщить наверх (RecordChanged) — чтобы таблица обновила ЭТУ
    // строку точечно (RefreshRecordRow), а не перезагружалась целиком. Дешево: одна строка на месте.
    private void PersistProperty(PropertyRowVm row)
    {
        ApplyFileSideEffects(row.Persist());
        NotifyRecordChanged();
        PropertiesHost.RefreshValues();
    }

    // Авто-привязка пересчитала файл-столбцы этой записи (правка столбца-источника, в т.ч.
    // «Названия»/«Описания») — обновляем их редакторы сразу, без перезагрузки панели.
    private void ApplyFileSideEffects(IReadOnlyList<(int FieldId, string Value)> changes)
    {
        foreach (var (fid, val) in changes)
        {
            // В модель — всегда: карточка файла могла быть не показана и построится позже.
            if (_rowsByField.TryGetValue(fid, out var row)) row.Value = val;
            if (_fileEditors.TryGetValue(fid, out var ed)) ed.SyncStorage(val);
        }
        PropertiesHost.RefreshValues();
        SchedulePropertyFilter();
    }

    // Безбордерное текстовое поле значения внутри карточки. Класс propValue убирает рамку/фон
    // во ВСЕХ состояниях (в т.ч. при фокусе) — иначе Fluent рисует синюю рамку «бокс в боксе».
    // MaxLines=1 держит инлайн-поле в одну строку: многострочное значение (markdown-абзацы
    // текстового свойства) не растягивает карточку — полностью правится в нижнем блоке.
    private static TextBox FlatValueBox(string? text = null, string? watermark = null) => new()
    {
        Text      = text,
        Watermark = watermark,
        Classes   = { "propValue" },
        MaxLines   = 1,
    };

    // Показ значения текстового свойства в карточке — ТОЛЬКО ПЕРВАЯ строка: остальные
    // не показываем вовсе. MaxLines=1 сам по себе не помогает — он ограничивает перенос
    // по ширине, а явные переводы строк все равно рисуются, и текст вылезает за карточку
    // фиксированной высоты (виден кусками, по-разному после каждой синхронизации).
    // Усеченный показ править нельзя: правка записала бы первую строку поверх всего
    // значения и стерла остальные — поле становится read-only (обратную запись гасит и
    // проверка IsReadOnly в его TextChanged), полный текст правится в нижнем блоке, он и
    // так открывается по клику на карточку. Однострочное значение правится инлайн, как раньше.
    private void ApplyInlineText(TextBox tb, string value)
    {
        var brk     = value.IndexOfAny(['\r', '\n']);
        var display = brk < 0 ? value : value[..brk];

        tb.IsReadOnly = brk >= 0;
        if ((tb.Text ?? "") == display) return;

        // Установка служебная — TextChanged поля не должен записать показ обратно в значение.
        _suppressBottomSync = true;
        tb.Text = display;
        _suppressBottomSync = false;
    }

    private Control MakeInputForField(PropertyRowVm row)
    {
        var f = row.Field;
        Control input;

        // «Вычисляемые» типы (дата/автор создания-изменения) read-only: показываем
        // отформатированное значение из аудит-метаданных, без редактора. Значение в row.Value
        // уже сырое из метаданных (см. RecordPageViewModel), форматируем для показа.
        if (FieldTypeInfo.IsComputed(f.FieldType))
        {
            var box = FlatValueBox(FieldTypeInfo.FormatComputedDisplay(row.Value, f.FieldType));
            box.IsReadOnly  = true;
            box.Foreground  = (IBrush)this.FindResource("BrushSecondaryText")!;   // авто-значение — приглушенно
            return box;
        }

        switch (f.FieldType)
        {
            case "number":
            {
                // Число правится В ТОМ ЖЕ ВИДЕ, В КАКОМ ПОКАЗАНО. Раньше фокус подменял формат
                // на сырое значение, и «1 234,50 ₽» превращалось под курсором в «1234.5» —
                // строка меняла длину, текст прыгал вбок на клике. Теперь на входе в правку не
                // меняется ничего: в канон хранения строку переводит TryParseDisplay, а показ
                // приводится к формату поля по уходу фокуса.
                var fmt = NumberFormatValue.FromConfig(f.ConfigJson);
                var tb  = FlatValueBox(NumberFormatValue.Format(row.Value, fmt));
                var suppress = false;
                // Посимвольный фильтр (цифры, один разделитель, ведущий минус) — только у
                // «обычного числа», где показ совпадает с хранением. В остальных форматах в
                // строке законно стоят разряды, знак валюты и «%»; там ввод разбирается целиком.
                if (fmt.Kind == NumberFormatKind.Plain)
                    tb.AddHandler(InputElement.TextInputEvent, OnNumberTextInput, RoutingStrategies.Tunnel);
                tb.LostFocus += (_, _) => { suppress = true; tb.Text = NumberFormatValue.Format(row.Value, fmt); suppress = false; };
                tb.TextChanged += (_, _) =>
                {
                    if (suppress) return;   // программная подмена показа — не значение
                    // Событие отложенное и приходит уже после снятия suppress, поэтому сверяем не
                    // флаг, а результат: отформатированный показ разбирается обратно в то же самое
                    // значение — значит это установка, а не правка.
                    var text    = tb.Text ?? "";
                    var storage = text.Trim().Length == 0 ? ""
                                : NumberFormatValue.TryParseDisplay(text, fmt, fmt.DecimalSeparator, out var s) ? s
                                : text;   // не разобралось — храним как набрано (так же, как нечисловое)
                    if (storage == (row.Value ?? "")) return;
                    row.Value = storage;
                    PersistProperty(row);
                };
                input = tb;
                break;
            }
            case "date":
            {
                // Тот же редактор, что и в таблице: хранит/отдает канон DateValue (диапазоны,
                // дата+время, «весь день»). Flat — без собственной рамки (рамку дает карточка).
                var editor = new DateEditor
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Flat                = true,
                    Storage             = row.Value,
                };
                editor.Changed += (_, _) => { row.Value = editor.Storage ?? ""; PersistProperty(row); };
                input = editor;
                break;
            }
            case "select":
            case "status":  // как select: опции — строки из ConfigJson
            case "user":   // как select: опции — допущенные пользователи базы (живые)
            {
                // Опции пользователей читаются при каждом построении ПАНЕЛИ: состав меняют
                // соседи. Текущее значение добавляется к ним, даже если такого пользователя
                // в базе уже нет, — историческое имя остается видимым и не затирается.
                var combo = new ComboBox
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Classes             = { "propValue" },
                    ItemsSource         = f.FieldType == "user"
                        ? UserOptions(row.Value)
                        : SelectOptions(f),
                    SelectedItem        = row.Value,
                };
                combo.SelectionChanged += (_, _) =>
                {
                    row.Value = combo.SelectedItem?.ToString() ?? "";
                    PersistProperty(row);
                };
                input = combo;
                break;
            }
            case "multiselect":   // несколько опций: чипы + выпадающий чеклист (значение — JSON-массив)
            {
                var box = new Controls.Components.MultiSelectBox(SelectOptions(f), row.Value, flat: true,
                    separator: MultiSelectValue.SeparatorFromConfig(f.ConfigJson))
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    // Отступ значения задает карточка — у всех остальных типов он нулевой.
                    // Со своими 8px мультисписок стоял правее соседей по вертикали значений.
                    ContentPadding      = new Thickness(0, 4),
                };
                box.Changed += (_, _) => { row.Value = box.Storage; PersistProperty(row); };
                input = box;
                break;
            }
            case "checkbox":
            {
                var cb = new CheckBox
                {
                    IsChecked         = row.Value is "1" or "true" or "True",
                    VerticalAlignment = VerticalAlignment.Center,
                };
                cb.IsCheckedChanged += (_, _) =>
                {
                    row.Value = cb.IsChecked == true ? "1" : "0";
                    PersistProperty(row);
                };
                input = cb;
                break;
            }
            case "url":
            {
                var tb = FlatValueBox(row.Value, "https://");
                tb.TextChanged += (_, _) =>
                {
                    var text = tb.Text ?? "";
                    if (text == (row.Value ?? "")) return;   // отложенное TextChanged от установки текста — не правка
                    row.Value = text;
                    PersistProperty(row);
                };
                input = tb;
                break;
            }
            case "file":
            {
                // Тот же редактор, что и в таблице: путь к файлу, ссылка-открытие, ручной ввод +
                // иконка-скрепка (системный выбор). Flat — без собственной рамки (ее дает карточка).
                var editor = new FileEditor
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Flat                = true,
                    AutoFolder          = FileAutoRule.FromConfig(f.ConfigJson)?.Folder ?? "",   // read-only, если авто
                    Storage             = row.Value,
                };
                // Ручную правку сохраняем только в ручном режиме (в авто значения ведет правило).
                editor.Changed += (_, _) => { row.Value = editor.Storage ?? ""; PersistProperty(row); };
                if (f.Id is int ffid) _fileEditors[ffid] = editor;   // для мгновенного обновления при правке источника
                input = editor;
                break;
            }
            case "relation":
            {
                // Названия связанных записей текстом + иконка-стрелка справа (чеклист выбора) — тот
                // же контрол, что и в таблице, без рамки (ее дает карточка). «→» у пункта → переход.
                var cfg = RelationConfig.FromConfig(f.ConfigJson);
                if (cfg is null || _repo is null || f.Id is not int relFid)
                {
                    var note = FlatValueBox("Связь не настроена");
                    note.IsReadOnly = true;
                    note.Foreground = (IBrush)this.FindResource("BrushSecondaryText")!;
                    input = note;
                    break;
                }
                var rel = new RelationSelectBox(_repo, relFid, cfg, editing: true)
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    RecordId            = _vm?.RecordId ?? 0,
                };
                rel.Navigate += (_, t) => NavigateTo(t.DbId, t.RecordId);
                rel.Changed += (_, _) =>
                {
                    _relationText.Remove(relFid);
                    _relationHasValues.Remove(relFid);
                    NotifyRecordChanged();
                    PropertiesHost.RefreshValues();
                    SchedulePropertyFilter();
                };
                input = rel;
                break;
            }
            default:  // text
            {
                var tb = FlatValueBox();
                ApplyInlineText(tb, row.Value ?? "");
                tb.TextChanged += (_, _) =>
                {
                    if (_suppressBottomSync) return;   // программная установка из нижнего блока
                    // Значение многострочное — в поле лежит только первая строка (read-only):
                    // писать ее обратно нельзя, иначе остальные строки пропали бы. Проверка
                    // дублирует guard выше: TextChanged приходит и после его снятия.
                    if (tb.IsReadOnly) return;
                    var text = tb.Text ?? "";
                    if (text == (row.Value ?? "")) return;   // отложенное TextChanged от установки текста — не правка
                    row.Value = text;
                    PersistProperty(row);
                    SyncBottomFromInline(tb);          // если поле открыто снизу — обновить большой редактор
                };
                input = tb;
                break;
            }
        }

        return input;
    }

    // Фильтр ввода числового поля (как в TableView): символ принимается только если итоговый
    // текст остается допустимым (частичным) числом — цифры, один разделитель, ведущий минус.
    private static void OnNumberTextInput(object? sender, TextInputEventArgs e)
    {
        if (sender is not TextBox tb || string.IsNullOrEmpty(e.Text)) return;

        var text  = tb.Text ?? "";
        var start = Math.Min(tb.SelectionStart, tb.SelectionEnd);
        var end   = Math.Max(tb.SelectionStart, tb.SelectionEnd);
        var prospective = text[..start] + e.Text + text[end..];

        if (!IsPartialNumber(prospective)) e.Handled = true;
    }

    // Допускает незавершенный ввод: "", "-", "12", "12,", "-3.5".
    private static bool IsPartialNumber(string s)
    {
        if (s.Length == 0) return true;
        var i = s[0] == '-' ? 1 : 0;
        var seps = 0;
        for (; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '.' or ',') { if (++seps > 1) return false; }
            else if (!char.IsDigit(c)) return false;
        }
        return true;
    }

    private static List<string> SelectOptions(DbField field)
    {
        try
        {
            using var doc = JsonDocument.Parse(field.ConfigJson ?? "{}");
            if (doc.RootElement.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                return opts.EnumerateArray().Select(o => o.ToString()).ToList();
        }
        catch { /* ignore bad json */ }
        return [];
    }
}
