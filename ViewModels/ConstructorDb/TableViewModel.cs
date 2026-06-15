using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataBase.Core.Models;
using DataBase.Database.Repositories;

namespace DataBase.ViewModels.ConstructorDb;

public partial class TableViewModel : ViewModelBase
{
    private readonly ConstructorRepository _constructor;

    [ObservableProperty] private ObservableCollection<DbField>  _fields  = [];
    [ObservableProperty] private ObservableCollection<DbRecord> _records = [];
    [ObservableProperty] private int? _collectionId;

    public TableViewModel(ConstructorRepository constructor)
    {
        _constructor = constructor;
    }

    public void LoadCollection(int collectionId)
    {
        CollectionId = collectionId;
        Fields  = new(_constructor.GetFields(collectionId));
        Records = new(_constructor.GetRecords(collectionId));
    }

    public Dictionary<int, string> GetFieldValues(int recordId) =>
        _constructor.GetFieldValues(recordId);

    [RelayCommand]
    private void SetValue((int recordId, int fieldId, string value) args) =>
        _constructor.SetFieldValue(args.recordId, args.fieldId, args.value);

    [RelayCommand]
    private void AddRecord()
    {
        if (CollectionId is null) return;
        var maxPos = Records.Count > 0 ? Records.Max(r => r.Position) : 0;
        var rec = _constructor.AddRecord(new DbRecord
        {
            CollectionId = CollectionId.Value,
            Position     = maxPos + 1000,
        });
        Records.Add(rec);
    }

    [RelayCommand]
    private void DeleteRecord(int recordId)
    {
        _constructor.DeleteRecord(recordId);
        var r = Records.FirstOrDefault(x => x.Id == recordId);
        if (r is not null) Records.Remove(r);
    }
}
