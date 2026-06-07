using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MyPlanner.Core.Models;
using MyPlanner.Database.Repositories;
using MyPlanner.ViewModels.ConstructorDb;

namespace MyPlanner.Views.ConstructorDb;

/// <summary>
/// Правая панель «Страница записи» (Obsidian-стиль): свойства сверху + markdown снизу.
/// Сохранение значений идёт сразу в БД при изменении (как в TableView).
/// </summary>
public partial class RecordPageView : UserControl
{
    public event EventHandler?      CloseRequested;
    public event EventHandler<int>? RecordChanged; // recordId — известили родителя, что данные изменились

    private RecordPageViewModel? _vm;

    public RecordPageView()
    {
        InitializeComponent();

        CloseBtn.Click       += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        PreviewToggle.IsCheckedChanged += (_, _) => UpdatePreviewVisibility();
    }

    public void Show(ConstructorRepository repository, int recordId, int collectionId)
    {
        _vm = new RecordPageViewModel(repository, recordId, collectionId);
        DataContext = _vm;

        TitleBox.Text        = _vm.Title;
        MarkdownEditor.Text  = _vm.MarkdownText;
        PreviewToggle.IsChecked = false;
        UpdatePreviewVisibility();

        BuildProperties();
        WireAutoSave();
    }

    public void Clear()
    {
        _vm = null;
        DataContext = null;
        TitleBox.Text        = "";
        MarkdownEditor.Text  = "";
        MarkdownPreview.Markdown = "";
        PropertiesList.ItemsSource = null;
    }

    private void UpdatePreviewVisibility()
    {
        var preview = PreviewToggle.IsChecked == true;
        MarkdownPreviewBox.IsVisible = preview;
        MarkdownEditor.IsVisible     = !preview;

        if (preview)
            MarkdownPreview.Markdown = MarkdownEditor.Text ?? "";
    }

    private void WireAutoSave()
    {
        if (_vm is null) return;

        // Имя — пишем в БД при потере фокуса (как Notion: save on blur).
        TitleBox.LostFocus += (_, _) =>
        {
            if (_vm is null) return;
            _vm.Title = TitleBox.Text ?? "";
            _vm.SaveTitle();
            RecordChanged?.Invoke(this, _vm.RecordId);
        };

        // Описание — также при потере фокуса (избегаем шторма записей при печати).
        MarkdownEditor.LostFocus += (_, _) =>
        {
            if (_vm is null) return;
            _vm.MarkdownText = MarkdownEditor.Text ?? "";
            _vm.SaveMarkdown();
            RecordChanged?.Invoke(this, _vm.RecordId);
        };
    }

    private void BuildProperties()
    {
        if (_vm is null) { PropertiesList.ItemsSource = null; return; }

        PropertiesList.ItemTemplate = new FuncDataTemplate<PropertyRowVm>((row, _) => BuildPropertyRow(row));
        PropertiesList.ItemsSource  = _vm.Properties;
    }

    private Control BuildPropertyRow(PropertyRowVm row)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("140,*"),
            Margin = new Thickness(0, 0, 0, 6),
        };

        var nameLabel = new TextBlock
        {
            Text                = row.Field.Name,
            VerticalAlignment   = VerticalAlignment.Center,
            Foreground          = (IBrush)this.FindResource("BrushSecondaryText")!,
            FontSize            = 13,
        };
        Grid.SetColumn(nameLabel, 0);

        var input = MakeInputForField(row);
        Grid.SetColumn(input, 1);

        grid.Children.Add(nameLabel);
        grid.Children.Add(input);
        return grid;
    }

    private Control MakeInputForField(PropertyRowVm row)
    {
        var f = row.Field;
        Control input;

        switch (f.FieldType)
        {
            case "number":
            {
                var tb = new TextBox { Text = row.Value, BorderThickness = new Thickness(1), Height = 32 };
                tb.LostFocus += (_, _) => { row.Value = tb.Text ?? ""; row.Persist(); };
                input = tb;
                break;
            }
            case "date":
            {
                var picker = new CalendarDatePicker { Height = 32, HorizontalAlignment = HorizontalAlignment.Stretch };
                if (DateTime.TryParseExact(row.Value, "dd.MM.yyyy", null, DateTimeStyles.None, out var dt))
                    picker.SelectedDate = dt;
                picker.SelectedDateChanged += (_, _) =>
                {
                    row.Value = picker.SelectedDate?.ToString("dd.MM.yyyy") ?? "";
                    row.Persist();
                };
                input = picker;
                break;
            }
            case "select":
            {
                var combo = new ComboBox
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Height              = 32,
                    ItemsSource         = SelectOptions(f),
                    SelectedItem        = row.Value,
                };
                combo.SelectionChanged += (_, _) =>
                {
                    row.Value = combo.SelectedItem?.ToString() ?? "";
                    row.Persist();
                };
                input = combo;
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
                    row.Persist();
                };
                input = cb;
                break;
            }
            case "url":
            {
                var tb = new TextBox { Text = row.Value, Watermark = "https://", Height = 32 };
                tb.LostFocus += (_, _) => { row.Value = tb.Text ?? ""; row.Persist(); };
                input = tb;
                break;
            }
            default:  // text
            {
                var tb = new TextBox { Text = row.Value, Height = 32 };
                tb.LostFocus += (_, _) => { row.Value = tb.Text ?? ""; row.Persist(); };
                input = tb;
                break;
            }
        }

        return input;
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
