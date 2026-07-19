using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.ViewModels.ConstructorDb;

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

    // Ошибки записи в многопользовательском режиме (файл занят/пропал) гасятся тостом
    // (Utils/DbErrors) — команда просто не применяется, UI не падает.

    [RelayCommand]
    private void SetValue((int recordId, int fieldId, string value) args)
    {
        try { _constructor.SetFieldValue(args.recordId, args.fieldId, args.value); }
        catch (Exception ex) when (Utils.DbErrors.Handle(ex, _constructor.DbPath)) { }
    }

    [RelayCommand]
    private void AddRecord()
    {
        if (CollectionId is null) return;
        try
        {
            var maxPos = Records.Count > 0 ? Records.Max(r => r.Position) : 0;
            var rec = _constructor.AddRecord(new DbRecord
            {
                CollectionId = CollectionId.Value,
                Position     = maxPos + 1000,
            });
            Records.Add(rec);
        }
        catch (Exception ex) when (Utils.DbErrors.Handle(ex, _constructor.DbPath)) { }
    }

    [RelayCommand]
    private void DeleteRecord(int recordId)
    {
        try
        {
            _constructor.DeleteRecord(recordId);
            var r = Records.FirstOrDefault(x => x.Id == recordId);
            if (r is not null) Records.Remove(r);
        }
        catch (Exception ex) when (Utils.DbErrors.Handle(ex, _constructor.DbPath)) { }
    }
}
