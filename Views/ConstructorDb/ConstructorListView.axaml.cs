using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using MyPlanner.Database.Repositories;
using System.Windows.Input;

namespace MyPlanner.Views.ConstructorDb;

public partial class ConstructorListView : UserControl
{
    public event EventHandler<int>? RecordClicked;

    private ConstructorRepository? _repository;
    private int _collectionId;

    public ConstructorListView() { InitializeComponent(); }

    public void Load(ConstructorRepository repository, int collectionId)
    {
        _repository   = repository;
        _collectionId = collectionId;
        Reload();
    }

    /// <summary>Очистить список (например, после удаления открытой коллекции).</summary>
    public void Clear()
    {
        _repository = null;
        _collectionId = 0;
        RecordsList.ItemsSource = null;
    }

    private void Reload()
    {
        if (_repository is null) return;

        var fields  = _repository.GetFields(_collectionId);
        var records = _repository.GetRecords(_collectionId);

        var nameField   = fields.FirstOrDefault();
        var statusField = fields.FirstOrDefault(f => f.FieldType == "select");
        var dateField   = fields.FirstOrDefault(f => f.FieldType == "date");

        var items = new List<ListRecordItem>();

        foreach (var record in records)
        {
            var rid    = record.Id ?? 0;
            var values = _repository.GetFieldValues(rid);
            items.Add(new ListRecordItem(
                recordId: rid,
                name:     nameField   is not null && values.TryGetValue(nameField.Id   ?? 0, out var n) ? n : "—",
                status:   statusField is not null && values.TryGetValue(statusField.Id ?? 0, out var s) ? s : "",
                date:     dateField   is not null && values.TryGetValue(dateField.Id   ?? 0, out var d) ? d : "",
                onClick:  id => RecordClicked?.Invoke(this, id)
            ));
        }

        items.Add(new ListRecordItem(recordId: null, name: "+ Запись", status: "", date: "", onClick: _ => { }));

        RecordsList.ItemsSource = items;
    }
}

public class ListRecordItem
{
    public int?     RecordId     { get; }
    public string   Name         { get; }
    public string   Status       { get; }
    public string   Date         { get; }
    public bool     HasStatus    { get; }
    public bool     HasDate      { get; }
    public ICommand ClickCommand { get; }

    public ListRecordItem(int? recordId, string name, string status, string date, Action<int> onClick)
    {
        RecordId     = recordId;
        Name         = name;
        Status       = status;
        Date         = date;
        HasStatus    = !string.IsNullOrEmpty(status);
        HasDate      = !string.IsNullOrEmpty(date);
        ClickCommand = new RelayCommand(() => { if (recordId.HasValue) onClick(recordId.Value); });
    }
}
