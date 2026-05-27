using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MyPlanner.Core.Models;
using MyPlanner.Database.Repositories;

namespace MyPlanner.ViewModels.ConstructorDb;

public partial class ListViewModel : ViewModelBase
{
    private readonly ConstructorRepository _constructor;
    private readonly EventRepository       _events;

    [ObservableProperty] private ObservableCollection<DbField>  _fields  = [];
    [ObservableProperty] private ObservableCollection<DbRecord> _records = [];
    [ObservableProperty] private int? _collectionId;

    public ListViewModel(ConstructorRepository constructor, EventRepository events)
    {
        _constructor = constructor;
        _events      = events;
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
