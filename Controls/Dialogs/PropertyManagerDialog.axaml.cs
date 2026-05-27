using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using MyPlanner.Core.Models;
using MyPlanner.Database.Repositories;
using System.Windows.Input;

namespace MyPlanner.Controls.Dialogs;

public partial class PropertyManagerDialog : Window
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
    private readonly int _collectionId;
    private string _selectedType = "text";
    private readonly List<Button> _typeButtons = [];

    public PropertyManagerDialog(ConstructorRepository repository, int collectionId)
    {
        InitializeComponent();

        _repository   = repository;
        _collectionId = collectionId;

        var col = repository.GetAllCollections().FirstOrDefault(c => c.Id == collectionId);
        CollectionTitle.Text = col?.Name ?? collectionId.ToString();
        Title = $"Свойства — {CollectionTitle.Text}";

        BuildTypeButtons();
        LoadFields();
        UpdateTypeButtonStyles();

        CloseXBtn.Click    += (_, _) => Close(true);
        CloseBtn.Click     += (_, _) => Close(true);
        AddFieldBtn.Click  += (_, _) => AddField();
        NewFieldNameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) AddField();
        };
    }

    private void BuildTypeButtons()
    {
        foreach (var (type, icon, label) in FieldTypes)
        {
            var btn = new Button
            {
                Content            = $"{icon}  {label}",
                Height             = 34,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin             = new Avalonia.Thickness(2),
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
                btn.Background = new SolidColorBrush(Color.Parse("#E3F0FF"));
                btn.Foreground = new SolidColorBrush(Color.Parse("#007AFF"));
                btn.BorderBrush = new SolidColorBrush(Color.Parse("#007AFF"));
                btn.BorderThickness = new Avalonia.Thickness(1.5);
            }
            else
            {
                btn.Background = new SolidColorBrush(Color.Parse("#FFFFFF"));
                btn.Foreground = new SolidColorBrush(Color.Parse("#1C1C1E"));
                btn.BorderBrush = new SolidColorBrush(Color.Parse("#E5E5EA"));
                btn.BorderThickness = new Avalonia.Thickness(1.5);
            }
        }
    }

    private void LoadFields()
    {
        var fields = _repository.GetFields(_collectionId);
        FieldsList.ItemsSource = fields.Select(f => new FieldRow(f, () =>
        {
            _repository.DeleteField(f.Id!.Value);
            LoadFields();
        })).ToList();
    }

    private void AddField()
    {
        var name = NewFieldNameBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name)) { NewFieldNameBox.Focus(); return; }

        var fields  = _repository.GetFields(_collectionId);
        var maxPos  = fields.Count > 0 ? fields.Max(f => f.Position) : 0;

        _repository.AddField(new DbField
        {
            CollectionId = _collectionId,
            Name         = name,
            FieldType    = _selectedType,
            Position     = maxPos + 1000,
        });
        NewFieldNameBox.Text = "";
        LoadFields();
    }
}

public class FieldRow
{
    public string   Name       { get; }
    public string   TypeIcon   { get; }
    public string   TypeLabel  { get; }
    public bool     IsRequired { get; }
    public ICommand DeleteCommand { get; }

    private static readonly (string Type, string Icon, string Label)[] FieldTypes =
    [
        ("text",     "Аа", "Текст"),
        ("number",   "#",  "Число"),
        ("date",     "⊙",  "Дата"),
        ("select",   "≡",  "Список"),
        ("checkbox", "✓",  "Флаг"),
        ("url",      "↗",  "Ссылка"),
    ];

    public FieldRow(DbField field, Action onDelete)
    {
        Name       = field.Name;
        IsRequired = field.IsRequired;
        var info   = FieldTypes.FirstOrDefault(t => t.Type == field.FieldType);
        TypeIcon   = info.Icon  ?? "Аа";
        TypeLabel  = info.Label ?? "Текст";
        DeleteCommand = new RelayCommand(onDelete);
    }
}
