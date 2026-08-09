using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using SmartBase.Controls.Widgets;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.ViewModels.ConstructorDb;
using Path = Avalonia.Controls.Shapes.Path;

namespace SmartBase.Views.ConstructorDb;

/// <summary>
/// Правая панель «Страница записи» (Obsidian-стиль): свойства сверху + markdown снизу.
/// Сохранение значений идёт сразу в БД при изменении (как в TableView).
/// </summary>
public partial class RecordPageView : UserControl
{
    public event EventHandler<int>? RecordChanged;
    public event EventHandler<bool>? CollectionPanelToggled;

    /// <summary>Клик по чипу поля-связи: открыть связанную запись (её Id; коллекция может быть
    /// другой). Обрабатывает MainWindow → OpenRecordPanel (коллекция резолвится у самой записи).</summary>
    public event EventHandler<int>? RelatedRecordRequested;

    /// <summary>Переход к записи в ДРУГОЙ БД (чип межбазовой связи): (DbId цели, Id записи).</summary>
    public event EventHandler<(int DbId, int RecordId)>? CrossDbRelatedRecordRequested;

    /// <summary>Клик по «Добавить свойство»: создать новое поле текущей коллекции.
    /// Обрабатывает MainWindow → контроллер (тот же путь, что и «+» в таблице),
    /// чтобы новое поле сразу появилось и в таблице, и в панели.</summary>
    public event EventHandler? AddPropertyRequested;

    /// <summary>Клик по «Добавить описание» в крошке нижнего блока: завести полю коллекции
    /// роль «Описание». Тот же путь наверх, что и у «Добавить свойство» — контроллер создаёт
    /// поле, добавляет колонку в таблицу и шлёт FieldsChanged, по которому панель перечитывается
    /// и открывает нижний блок уже на описании.</summary>
    public event EventHandler? AddDescriptionRequested;

    private RecordPageViewModel? _vm;
    private ConstructorRepository? _repo;   // для перечитывания записи из БД (ReloadFromDb)

    // База ПОКАЗЫВАЕМОЙ записи (0 — активная). Панель может быть открыта межбазовым переходом, и тогда
    // «текущая база» для неё и для приложения — РАЗНЫЕ. Без этого внутрибазовая связь чужой записи
    // уводила бы в активную базу (см. Navigate ниже).
    private int _dbId;

    // Гасит авто-сейв описания на время служебной установки текста (Show/ReloadFromDb), чтобы
    // программное MarkdownEditor.Text не приняли за правку пользователя (иначе перечитанное из БД
    // значение тут же писалось бы обратно и дёргало таблицу). Имя гасит сам NavbarPage (SetTitle).
    private bool _suppressAutoSave;

    // ── Нижний блок = редактор активного ТЕКСТОВОГО свойства ───────────────────
    // Снизу виден один редактор (Markdown/обычный текст). Что он редактирует, задаёт
    // _bottomTarget: по умолчанию — поле с ролью «Описание», дальше выбор текстовой карточки
    // переключает цель. Нет ни одного текстового свойства (в т.ч. описания) — снизу пусто.
    // Не-текстовые карточки нижний блок не трогают.
    private sealed class BottomTextTarget
    {
        public int?            FieldId;          // id текстового поля-цели
        public string          Name = "";
        public string          IconKey = "IconFieldText";   // иконка типа (для заголовка-крошки)
        public Func<string>    Read  = () => "";
        public Action<string>  Write = _ => { }; // сохранить значение (+ уведомить таблицу)
        public TextBox?        Inline;           // инлайн-поле в карточке (текстовые свойства) — для синхронизации
        public Border?         Card;             // карточка цели — для подсветки
    }

    private BottomTextTarget? _bottomTarget;
    private int?              _bottomFieldId;    // id поля, открытого снизу — для восстановления
    private readonly Dictionary<int, BottomTextTarget> _fieldTargets = new();
    // Файл-редакторы по FieldId — чтобы обновить показ авто-file поля сразу при правке столбца-источника.
    private readonly Dictionary<int, Controls.Widgets.FileEditor> _fileEditors = new();
    private BottomTextTarget? _descTarget;      // цель по умолчанию — поле с ролью «Описание»
    private bool _focusBottomAfterReload;       // описание только что создано — поставить каретку в редактор
    private bool _suppressBottomSync;            // гасит обратную синхронизацию инлайн↔нижний при программной установке

    public RecordPageView()
    {
        InitializeComponent();

        FooterBar.PreviewToggled += (_, _) => UpdatePreviewVisibility();

        // Живая запись имени по символу — как ячейка таблицы: правка имени в навбаре панели
        // сразу видна в строке таблицы (RecordChanged → RefreshRecordRow). NavbarPage сам гасит
        // событие при программной установке имени (SetTitle), поэтому здесь — только правки пользователя.
        HeaderBar.TitleChanged += (_, text) =>
        {
            if (_vm is null) return;
            var entered = text.Trim();
            _vm.Title = string.IsNullOrEmpty(entered) ? "Без названия" : entered;
            ApplyFileSideEffects(_vm.SaveTitle());   // «Название» — источник авто-file: обновить файл-ячейки
            RecordChanged?.Invoke(this, _vm.RecordId);
        };

        // Живая запись активного текстового свойства по символу. Цель (поле «Описание» либо
        // другое текстовое поле) задаёт _bottomTarget. Guard _suppressAutoSave — для служебной
        // установки текста. Параллельно синхронизируем инлайн-поле карточки той же цели.
        MarkdownEditor.TextChanged += (_, _) =>
        {
            if (_vm is null || _suppressAutoSave || _bottomTarget is null) return;
            var text = MarkdownEditor.Text ?? "";
            _bottomTarget.Write(text);   // сохраняет значение и уведомляет таблицу

            if (_bottomTarget.Inline is { } inline && inline.Text != text)
            {
                _suppressBottomSync = true;
                inline.Text = text;
                _suppressBottomSync = false;
            }
        };

        HeaderBar.CollectionPanelToggled += (_, open) => CollectionPanelToggled?.Invoke(this, open);
    }

    /// <summary>Синхронизирует визуальное состояние кнопки с фактической видимостью блока коллекции.</summary>
    public void SetCollectionPanelOpen(bool isOpen) => HeaderBar.SetCollectionPanelOpen(isOpen);

    /// <param name="dbId">База показываемой записи: 0 — активная, &gt;0 — открыта межбазовым переходом.</param>
    public void Show(ConstructorRepository repository, int recordId, int collectionId, int dbId = 0)
    {
        _repo = repository;
        _dbId = dbId;
        _vm = new RecordPageViewModel(repository, recordId, collectionId);
        DataContext = _vm;

        HeaderBar.SetTitle(_vm.Title == "Без названия" ? "" : _vm.Title);

        FooterBar.IsPreview = false;

        BuildProperties();
        ApplyPropsHeight();

        // Новая запись — нижний блок по умолчанию на «Описании»; описания у коллекции нет —
        // снизу пусто, а в крошке снова видна ссылка + «Добавить описание» (выбор другого
        // текстового свойства её прячет, но живёт он только до следующего открытия записи).
        _bottomFieldId = null;
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
        if (_vm is null || _repo is null) return;

        var recordId     = _vm.RecordId;
        var collectionId = _vm.CollectionId;
        var preview      = FooterBar.IsPreview;

        _vm = new RecordPageViewModel(_repo, recordId, collectionId);
        DataContext = _vm;

        // Заголовок — только если он не в режиме редактирования (SetTitle сам не дёргает сохранение).
        if (!HeaderBar.IsTitleEditing)
            HeaderBar.SetTitle(_vm.Title == "Без названия" ? "" : _vm.Title);

        BuildProperties();
        ApplyPropsHeight();

        FooterBar.IsPreview = preview;

        // Возвращаем нижний блок на ту же цель (по id), если она ещё есть; иначе — «Описание».
        // Текст не перечитываем, пока редактор в фокусе (не затереть набираемое).
        RestoreBottom();
    }

    public void Clear()
    {
        _vm = null;
        _repo = null;
        DataContext = null;
        HeaderBar.SetTitle("");
        MarkdownEditor.Text  = "";
        MarkdownPreview.Markdown = "";
        BottomTitleHost.Children.Clear();
        PropertiesHost.Children.Clear();
        _nameCols.Clear();
        _selectedCard = null;
        _bottomTarget = null;
        _bottomFieldId = null;
        _descTarget = null;
        _fieldTargets.Clear();
        _fileEditors.Clear();
    }

    private void UpdatePreviewVisibility()
    {
        var preview = FooterBar.IsPreview;
        MarkdownPreviewBox.IsVisible = preview;
        MarkdownEditor.IsVisible     = !preview;

        if (preview)
            MarkdownPreview.Markdown = MarkdownEditor.Text ?? "";
    }

    // ── Перетаскивание ручки между свойствами и описанием ──────────────────────
    //
    // По умолчанию свойства и описание делят высоту поровну (обе строки 1*, 50/50);
    // ручка-пилюля сидит на границе. Перетаскивание задаёт строке свойств пиксельную
    // высоту: тянем вниз — свойства растут (описание сжимается, оно в Star-строке),
    // тянем вверх — свойства сжимаются, и если их больше, чем влезает, ScrollViewer
    // даёт прокрутку. Выбранная высота помнится в пределах сессии и переносится
    // между записями (на диск не пишем — у разных записей разное число свойств).

    private bool    _propsGripDragging;
    private double  _propsGripStartY;
    private double  _propsGripStartHeight;
    private double? _propsHeight;   // выбранная пользователем высота блока свойств (null = Auto)

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
        // Не даём свойствам съесть всё описание: оставляем ему запас (шапка+футер+минимум описания).
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
    }

    // Общая ширина колонки названий (px) — делится всеми карточками; меняется
    // перетаскиванием ручки-разделителя ⋮. Живёт в пределах сессии (переносится между записями).
    private readonly List<ColumnDefinition> _nameCols = new();
    private double _nameColWidth = 150;
    private const double NameColMin = 70;

    private Border? _selectedCard;

    private void BuildProperties()
    {
        PropertiesHost.Children.Clear();
        _nameCols.Clear();
        _fieldTargets.Clear();
        _fileEditors.Clear();
        _descTarget   = null;
        _bottomTarget = null;
        _selectedCard = null;
        if (_vm is null) return;

        // Порядок строго как в таблице: VM уже отсортировала элементы по эффективной позиции
        // колонки. «Описание» — обычное поле и идёт в этом же списке, отдельной карточки у него нет.
        foreach (var item in _vm.OrderedItems)
        {
            switch (item)
            {
                case FieldItem fi: PropertiesHost.Children.Add(BuildPropertyCard(fi.Row)); break;
            }
        }

        // Цель нижнего блока по умолчанию — поле с ролью «Описание» (если оно есть и текстовое:
        // после смены типа цели у него нет, и блок остаётся пустым, пока не выберут другое свойство).
        _descTarget = _vm.DescriptionFieldId is int did && _fieldTargets.TryGetValue(did, out var dt)
            ? dt : null;

        // «Добавить свойство» — единственная карточка с фиксированным местом (всегда внизу).
        // Тоже входит в общий ресайз колонки названий (ручка ⋮).
        PropertiesHost.Children.Add(BuildAddPropertyCard());
    }

    // Каркас карточки: [иконка + название] · [ручка ⋮] · [контент-значение]. Колонка названия
    // регистрируется в общем ресайзе. selectableName=true → подпись с классом propname (синеет
    // в выделенной карточке); cssClass — proprow/addprop.
    private Border BuildCardShell(string cssClass, Control icon, string name, bool selectableName, Control? valueContent)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
            {
                new ColumnDefinition(new GridLength(_nameColWidth, GridUnitType.Pixel)),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(new GridLength(1, GridUnitType.Star)),
            },
        };
        _nameCols.Add(grid.ColumnDefinitions[0]);

        // Название: иконка (Auto) + подпись (*). Подпись в звёздной колонке ограничена шириной
        // колонки названий — при сужении ручкой ⋮ обрезается многоточием, не наезжая на значение.
        var nameRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds      = true,
        };
        Grid.SetColumn(icon, 0);
        var nameLabel = new TextBlock
        {
            Text              = name,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Margin            = new Thickness(8, 0, 4, 0),
            FontSize          = (double)this.FindResource("FontSizeBase")!,
        };
        if (selectableName) nameLabel.Classes.Add("propname");
        else                nameLabel.Foreground = (IBrush)this.FindResource("BrushSecondaryText")!;
        Grid.SetColumn(nameLabel, 1);
        nameRow.Children.Add(icon);
        nameRow.Children.Add(nameLabel);
        Grid.SetColumn(nameRow, 0);

        var grip = BuildColumnGrip();
        Grid.SetColumn(grip, 1);

        grid.Children.Add(nameRow);
        grid.Children.Add(grip);
        if (valueContent is not null)
        {
            valueContent.VerticalAlignment = VerticalAlignment.Center;
            // Отбиваем значение от ручки ⋮ на те же 10px, что иконка отбита от левого края
            // карточки (Padding=10). Ручка 18px центрирует точки 3px → справа уже есть 7.5px,
            // добавляем недостающие 2.5px.
            valueContent.Margin = new Thickness(2.5, 0, 0, 0);
            Grid.SetColumn(valueContent, 2);
            grid.Children.Add(valueContent);
        }

        return new Border { Classes = { cssClass }, Child = grid };
    }

    // Карточка свойства: иконка типа + название · ручка ⋮ · значение. Клик выделяет карточку
    // (стиль выбранной строки сайдбара). Для типа «текст» — ещё и цель нижнего блока.
    private Border BuildPropertyCard(PropertyRowVm row)
    {
        var input = MakeInputForField(row);
        var card  = BuildCardShell("proprow", BuildTypeIcon(row.Field), row.Field.Name, selectableName: true, input);

        // Текстовое свойство → цель нижнего блока: инлайн-поле и большой редактор синхронны.
        BottomTextTarget? target = null;
        if (row.Field.FieldType == "text" && input is TextBox tb)
        {
            target = new BottomTextTarget
            {
                FieldId = row.Field.Id,
                Name    = row.Field.Name,
                IconKey = IconKeyFor(row.Field),
                Read    = () => row.Value ?? "",
                Write   = t => { row.Value = t; PersistProperty(row); },
                Inline  = tb,
                Card    = card,
            };
            if (row.Field.Id is int fid) _fieldTargets[fid] = target;
        }

        // Туннелем (до детей) — выделяем карточку и (для текстовых) открываем нижний блок;
        // поле ввода при этом всё равно получает клик и фокус (событие не помечаем обработанным).
        card.AddHandler(InputElement.PointerPressedEvent, (_, _) => SelectCard(card, target),
                        RoutingStrategies.Tunnel);
        return card;
    }

    // Выделяем карточку всегда; нижний блок переключаем только на ТЕКСТОВУЮ цель —
    // не-текстовые свойства его не трогают (остаётся как было).
    private void SelectCard(Border card, BottomTextTarget? target)
    {
        HighlightCard(card);
        if (target is not null) OpenBottom(target);
    }

    private void HighlightCard(Border card)
    {
        if (ReferenceEquals(_selectedCard, card)) return;
        _selectedCard?.Classes.Remove("selected");
        card.Classes.Add("selected");
        _selectedCard = card;
    }

    // Переключить нижний блок на цель: заголовок + текст в редактор (служебно, под guard'ом).
    private void OpenBottom(BottomTextTarget target)
    {
        _bottomTarget  = target;
        _bottomFieldId = target.FieldId;
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
    // пусто: стоит выбрать текстовую карточку — крошка занята её именем, и ссылка уходит до
    // следующего открытия записи. Клик уходит наверх (AddDescriptionRequested): поле заводит
    // контроллер, чтобы колонка появилась и в таблице.
    private Control? AddDescriptionLink()
    {
        if (_vm is null || _vm.DescriptionFieldId is not null) return null;
        if (_repo?.Permissions is { } perms && !perms.Can(PermAction.CreateField, _vm.CollectionId)) return null;

        // Плюс — часть самой подписи, а не иконка: один TextBlock, поэтому символ идёт
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

    // Вернуть нижний блок на ранее открытую цель (по id) после перестроения списка; если её
    // больше нет — на поле «Описание». Текст не перечитываем, пока пользователь печатает в редакторе.
    private void RestoreBottom()
    {
        var target = _bottomFieldId is int id && _fieldTargets.TryGetValue(id, out var t)
            ? t : _descTarget;
        if (target is null)
        {
            // Ни описания, ни другого текстового свойства — снизу пусто (редактор чистим, чтобы
            // не показывать текст прошлой записи), в крошке остаётся «Добавить описание».
            _bottomTarget  = null;
            _bottomFieldId = null;
            UpdateBottomHeader(null);
            _suppressAutoSave = true;
            MarkdownEditor.Text = "";
            _suppressAutoSave = false;
            UpdatePreviewVisibility();
            return;
        }

        _bottomTarget  = target;
        _bottomFieldId = target.FieldId;
        UpdateBottomHeader(target);

        if (!MarkdownEditor.IsFocused)
        {
            _suppressAutoSave = true;
            MarkdownEditor.Text = target.Read();
            _suppressAutoSave = false;
        }
        UpdatePreviewVisibility();

        if (target.Card is { } card) HighlightCard(card);

        // Описание только что создали ссылкой в крошке — сразу отдаём каретку редактору,
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
        "user"     => "IconUser",
        // Вычисляемые типы: та же иконка, что и у дат/пользователя (различаются подписью).
        "created_at" or "updated_at" => "IconCalendarDate",
        "created_by" or "updated_by" => "IconUser",
        _          => "IconFieldText",
    };

    // Иконка типа свойства — обводочная, только из реестра Icons.axaml.
    private Control BuildTypeIcon(DbField f) => BuildStrokeIcon(IconKeyFor(f), selectable: true);

    // Обводочная иконка 16×16 из реестра Icons.axaml. selectable=true → класс propicon,
    // чтобы стиль выделенной карточки перекрашивал её в синий; иначе фиксированный серый.
    private Control BuildStrokeIcon(string key, bool selectable)
    {
        var path = new Path
        {
            Data            = (Geometry)this.FindResource(key)!,
            StrokeThickness = (double)this.FindResource("IconStrokeSmall")!,
            StrokeLineCap   = PenLineCap.Round,
            StrokeJoin      = PenLineJoin.Round,
        };
        if (selectable) path.Classes.Add("propicon");
        else            path.Stroke = (IBrush)this.FindResource("BrushSecondaryText")!;

        return new Viewbox
        {
            Width             = 16,
            Height            = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Child             = new Canvas { Width = 24, Height = 24, Children = { path } },
        };
    }

    // Карточка «Добавить свойство»: [иконка «+» + подпись] · [ручка ⋮]. Та же 3-колоночная
    // сетка, что и у свойств, поэтому её колонка названия входит в общий ресайз и сужается
    // вместе со всеми. Клик (не по ручке) открывает редактор свойства; нажатие на ручку ⋮
    // OnColGripPressed помечает обработанным — до пузырькового OnAddPropertyClick, поэтому
    // перетаскивание ручки не открывает диалог, а меняет ширину.
    private Border BuildAddPropertyCard()
    {
        var card = BuildCardShell("addprop", BuildStrokeIcon("IconPlus", selectable: false),
                                  "Добавить свойство", selectableName: false, valueContent: null);
        // Пузырьком: нажатие на ручку ⋮ OnColGripPressed помечает обработанным раньше, поэтому
        // перетаскивание ручки не открывает диалог, а меняет ширину; клик мимо ручки — создаёт поле.
        card.PointerPressed += OnAddPropertyClick;
        return card;
    }

    private Border BuildColumnGrip()
    {
        // Размер точки и шаг — целые и равные: при layout-rounding (вкл. по умолчанию) дробные
        // значения округляются по-разному и зазоры выходят неравномерными (нижние точки ближе).
        var dots = new StackPanel
        {
            Orientation         = Orientation.Vertical,
            Spacing             = 3,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        for (var i = 0; i < 3; i++)
            dots.Children.Add(new Ellipse
            {
                Width  = 3,
                Height = 3,
                Fill   = (IBrush)this.FindResource("BrushSecondaryText")!,
            });

        var grip = new Border { Classes = { "propgrip" }, Child = dots };
        grip.PointerPressed  += OnColGripPressed;
        grip.PointerMoved    += OnColGripMoved;
        grip.PointerReleased += OnColGripReleased;
        return grip;
    }

    // ── Перетаскивание ручки ⋮: общая ширина колонки названий у всех карточек ──────
    private bool   _colGripDragging;
    private double _colGripStartX;
    private double _colGripStartWidth;

    private void OnColGripPressed(object? sender, PointerPressedEventArgs e)
    {
        _colGripDragging   = true;
        _colGripStartX     = e.GetPosition(this).X;
        _colGripStartWidth = _nameColWidth;
        e.Pointer.Capture((IInputElement?)sender);
        e.Handled = true;
    }

    private void OnColGripMoved(object? sender, PointerEventArgs e)
    {
        if (!_colGripDragging) return;

        var dx  = e.GetPosition(this).X - _colGripStartX;
        // Названиям не даём съесть всё значение: оставляем значению минимум места.
        var max = Math.Max(NameColMin, PropertiesHost.Bounds.Width - 160);
        _nameColWidth = Math.Clamp(_colGripStartWidth + dx, NameColMin, max);
        foreach (var col in _nameCols)
            col.Width = new GridLength(_nameColWidth, GridUnitType.Pixel);
    }

    private void OnColGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        _colGripDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnAddPropertyClick(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        AddPropertyRequested?.Invoke(this, EventArgs.Empty);
    }

    // Сохранить значение свойства И сообщить наверх (RecordChanged) — чтобы таблица обновила ЭТУ
    // строку точечно (RefreshRecordRow), а не перезагружалась целиком. Дёшево: одна строка на месте.
    private void PersistProperty(PropertyRowVm row)
    {
        ApplyFileSideEffects(row.Persist());
        if (_vm is not null) RecordChanged?.Invoke(this, _vm.RecordId);
    }

    // Авто-привязка пересчитала файл-столбцы этой записи (правка столбца-источника, в т.ч.
    // «Названия»/«Описания») — обновляем их редакторы сразу, без перезагрузки панели.
    private void ApplyFileSideEffects(IReadOnlyList<(int FieldId, string Value)> changes)
    {
        foreach (var (fid, val) in changes)
            if (_fileEditors.TryGetValue(fid, out var ed)) ed.SyncStorage(val);
    }

    // Безбордерное текстовое поле значения внутри карточки. Класс propValue убирает рамку/фон
    // во ВСЕХ состояниях (в т.ч. при фокусе) — иначе Fluent рисует синюю рамку «бокс в боксе».
    // MaxLines=1 держит инлайн-поле в одну строку: многострочное значение (markdown-абзацы
    // текстового свойства) не растягивает карточку — полностью правится в нижнем блоке.
    private static TextBox FlatValueBox(string? text, string? watermark = null) => new()
    {
        Text      = text,
        Watermark = watermark,
        Classes   = { "propValue" },
        MaxLines   = 1,
    };

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
            box.Foreground  = (IBrush)this.FindResource("BrushSecondaryText")!;   // авто-значение — приглушённо
            return box;
        }

        switch (f.FieldType)
        {
            case "number":
            {
                // Показ по формату поля (валюта/проценты/разряды), пока поле не в фокусе; при
                // фокусе показываем сырое число для правки, на уходе — снова отформатированное.
                // row.Value всегда сырое; программная подмена Text помечается флагом, чтобы не
                // записать отформатированную строку обратно в значение.
                var fmt = NumberFormatValue.FromConfig(f.ConfigJson);
                var tb  = FlatValueBox(NumberFormatValue.Format(row.Value, fmt));
                var suppress = false;
                // Тот же фильтр ввода, что и в ячейке таблицы: пускаем только цифры, один
                // разделитель и ведущий минус — иначе живая запись по символу клала бы в БД
                // промежуточный мусор («-», «12,»).
                tb.AddHandler(InputElement.TextInputEvent, OnNumberTextInput, RoutingStrategies.Tunnel);
                tb.GotFocus  += (_, _) => { suppress = true; tb.Text = row.Value; suppress = false; };
                tb.LostFocus += (_, _) => { suppress = true; tb.Text = NumberFormatValue.Format(row.Value, fmt); suppress = false; };
                tb.TextChanged += (_, _) =>
                {
                    if (suppress) return;   // программная подмена показа/сырого — не значение
                    row.Value = tb.Text ?? "";
                    PersistProperty(row);
                };
                input = tb;
                break;
            }
            case "date":
            {
                // Тот же редактор, что и в таблице: хранит/отдаёт канон DateValue (диапазоны,
                // дата+время, «весь день»). Flat — без собственной рамки (рамку даёт карточка).
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
                // Опции пользователей читаются при каждом построении поля: состав меняют
                // соседи. Текущее значение добавляется к ним, даже если такого пользователя
                // в базе уже нет, — историческое имя остаётся видимым и не затирается.
                var combo = new ComboBox
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Classes             = { "propValue" },
                    ItemsSource         = f.FieldType == "user"
                        ? _repo?.GetUserOptions(row.Value) ?? []
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
                tb.TextChanged += (_, _) => { row.Value = tb.Text ?? ""; PersistProperty(row); };
                input = tb;
                break;
            }
            case "file":
            {
                // Тот же редактор, что и в таблице: путь к файлу, ссылка-открытие, ручной ввод +
                // иконка-скрепка (системный выбор). Flat — без собственной рамки (её даёт карточка).
                var editor = new FileEditor
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Flat                = true,
                    AutoFolder          = FileAutoRule.FromConfig(f.ConfigJson)?.Folder ?? "",   // read-only, если авто
                    Storage             = row.Value,
                };
                // Ручную правку сохраняем только в ручном режиме (в авто значения ведёт правило).
                editor.Changed += (_, _) => { row.Value = editor.Storage ?? ""; PersistProperty(row); };
                if (f.Id is int ffid) _fileEditors[ffid] = editor;   // для мгновенного обновления при правке источника
                input = editor;
                break;
            }
            case "relation":
            {
                // Названия связанных записей текстом + иконка-стрелка справа (чеклист выбора) — тот
                // же контрол, что и в таблице, без рамки (её даёт карточка). «→» у пункта → переход.
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
                rel.Navigate += (_, t) =>
                {
                    // t.DbId = 0 означает «в базе ЭТОЙ записи», а она не обязательно активная: панель
                    // могла быть открыта межбазовым переходом. Подставляем базу панели (_dbId), иначе
                    // внутрибазовая связь чужой записи открывалась бы в АКТИВНОЙ базе — чужая/пустая.
                    var db = t.DbId > 0 ? t.DbId : _dbId;
                    if (db > 0) CrossDbRelatedRecordRequested?.Invoke(this, (db, t.RecordId));
                    else RelatedRecordRequested?.Invoke(this, t.RecordId);            // панель в активной БД
                };
                rel.Changed  += (_, _) => { if (_vm is not null) RecordChanged?.Invoke(this, _vm.RecordId); };
                input = rel;
                break;
            }
            default:  // text
            {
                var tb = FlatValueBox(row.Value);
                tb.TextChanged += (_, _) =>
                {
                    if (_suppressBottomSync) return;   // программная установка из нижнего блока
                    row.Value = tb.Text ?? "";
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
    // текст остаётся допустимым (частичным) числом — цифры, один разделитель, ведущий минус.
    private static void OnNumberTextInput(object? sender, TextInputEventArgs e)
    {
        if (sender is not TextBox tb || string.IsNullOrEmpty(e.Text)) return;

        var text  = tb.Text ?? "";
        var start = Math.Min(tb.SelectionStart, tb.SelectionEnd);
        var end   = Math.Max(tb.SelectionStart, tb.SelectionEnd);
        var prospective = text[..start] + e.Text + text[end..];

        if (!IsPartialNumber(prospective)) e.Handled = true;
    }

    // Допускает незавершённый ввод: "", "-", "12", "12,", "-3.5".
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
