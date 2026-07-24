using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Controls.Dialogs;

/// <summary>Тип поля для ComboBox: обводочная иконка (lucide) + подпись.</summary>
public sealed class FieldTypeItem
{
    public required string   Type  { get; init; }
    public required string   Label { get; init; }
    public required Geometry Icon  { get; init; }
    /// <summary>Первый элемент «системной» группы (вычисляемые типы) — над ним рисуется
    /// разделительная черта в выпадающем списке.</summary>
    public bool StartsGroup { get; init; }
}

/// <summary>
/// Notion-style мини-диалог для создания/изменения одного свойства (поля коллекции).
/// Только два параметра: название и тип. Markdown как тип скрыт (это системное body-поле).
/// Для типа «Дата» есть переключатель «Отображать в календаре» (флаг IsCalStart поля) —
/// несколько полей-дат коллекции могут быть включены одновременно.
/// </summary>
public partial class PropertyEditorDialog : Window
{
    // Иконки берём из общего источника FieldTypeIcons (lucide, 24×24).
    private static readonly FieldTypeItem[] FieldTypes =
    [
        new() { Type = "text",     Label = "Текст",   Icon = Components.FieldTypeIcons.ForType("text")!     },
        new() { Type = "number",   Label = "Число",   Icon = Components.FieldTypeIcons.ForType("number")!   },
        new() { Type = "date",     Label = "Дата",    Icon = Components.FieldTypeIcons.ForType("date")!     },
        new() { Type = "select",   Label = "Список",  Icon = Components.FieldTypeIcons.ForType("select")!   },
        new() { Type = "status",   Label = "Статус",  Icon = Components.FieldTypeIcons.ForType("status")!   },
        new() { Type = "multiselect", Label = "Мультисписок", Icon = Components.FieldTypeIcons.ForType("multiselect")! },
        new() { Type = "checkbox", Label = "Чекбокс", Icon = Components.FieldTypeIcons.ForType("checkbox")! },
        new() { Type = "url",      Label = "Ссылка",  Icon = Components.FieldTypeIcons.ForType("url")!      },
        new() { Type = "file",     Label = "Файл",    Icon = Components.FieldTypeIcons.ForType("file")!     },
        new() { Type = "user",     Label = "Пользователь", Icon = Components.FieldTypeIcons.ForType("user")! },
        // Вычисляемые типы (значение авто из метаданных записи, ячейка read-only). Идут внизу
        // отдельной группой — StartsGroup у первого рисует разделительную черту над ней.
        new() { Type = "created_at", Label = "Дата создания",          Icon = Components.FieldTypeIcons.ForType("created_at")!, StartsGroup = true },
        new() { Type = "updated_at", Label = "Дата изменения",         Icon = Components.FieldTypeIcons.ForType("updated_at")! },
        new() { Type = "created_by", Label = "Создание пользователем",  Icon = Components.FieldTypeIcons.ForType("created_by")! },
        new() { Type = "updated_by", Label = "Изменение пользователем", Icon = Components.FieldTypeIcons.ForType("updated_by")! },
    ];

    private readonly ConstructorRepository _repository;
    private readonly int  _collectionId;
    private readonly int? _editFieldId;

    // Режим системной колонки («Название»/«Описание»): тип зафиксирован «Текст»,
    // запись идёт не в DbFields, а через результат диалога (см. ResultName).
    private readonly bool _systemMode;

    private string _selectedType = "text";

    // Текстовые поля пунктов списка (типы «Список»/«Статус»). Собираются в ConfigJson при
    // сохранении; строки строятся в BuildOptionRow и живут в OptionsList.
    private readonly List<TextBox> _optionBoxes = [];

    // Формат склейки значений «Мультисписка» — порядок совпадает с пунктами SeparatorCombo в XAML.
    private static readonly string[] SeparatorKeys = ["comma", "space", "semicolon", "newline"];

    // Формат числа: порядок пунктов NumberFormatCombo в XAML.
    private static readonly NumberFormatKind[] NumberFormatKinds =
        [NumberFormatKind.Plain, NumberFormatKind.Grouped, NumberFormatKind.Currency, NumberFormatKind.Percent];

    // Строка пунктов, которая сейчас в режиме ввода (поле + действие фиксации). Клик мимо неё
    // (в т.ч. по неинтерактивному фону) фиксирует ввод — см. DismissOptionEditor.
    private (TextBox Box, Action Commit)? _editingOption;

    /// <summary>В системном режиме — введённая подпись (после Сохранить); иначе null.</summary>
    public string? ResultName { get; private set; }

    public PropertyEditorDialog(ConstructorRepository repository, int collectionId, int? editFieldId = null)
    {
        InitializeComponent();

        _repository   = repository;
        _collectionId = collectionId;
        _editFieldId  = editFieldId;

        TypeCombo.ItemsSource = FieldTypes;

        // По умолчанию новое поле-дата отображается в календаре (включено).
        CalDateToggle.IsChecked = true;
        // Задачи — наоборот, только по явному включению (иначе любое user-поле плодило бы задачи).
        TaskToggle.IsChecked = false;
        // Формат «Мультисписка» по умолчанию — запятая (первый пункт).
        SeparatorCombo.SelectedIndex = 0;
        // Формат «Числа» по умолчанию — «Обычное число» (показ = сырому вводу).
        ApplyNumberFormat(NumberFormat.Default);
        // Смена формата числа переключает видимость под-опций (валюта — только для «Денег»,
        // блок опций скрыт для «Обычного числа»).
        NumberFormatCombo.SelectionChanged += (_, _) => UpdateNumberOptionsVisibility();

        if (editFieldId.HasValue)
        {
            Title = "Изменение свойства";
            var field = repository.GetFields(collectionId).FirstOrDefault(f => f.Id == editFieldId);
            if (field is not null)
            {
                NameBox.Text  = field.Name;
                _selectedType = field.FieldType;
                CalDateToggle.IsChecked = field.IsCalStart;
                TaskToggle.IsChecked    = field.IsTaskExec;
                // Существующие опции «Списка»/«Статуса» — в редактор пунктов.
                if (FieldTypeInfo.IsOptionList(field.FieldType))
                    foreach (var o in ParseOptions(field.ConfigJson)) AddOptionRow(o);
                // «Мультисписок» — восстановить выбранный формат склейки значений.
                if (field.FieldType == "multiselect")
                {
                    var sepIdx = Array.IndexOf(SeparatorKeys, MultiSelectValue.SeparatorKeyFromConfig(field.ConfigJson));
                    SeparatorCombo.SelectedIndex = sepIdx >= 0 ? sepIdx : 0;
                }
                // «Число» — восстановить выбранный формат показа (валюта/проценты/разряды).
                if (field.FieldType == "number")
                    ApplyNumberFormat(NumberFormatValue.FromConfig(field.ConfigJson));
            }
        }

        // Добавление пункта: сбрасываем поиск (иначе новая пустая строка не пройдёт фильтр),
        // добавляем строку и ставим в неё фокус.
        AddOptionBtn.Click   += (_, _) => { OptionsSearch.Text = ""; AddOptionRow("").Focus(); };
        OptionsSearch.TextChanged += (_, _) => FilterOptionRows();

        // Клик в любое место диалога мимо активного поля пункта фиксирует его (снимает фокус —
        // каретка гаснет). Tunnel: срабатывает раньше обработчиков строк, поэтому клик по другой
        // строке сперва зафиксирует текущую, затем откроет ту.
        AddHandler(PointerPressedEvent, DismissOptionEditor, RoutingStrategies.Tunnel);

        var idx = Array.FindIndex(FieldTypes, t => t.Type == _selectedType);
        TypeCombo.SelectedIndex = idx >= 0 ? idx : 0;
        TypeCombo.SelectionChanged += (_, _) =>
        {
            var i = TypeCombo.SelectedIndex;
            if (i >= 0 && i < FieldTypes.Length) _selectedType = FieldTypes[i].Type;
            UpdateCalDateVisibility();
        };
        UpdateCalDateVisibility();

        CancelBtn.Click += (_, _) => Close(false);
        SaveBtn.Click   += (_, _) => TrySave();
        NameBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) TrySave(); };
        NameBox.TextChanged += (_, _) => HideError();
    }

    // Переключатель «Отображать в календаре» виден только для типа «Дата»,
    // «Добавить в раздел задач» — только для типа «Пользователь», редактор пунктов —
    // для «Список»/«Статус». Новый «Статус» с пустым набором засеивается тремя статусами
    // по умолчанию (при переключении/открытии) — как у авто-поля задач.
    private void UpdateCalDateVisibility()
    {
        CalDateSection.IsVisible = _selectedType == "date";
        TaskSection.IsVisible    = _selectedType == "user";
        OptionsSection.IsVisible = FieldTypeInfo.IsOptionList(_selectedType);
        // Формат склейки нужен только «Мультисписку» (у select/status значение одно).
        SeparatorSection.IsVisible = _selectedType == "multiselect";
        // Формат числа — только для типа «Число».
        NumberSection.IsVisible = _selectedType == "number";

        if (_selectedType == "status" && _optionBoxes.Count == 0)
            foreach (var o in ConstructorRepository.TaskStatusOptions) AddOptionRow(o);
    }

    // Под-опции формата числа: блок опций (точность/разряды/валюта) скрыт для «Обычного числа»
    // (показ = сырому вводу), поле валюты — только для формата «Деньги».
    private void UpdateNumberOptionsVisibility()
    {
        var idx  = Math.Clamp(NumberFormatCombo.SelectedIndex, 0, NumberFormatKinds.Length - 1);
        var kind = NumberFormatKinds[idx];
        NumberOptions.IsVisible   = kind != NumberFormatKind.Plain;
        CurrencySection.IsVisible = kind == NumberFormatKind.Currency;
    }

    // Разложить формат числа по контролам диалога (обратное к BuildNumberConfigJson).
    private void ApplyNumberFormat(NumberFormat fmt)
    {
        NumberFormatCombo.SelectedIndex     = Math.Max(0, Array.IndexOf(NumberFormatKinds, fmt.Kind));
        DecimalsCombo.SelectedIndex         = fmt.Decimals < 0 ? 0 : Math.Clamp(fmt.Decimals + 1, 1, 5);
        ThousandsToggle.IsChecked           = fmt.Thousands;
        CurrencyBox.Text                    = fmt.CurrencySymbol;
        SymbolPosCombo.SelectedIndex        = fmt.SymbolSuffix ? 0 : 1;
        DecimalSeparatorCombo.SelectedIndex = fmt.DecimalSeparator == DecimalMark.Dot ? 1 : 0;
        UpdateNumberOptionsVisibility();
    }

    // Собрать формат числа из контролов диалога → ConfigJson поля.
    private string BuildNumberConfigJson()
    {
        var idx      = Math.Clamp(NumberFormatCombo.SelectedIndex, 0, NumberFormatKinds.Length - 1);
        var kind     = NumberFormatKinds[idx];
        var decimals = DecimalsCombo.SelectedIndex <= 0 ? -1 : DecimalsCombo.SelectedIndex - 1;   // «Авто» = -1
        var symbol   = CurrencyBox.Text?.Trim() is { Length: > 0 } s ? s : "₽";
        var mark     = DecimalSeparatorCombo.SelectedIndex == 1 ? DecimalMark.Dot : DecimalMark.Comma;
        var fmt = new NumberFormat(kind, decimals, ThousandsToggle.IsChecked == true, symbol,
            SymbolPosCombo.SelectedIndex != 1, mark);
        return JsonSerializer.Serialize(NumberFormatValue.ToConfigObject(fmt));
    }

    // Одна строка редактора пунктов: показ значения (с многоточием) + поле ввода + удаление.
    // Показ и ввод — ДВА контрола, видим всегда ровно один (переключаются), поэтому нет ни
    // «прыжка» размера, ни просвечивания текста. Возвращает TextBox (чтобы сфокусировать
    // вновь добавленный). Значения собираются в BuildOptionsConfigJson из этого же TextBox.
    private TextBox AddOptionRow(string value)
    {
        var primary   = ResourceBrush("BrushPrimaryText", Brushes.Black);
        var secondary = ResourceBrush("BrushSecondaryText", Brushes.Gray);

        // Поле ввода (авторитетное значение) — плоское, видно только при редактировании.
        var box = new TextBox
        {
            Text                     = value,
            Watermark                = "Название пункта",
            Height                   = 30,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsVisible                = false,
        };
        box.Classes.Add("optionRow");

        // Показ значения — TextBlock с многоточием в конце (TextBox сам «…» не умеет). Клик по нему
        // переключает строку в режим ввода. Шрифт привязан к полю → при переключении размер тот же.
        var display = new TextBlock
        {
            // Левый отступ = Padding поля optionRow («2,0») — чтобы при клике текст не сдвигался.
            Margin            = new Thickness(2, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Cursor            = new Cursor(StandardCursorType.Ibeam),
        };
        display.Bind(TextBlock.FontSizeProperty,   box.GetObservable(TextBox.FontSizeProperty));
        display.Bind(TextBlock.FontFamilyProperty, box.GetObservable(TextBox.FontFamilyProperty));

        // Показ: пустое значение — приглушённый плейсхолдер (как watermark поля).
        void ShowDisplay()
        {
            var v = box.Text ?? "";
            display.Text       = v.Length > 0 ? v : "Название пункта";
            display.Foreground = v.Length > 0 ? primary : secondary;
            display.IsVisible  = true;
            box.IsVisible      = false;
            if (_editingOption?.Box == box) _editingOption = null;
        }
        // Ввод: показать поле, курсор в конец. Фиксация — по уходу фокуса или клику мимо строки.
        void ShowEditor()
        {
            display.IsVisible = false;
            box.IsVisible     = true;
            box.Focus();
            box.CaretIndex    = (box.Text ?? "").Length;
            _editingOption    = (box, ShowDisplay);
        }
        display.PointerPressed += (_, _) => ShowEditor();
        box.LostFocus          += (_, _) => ShowDisplay();
        box.KeyDown            += (_, e) => { if (e.Key is Key.Enter or Key.Escape) ShowDisplay(); };

        // Панель фиксированной высоты строки (30) — чтобы список не «дёргался» при переключении
        // показ/ввод (скрытое поле в измерение не входит).
        var editArea = new Panel { Height = 30 };
        editArea.Children.Add(display);
        editArea.Children.Add(box);

        var del = new Button
        {
            Background        = Brushes.Transparent,
            BorderThickness   = new Thickness(0),
            Padding           = new Thickness(6),
            VerticalAlignment = VerticalAlignment.Center,
            Content           = BuildDeleteGlyph(),
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        del.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(editArea, 0);
        Grid.SetColumn(del, 1);
        grid.Children.Add(editArea);
        grid.Children.Add(del);

        del.Click += (_, _) =>
        {
            if (_editingOption?.Box == box) _editingOption = null;
            _optionBoxes.Remove(box);
            OptionsList.Children.Remove(grid);
        };

        box.Tag = grid;   // строка-контейнер для фильтра (box лежит внутри editArea, не прямо в grid)

        // Заполненная строка стартует в показе, новая (пустая) — сразу в вводе (её фокусирует вызвавший).
        if (value.Length > 0) ShowDisplay();
        else ShowEditor();

        _optionBoxes.Add(box);
        OptionsList.Children.Add(grid);
        return box;
    }

    // Фильтр строк-пунктов по полю поиска: строка видна, если её текст содержит запрос
    // (пустой запрос — видны все). Совпадение по текущему тексту строки.
    private void FilterOptionRows()
    {
        var q = OptionsSearch.Text?.Trim() ?? "";
        foreach (var box in _optionBoxes)
            if (box.Tag is Control row)
                row.IsVisible = q.Length == 0 || (box.Text ?? "").Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    // Клик мимо активного поля пункта — фиксирует ввод (прячет поле → фокус снимается, каретка
    // гаснет). Клик по самому полю (правка курсора) не трогаем.
    private void DismissOptionEditor(object? sender, PointerPressedEventArgs e)
    {
        if (_editingOption is not { } ed) return;
        if (IsWithin(e.Source, ed.Box)) return;
        ed.Commit();
    }

    // Является ли source самим target или его потомком в визуальном дереве.
    private static bool IsWithin(object? source, Visual target)
    {
        for (var v = source as Visual; v is not null; v = v.GetVisualParent())
            if (ReferenceEquals(v, target)) return true;
        return false;
    }

    private Control BuildDeleteGlyph()
    {
        // Крестик удаления строки — как остальные иконки приложения: Viewbox стандартного
        // размера (IconSizeSmall=16) + Canvas 24×24 + Path, чтобы сохранить «сетку 24» и
        // естественные поля глифа (иначе Stretch растягивал крестик на весь бокс — выглядел
        // крупным). Масштаб зума наследуется от LayoutTransform окна.
        var geo    = this.TryFindResource("IconClose", out var g) && g is Geometry geom ? geom : null;
        var size   = this.TryFindResource("IconSizeSmall", out var s) && s is double d ? d : 16.0;
        var thick  = this.TryFindResource("IconStrokeSmall", out var t) && t is double td ? td : 1.8;
        var stroke = this.TryFindResource("BrushSecondaryText", out var b) && b is IBrush br ? br : Brushes.Gray;

        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data            = geo,
            Stroke          = stroke,
            StrokeThickness = thick,   // стандарт: IconStrokeSmall (как иконки-оглавления колонок)
            StrokeLineCap   = PenLineCap.Round,
            StrokeJoin      = PenLineJoin.Round,
        });
        return new Viewbox { Width = size, Height = size, Child = canvas };
    }

    // Кисть из ресурсов окна с запасным значением (для контролов, создаваемых в коде).
    private IBrush ResourceBrush(string key, IBrush fallback) =>
        this.TryFindResource(key, out var r) && r is IBrush b ? b : fallback;

    // Разбор опций из ConfigJson поля (как MergeSelectOptions в репозитории): {"options":[...]}.
    private static IEnumerable<string> ParseOptions(string? configJson)
    {
        var result = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(configJson) ? "{}" : configJson);
            if (doc.RootElement.TryGetProperty("options", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var el in arr.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s)
                        result.Add(s);
        }
        catch (JsonException) { /* нечитаемый конфиг — пустой набор */ }
        return result;
    }

    // Сборка ConfigJson из редактора пунктов: непустые, обрезанные, без дублей (регистр игнор.).
    // Для «Мультисписка» дополнительно сохраняется формат склейки значений (separator).
    private string BuildOptionsConfigJson()
    {
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new List<string>();
        foreach (var b in _optionBoxes)
        {
            var v = b.Text?.Trim() ?? "";
            if (v.Length > 0 && seen.Add(v)) options.Add(v);
        }
        if (_selectedType == "multiselect")
        {
            var separator = SeparatorKeys[Math.Clamp(SeparatorCombo.SelectedIndex, 0, SeparatorKeys.Length - 1)];
            return JsonSerializer.Serialize(new { options, separator });
        }
        return JsonSerializer.Serialize(new { options });
    }

    /// <summary>
    /// Конструктор для системной колонки «Название»/«Описание». Визуально как
    /// «Изменение свойства», но тип зафиксирован «Текст» (ComboBox заблокирован),
    /// а сохранение возвращает подпись через ResultName, не трогая DbFields.
    /// </summary>
    public PropertyEditorDialog(string currentName)
    {
        InitializeComponent();

        _repository   = null!;   // в системном режиме не используется
        _collectionId = 0;
        _editFieldId  = null;
        _systemMode   = true;

        Title = "Изменение свойства";

        TypeCombo.ItemsSource   = FieldTypes;
        TypeCombo.SelectedIndex = Array.FindIndex(FieldTypes, t => t.Type == "text");
        TypeCombo.IsEnabled     = false;   // тип «Текст» нельзя изменить

        // Пояснение: «Название»/«Описание» — базовые свойства целостности, их нельзя
        // удалить и сменить тип. Показываем только в этом (системном) режиме.
        SystemNote.IsVisible = true;

        NameBox.Text = currentName;

        CancelBtn.Click += (_, _) => Close(false);
        SaveBtn.Click   += (_, _) => TrySave();
        NameBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) TrySave(); };
        NameBox.TextChanged += (_, _) => HideError();

        Opened += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void ShowError(string message)
    {
        var pos = Position;            // баннер увеличит высоту — держим позицию
        ErrorBanner.Show(message);
        KeepWindowPosition(pos);
    }

    private void HideError()
    {
        if (!ErrorBanner.IsVisible) return;
        var pos = Position;
        ErrorBanner.Hide();
        KeepWindowPosition(pos);
    }

    // Держим позицию окна при пересчёте высоты (SizeToContent), чтобы оно не «прыгало».
    private void KeepWindowPosition(PixelPoint pos)
    {
        void Restore(object? _, EventArgs __)
        {
            LayoutUpdated -= Restore;
            if (Position != pos) Position = pos;
        }
        LayoutUpdated += Restore;
    }

    private void TrySave()
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
        {
            ShowError("Укажите название свойства");
            NameBox.Focus();
            return;
        }

        // Системная колонка: возвращаем подпись наружу, запись делает контроллер
        // (метаданные), DbFields не трогаем.
        if (_systemMode)
        {
            ResultName = name;
            Close(true);
            return;
        }

        int savedFieldId;
        try
        {
            if (_editFieldId.HasValue)
            {
                var field = _repository.GetFields(_collectionId).FirstOrDefault(f => f.Id == _editFieldId);
                if (field is null) { Close(true); return; }

                field.Name = name;
                // Системные поля имеют фиксированный тип (markdown/title), их тип не меняем.
                if (!field.IsSystem) field.FieldType = _selectedType;
                // Пункты «Списка»/«Статуса» и формат «Числа» — в ConfigJson (иные типы не трогают).
                if (FieldTypeInfo.IsOptionList(_selectedType)) field.ConfigJson = BuildOptionsConfigJson();
                else if (_selectedType == "number")           field.ConfigJson = BuildNumberConfigJson();
                _repository.UpdateField(field);
                savedFieldId = _editFieldId.Value;
            }
            else
            {
                var fields = _repository.GetFields(_collectionId);
                var maxPos = fields.Count > 0 ? fields.Max(f => f.Position) : 0;

                var added = _repository.AddField(new DbField
                {
                    CollectionId = _collectionId,
                    Name         = name,
                    FieldType    = _selectedType,
                    Position     = maxPos + 1000,
                    ConfigJson   = FieldTypeInfo.IsOptionList(_selectedType) ? BuildOptionsConfigJson()
                                 : _selectedType == "number"                 ? BuildNumberConfigJson()
                                 : "{}",
                });
                savedFieldId = added.Id!.Value;
            }
        }
        catch (InvalidOperationException ex)
        {
            // Дубль имени поля и прочие нарушения — показываем в баннере, не роняя приложение.
            ShowError(ex.Message);
            NameBox.Focus();
            return;
        }

        // Для типа «Дата» применяем переключатель «Отображать в календаре».
        if (_selectedType == "date")
            _repository.SetDateFieldVisible(savedFieldId, CalDateToggle.IsChecked == true);

        // Для типа «Пользователь» — «Добавить в раздел задач» (может авто-создать
        // «Назначил»/«Статус»; нет права на создание полей — покажем причину баннером).
        if (_selectedType == "user")
        {
            try { _repository.SetUserFieldTaskExec(savedFieldId, TaskToggle.IsChecked == true); }
            catch (InvalidOperationException ex)
            {
                ShowError(ex.Message);
                return;
            }
        }

        Close(true);
    }
}
