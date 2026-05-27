using Avalonia.Controls;
using Avalonia.Data;
using MyPlanner.Core.Models;
using MyPlanner.Database.Repositories;

namespace MyPlanner.Views.ConstructorDb;

public partial class TableView : UserControl
{
    public event EventHandler<int>? RecordClicked;

    private ConstructorRepository? _repository;
    private int _collectionId;
    private List<DbField> _fields = [];

    public TableView()
    {
        InitializeComponent();
        RecordsGrid.SelectionChanged += OnSelectionChanged;
    }

    public void Load(ConstructorRepository repository, int collectionId)
    {
        _repository   = repository;
        _collectionId = collectionId;
        Reload();
    }

    public void SetColResizeMode(bool manual)
    {
        if (!manual) ResetColumnWidths();
    }

    public void SetRowResizeMode(bool _) { }

    private void Reload()
    {
        if (_repository is null) return;

        _fields = _repository.GetFields(_collectionId);
        var records = _repository.GetRecords(_collectionId);

        RecordsGrid.Columns.Clear();

        RecordsGrid.Columns.Add(new DataGridTextColumn
        {
            Header     = "№",
            Binding    = new Binding("RowNumber"),
            IsReadOnly = true,
            Width      = new DataGridLength(50),
        });

        foreach (var field in _fields)
        {
            RecordsGrid.Columns.Add(new DataGridTextColumn
            {
                Header     = field.Name,
                Binding    = new Binding($"[{field.Id}]"),
                IsReadOnly = true,
                Width      = new DataGridLength(1, DataGridLengthUnitType.Star),
            });
        }

        var rows = new List<TableRowModel>();
        for (var i = 0; i < records.Count; i++)
        {
            var id     = records[i].Id ?? 0;
            var values = _repository.GetFieldValues(id);
            rows.Add(new TableRowModel(id, i + 1, values));
        }

        RecordsGrid.ItemsSource = rows;
    }

    private void ResetColumnWidths()
    {
        foreach (var col in RecordsGrid.Columns.Skip(1))
            col.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (RecordsGrid.SelectedItem is TableRowModel row)
        {
            RecordsGrid.SelectedItem = null;
            RecordClicked?.Invoke(this, row.RecordId);
        }
    }
}

public class TableRowModel(int recordId, int rowNumber, Dictionary<int, string> values)
{
    public int RecordId  { get; } = recordId;
    public int RowNumber { get; } = rowNumber;

    public string this[string key] =>
        int.TryParse(key, out var id) && values.TryGetValue(id, out var v) ? v : "";
}
