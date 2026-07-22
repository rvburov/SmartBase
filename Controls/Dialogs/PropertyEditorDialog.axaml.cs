using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
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
        new() { Type = "checkbox", Label = "Чекбокс", Icon = Components.FieldTypeIcons.ForType("checkbox")! },
        new() { Type = "url",      Label = "Ссылка",  Icon = Components.FieldTypeIcons.ForType("url")!      },
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
            }
        }

        AddOptionBtn.Click += (_, _) => AddOptionRow("").Focus();

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

        if (_selectedType == "status" && _optionBoxes.Count == 0)
            foreach (var o in ConstructorRepository.TaskStatusOptions) AddOptionRow(o);
    }

    // Одна строка редактора пунктов: поле ввода + кнопка удаления. Возвращает TextBox
    // (чтобы сфокусировать вновь добавленный). Значения собираются в BuildOptionsConfigJson.
    private TextBox AddOptionRow(string value)
    {
        var box = new TextBox
        {
            Text                     = value,
            Watermark                = "Название пункта",
            Height                   = 34,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        if (this.TryFindResource("RadiusMedium", out var r) && r is CornerRadius cr) box.CornerRadius = cr;

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
        Grid.SetColumn(box, 0);
        Grid.SetColumn(del, 1);
        grid.Children.Add(box);
        grid.Children.Add(del);

        del.Click += (_, _) =>
        {
            _optionBoxes.Remove(box);
            OptionsList.Children.Remove(grid);
        };

        _optionBoxes.Add(box);
        OptionsList.Children.Add(grid);
        return box;
    }

    private Control BuildDeleteGlyph()
    {
        var geo = this.TryFindResource("IconClose", out var g) && g is Geometry geom ? geom : null;
        return new Avalonia.Controls.Shapes.Path
        {
            Data            = geo,
            Stroke          = this.TryFindResource("BrushSecondaryText", out var b) && b is IBrush br ? br : Brushes.Gray,
            StrokeThickness = 1.6,
            Stretch         = Stretch.Uniform,
            Width           = 14,
            Height          = 14,
        };
    }

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
    private string BuildOptionsConfigJson()
    {
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new List<string>();
        foreach (var b in _optionBoxes)
        {
            var v = b.Text?.Trim() ?? "";
            if (v.Length > 0 && seen.Add(v)) options.Add(v);
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
                // Пункты «Списка»/«Статуса» — в ConfigJson (иные типы конфиг не трогают).
                if (FieldTypeInfo.IsOptionList(_selectedType)) field.ConfigJson = BuildOptionsConfigJson();
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
                    ConfigJson   = FieldTypeInfo.IsOptionList(_selectedType) ? BuildOptionsConfigJson() : "{}",
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
