using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using MyPlanner.Core.Models;
using MyPlanner.Database.Repositories;

namespace MyPlanner.Controls.Dialogs;

public partial class RecordDialog : Window
{
    private readonly ConstructorRepository _repository;
    private readonly int _collectionId;
    private int? _recordId;

    // field.Id → (field, input control)
    private readonly Dictionary<int, (DbField Field, Control Input)> _fieldWidgets = [];

    public RecordDialog(ConstructorRepository repository, int collectionId, int? recordId = null)
    {
        InitializeComponent();

        _repository   = repository;
        _collectionId = collectionId;
        _recordId     = recordId;

        if (recordId.HasValue)
        {
            Title = "Изменить запись";
            DeleteBtn.IsVisible = true;
        }

        BuildFields();
        if (recordId.HasValue) FillValues(recordId.Value);

        CancelBtn.Click += (_, _) => Close(null);
        DeleteBtn.Click += (_, _) => { _repository.DeleteRecord(_recordId!.Value); Close(false); };
        SaveBtn.Click   += (_, _) => TrySave();
    }

    private void BuildFields()
    {
        var fields = _repository.GetFields(_collectionId);
        foreach (var field in fields)
        {
            var label = new TextBlock
            {
                Text       = field.Name,
                FontWeight = Avalonia.Media.FontWeight.Bold,
                FontSize   = 13,
                Foreground = Avalonia.Media.Brushes.Gray,
            };
            FieldsPanel.Children.Add(label);

            var input = MakeInput(field);
            _fieldWidgets[field.Id!.Value] = (field, input);
            FieldsPanel.Children.Add(input);
        }
    }

    private static Control MakeInput(DbField field)
    {
        Dictionary<string, object> config = [];
        try { config = JsonSerializer.Deserialize<Dictionary<string, object>>(field.ConfigJson ?? "{}") ?? []; }
        catch { /* ignore bad json */ }

        return field.FieldType switch
        {
            "number" => new NumericUpDown { Minimum = decimal.MinValue, Maximum = decimal.MaxValue, Height = 36 },
            "date"   => new CalendarDatePicker { SelectedDate = DateTime.Today, Height = 36, HorizontalAlignment = HorizontalAlignment.Stretch },
            "select" => BuildSelectCombo(config),
            "checkbox" => new CheckBox { VerticalAlignment = VerticalAlignment.Center },
            "url"    => new TextBox { Watermark = "https://", Height = 36 },
            _        => new TextBox { Height = 36 },  // text, default
        };
    }

    private static ComboBox BuildSelectCombo(Dictionary<string, object> config)
    {
        var opts = new List<string>();
        if (config.TryGetValue("options", out var raw) && raw is JsonElement elem && elem.ValueKind == JsonValueKind.Array)
            opts = elem.EnumerateArray().Select(o => o.ToString()).ToList();

        return new ComboBox
        {
            ItemsSource    = opts.Count > 0 ? opts : ["—"],
            SelectedIndex  = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height         = 36,
        };
    }

    private void FillValues(int recordId)
    {
        var values = _repository.GetFieldValues(recordId);
        foreach (var (fieldId, (field, input)) in _fieldWidgets)
        {
            var val = values.GetValueOrDefault(fieldId, "");
            switch (field.FieldType)
            {
                case "checkbox":
                    ((CheckBox)input).IsChecked = val is "1" or "true" or "True";
                    break;
                case "date":
                    if (DateTime.TryParseExact(val, "dd.MM.yyyy", null, System.Globalization.DateTimeStyles.None, out var dt))
                        ((CalendarDatePicker)input).SelectedDate = dt;
                    break;
                case "select":
                    var cb = (ComboBox)input;
                    var idx = (cb.ItemsSource as List<string>)?.IndexOf(val) ?? -1;
                    if (idx >= 0) cb.SelectedIndex = idx;
                    break;
                case "number":
                    if (decimal.TryParse(val, out var num))
                        ((NumericUpDown)input).Value = num;
                    break;
                default:
                    ((TextBox)input).Text = val;
                    break;
            }
        }
    }

    private string GetValue(DbField field, Control input) => field.FieldType switch
    {
        "checkbox" => ((CheckBox)input).IsChecked == true ? "1" : "0",
        "date"     => ((CalendarDatePicker)input).SelectedDate?.ToString("dd.MM.yyyy") ?? "",
        "select"   => ((ComboBox)input).SelectedItem?.ToString() ?? "",
        "number"   => ((NumericUpDown)input).Value?.ToString() ?? "",
        _          => ((TextBox)input).Text ?? "",
    };

    private void TrySave()
    {
        if (_recordId is null)
        {
            var record = _repository.AddRecord(new DbRecord { CollectionId = _collectionId });
            _recordId = record.Id;
        }

        foreach (var (fieldId, (field, input)) in _fieldWidgets)
            _repository.SetFieldValue(_recordId!.Value, fieldId, GetValue(field, input));

        Close(true);
    }
}
