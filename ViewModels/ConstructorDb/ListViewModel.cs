using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.ViewModels.ConstructorDb;

public partial class ListViewModel : ViewModelBase
{
    private readonly ConstructorRepository _constructor;

    [ObservableProperty] private ObservableCollection<DbField>  _fields  = [];
    [ObservableProperty] private ObservableCollection<DbRecord> _records = [];
    [ObservableProperty] private int? _collectionId;

    public ListViewModel(ConstructorRepository constructor)
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
}
