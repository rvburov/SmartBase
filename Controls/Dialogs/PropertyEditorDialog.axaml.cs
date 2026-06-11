using Avalonia;
using Avalonia.Controls;
using MyPlanner.Core.Models;
using MyPlanner.Database.Repositories;

namespace MyPlanner.Controls.Dialogs;

/// <summary>
/// Notion-style мини-диалог для создания/изменения одного свойства (поля коллекции).
/// Только два параметра: название и тип. Markdown как тип скрыт (это системное body-поле).
/// </summary>
public partial class PropertyEditorDialog : Window
{
    private static readonly (string Type, string Icon, string Label)[] FieldTypes =
    [
        ("text",     "Аа", "Текст"),
        ("number",   "#",  "Число"),
        ("date",     "⊙",  "Дата"),
        ("select",   "≡",  "Список"),
        ("checkbox", "✓",  "Флаг"),
        ("url",      "↗",  "Ссылка"),
    ];

    private readonly ConstructorRepository _repository;
    private readonly int  _collectionId;
    private readonly int? _editFieldId;

    private string _selectedType = "text";

    public PropertyEditorDialog(ConstructorRepository repository, int collectionId, int? editFieldId = null)
    {
        InitializeComponent();

        _repository   = repository;
        _collectionId = collectionId;
        _editFieldId  = editFieldId;

        TypeCombo.ItemsSource = FieldTypes.Select(t => $"{t.Icon}  {t.Label}").ToList();

        if (editFieldId.HasValue)
        {
            Title = "Изменение свойства";
            var field = repository.GetFields(collectionId).FirstOrDefault(f => f.Id == editFieldId);
            if (field is not null)
            {
                NameBox.Text  = field.Name;
                _selectedType = field.FieldType;
            }
        }

        var idx = Array.FindIndex(FieldTypes, t => t.Type == _selectedType);
        TypeCombo.SelectedIndex = idx >= 0 ? idx : 0;
        TypeCombo.SelectionChanged += (_, _) =>
        {
            var i = TypeCombo.SelectedIndex;
            if (i >= 0 && i < FieldTypes.Length) _selectedType = FieldTypes[i].Type;
        };

        CancelBtn.Click += (_, _) => Close(false);
        SaveBtn.Click   += (_, _) => TrySave();
        NameBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) TrySave(); };
        NameBox.TextChanged += (_, _) => HideError();
    }

    private void ShowError(string message)
    {
        var pos = Position;            // баннер увеличит высоту — держим позицию
        ErrorText.Text        = message;
        ErrorBanner.IsVisible = true;
        KeepWindowPosition(pos);
    }

    private void HideError()
    {
        if (!ErrorBanner.IsVisible) return;
        var pos = Position;
        ErrorBanner.IsVisible = false;
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

        if (_editFieldId.HasValue)
        {
            var field = _repository.GetFields(_collectionId).FirstOrDefault(f => f.Id == _editFieldId);
            if (field is not null)
            {
                field.Name      = name;
                // Системные поля имеют фиксированный тип (markdown/title), их тип не меняем.
                if (!field.IsSystem) field.FieldType = _selectedType;
                _repository.UpdateField(field);
            }
        }
        else
        {
            var fields = _repository.GetFields(_collectionId);
            var maxPos = fields.Count > 0 ? fields.Max(f => f.Position) : 0;

            _repository.AddField(new DbField
            {
                CollectionId = _collectionId,
                Name         = name,
                FieldType    = _selectedType,
                Position     = maxPos + 1000,
            });
        }

        Close(true);
    }
}
