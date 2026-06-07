using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using MyPlanner.Database.Repositories;
using System.Windows.Input;

namespace MyPlanner.Views.ConstructorDb;

public partial class ConstructorListView : UserControl
{
    public event EventHandler<int>? RecordClicked;
    public event EventHandler<int>? RecordDeleteRequested;
    public event EventHandler?      RecordCreateRequested;

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

        // Заголовок берём из колонки записи Title; статус — из первого select-поля,
        // дата — из первого date-поля схемы коллекции.
        var statusField = fields.FirstOrDefault(f => f.FieldType == "select");
        var dateField   = fields.FirstOrDefault(f => f.FieldType == "date");

        var items = new List<ListRecordItem>();

        foreach (var record in records)
        {
            var rid    = record.Id ?? 0;
            var values = _repository.GetFieldValues(rid);
            items.Add(new ListRecordItem(
                recordId: rid,
                name:     record.DisplayTitle,
                status:   statusField is not null && values.TryGetValue(statusField.Id ?? 0, out var s) ? s : "",
                date:     dateField   is not null && values.TryGetValue(dateField.Id   ?? 0, out var d) ? d : "",
                onClick:  id => RecordClicked?.Invoke(this, id),
                onDelete: id => RecordDeleteRequested?.Invoke(this, id),
                onCreate: () => RecordCreateRequested?.Invoke(this, EventArgs.Empty)
            ));
        }

        RecordsList.ItemsSource = items;
    }
}

public class ListRecordItem
{
    public int?     RecordId      { get; }
    public string   Name          { get; }
    public string   Status        { get; }
    public string   Date          { get; }
    public bool     HasStatus     { get; }
    public bool     HasDate       { get; }
    public ICommand ClickCommand  { get; }
    public ICommand OpenCommand   { get; }
    public ICommand CreateCommand { get; }
    public ICommand DeleteCommand { get; }

    public ListRecordItem(int? recordId, string name, string status, string date,
                          Action<int> onClick, Action<int> onDelete, Action onCreate)
    {
        RecordId     = recordId;
        Name         = name;
        Status       = status;
        Date         = date;
        HasStatus    = !string.IsNullOrEmpty(status);
        HasDate      = !string.IsNullOrEmpty(date);

        ClickCommand  = new RelayCommand(() => { if (recordId.HasValue) onClick(recordId.Value); });
        OpenCommand   = new RelayCommand(() => { if (recordId.HasValue) onClick(recordId.Value); });
        DeleteCommand = new RelayCommand(() => { if (recordId.HasValue) onDelete(recordId.Value); });
        CreateCommand = new RelayCommand(onCreate);
    }
}
