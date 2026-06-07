using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MyPlanner.Core.Models;
using MyPlanner.Database.Repositories;

namespace MyPlanner.Controls.Dialogs;

/// <summary>
/// Notion-style мини-диалог для создания/изменения одного свойства (поля коллекции).
/// Только два параметра: имя и тип. Markdown как тип скрыт (это системный body-поле).
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
    private readonly List<Button> _typeButtons = [];

    public PropertyEditorDialog(ConstructorRepository repository, int collectionId, int? editFieldId = null)
    {
        InitializeComponent();

        _repository   = repository;
        _collectionId = collectionId;
        _editFieldId  = editFieldId;

        BuildTypeButtons();

        if (editFieldId.HasValue)
        {
            Title             = "Изменить свойство";
            TitleLabel.Text   = "Изменить свойство";
            var field = repository.GetFields(collectionId).FirstOrDefault(f => f.Id == editFieldId);
            if (field is not null)
            {
                NameBox.Text  = field.Name;
                _selectedType = field.FieldType;
            }
        }

        UpdateTypeButtonStyles();

        CancelBtn.Click += (_, _) => Close(false);
        SaveBtn.Click   += (_, _) => TrySave();
        NameBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) TrySave(); };
    }

    private void BuildTypeButtons()
    {
        foreach (var (type, icon, label) in FieldTypes)
        {
            var btn = new Button
            {
                Content = $"{icon}  {label}",
                Height  = 34,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin  = new Avalonia.Thickness(2),
            };
            btn.Click += (_, _) => { _selectedType = type; UpdateTypeButtonStyles(); };
            _typeButtons.Add(btn);
            TypeButtonsGrid.Children.Add(btn);
        }
    }

    private void UpdateTypeButtonStyles()
    {
        for (var i = 0; i < FieldTypes.Length; i++)
        {
            var (type, _, _) = FieldTypes[i];
            var btn = _typeButtons[i];
            if (type == _selectedType)
            {
                btn.Background      = new SolidColorBrush(Color.Parse("#E3F0FF"));
                btn.Foreground      = new SolidColorBrush(Color.Parse("#007AFF"));
                btn.BorderBrush     = new SolidColorBrush(Color.Parse("#007AFF"));
                btn.BorderThickness = new Avalonia.Thickness(1.5);
            }
            else
            {
                btn.Background      = new SolidColorBrush(Color.Parse("#FFFFFF"));
                btn.Foreground      = new SolidColorBrush(Color.Parse("#1C1C1E"));
                btn.BorderBrush     = new SolidColorBrush(Color.Parse("#E5E5EA"));
                btn.BorderThickness = new Avalonia.Thickness(1.5);
            }
        }
    }

    private void TrySave()
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name)) { NameBox.Focus(); return; }

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
