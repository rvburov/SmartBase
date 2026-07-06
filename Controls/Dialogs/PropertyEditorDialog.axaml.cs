using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DataBase.Core.Models;
using DataBase.Database.Repositories;

namespace DataBase.Controls.Dialogs;

/// <summary>Тип поля для ComboBox: обводочная иконка (lucide) + подпись.</summary>
public sealed class FieldTypeItem
{
    public required string   Type  { get; init; }
    public required string   Label { get; init; }
    public required Geometry Icon  { get; init; }
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
        new() { Type = "checkbox", Label = "Чекбокс", Icon = Components.FieldTypeIcons.ForType("checkbox")! },
        new() { Type = "url",      Label = "Ссылка",  Icon = Components.FieldTypeIcons.ForType("url")!      },
        new() { Type = "user",     Label = "Пользователь", Icon = Components.FieldTypeIcons.ForType("user")! },
    ];

    private readonly ConstructorRepository _repository;
    private readonly int  _collectionId;
    private readonly int? _editFieldId;

    // Режим системной колонки («Название»/«Описание»): тип зафиксирован «Текст»,
    // запись идёт не в DbFields, а через результат диалога (см. ResultName).
    private readonly bool _systemMode;

    private string _selectedType = "text";

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

        if (editFieldId.HasValue)
        {
            Title = "Изменение свойства";
            var field = repository.GetFields(collectionId).FirstOrDefault(f => f.Id == editFieldId);
            if (field is not null)
            {
                NameBox.Text  = field.Name;
                _selectedType = field.FieldType;
                CalDateToggle.IsChecked = field.IsCalStart;
            }
        }

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

    // Переключатель «Отображать в календаре» виден только для типа «Дата».
    private void UpdateCalDateVisibility() => CalDateSection.IsVisible = _selectedType == "date";

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

        Close(true);
    }
}
