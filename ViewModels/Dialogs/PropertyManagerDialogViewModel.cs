using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataBase.Core.Models;
using DataBase.Database.Repositories;

namespace DataBase.ViewModels.Dialogs;

public partial class PropertyManagerDialogViewModel : ViewModelBase
{
    private readonly ConstructorRepository _constructor;
    private readonly int                   _collectionId;

    [ObservableProperty] private ObservableCollection<DbField> _fields = [];
    [ObservableProperty] private DbField?                      _selectedField;
    [ObservableProperty] private string                        _newFieldName = "";
    [ObservableProperty] private string                        _newFieldType = "text";
    [ObservableProperty] private string?                       _errorMessage;

    public static string[] FieldTypes => ["text", "number", "date", "select", "checkbox", "url"];

    public event Action? Changed;

    public PropertyManagerDialogViewModel(ConstructorRepository constructor, int collectionId)
    {
        _constructor  = constructor;
        _collectionId = collectionId;
        Refresh();
    }

    private void Refresh() =>
        Fields = new(_constructor.GetFields(_collectionId));

    [RelayCommand]
    private void AddField()
    {
        if (string.IsNullOrWhiteSpace(NewFieldName)) { ErrorMessage = "Введите название поля"; return; }

        var maxPos = Fields.Select(f => f.Position).DefaultIfEmpty(0).Max();
        _constructor.AddField(new DbField
        {
            CollectionId = _collectionId,
            Name         = NewFieldName.Trim(),
            FieldType    = NewFieldType,
            Position     = maxPos + 1000,
        });

        NewFieldName = "";
        Refresh();
        Changed?.Invoke();
    }

    [RelayCommand]
    private void DeleteField(int fieldId)
    {
        _constructor.DeleteField(fieldId);
        Refresh();
        Changed?.Invoke();
    }

    [RelayCommand]
    private void MoveUp(int fieldId)
    {
        var ids = Fields.Select(f => f.Id!.Value).ToList();
        var idx = ids.IndexOf(fieldId);
        if (idx <= 0) return;
        (ids[idx], ids[idx - 1]) = (ids[idx - 1], ids[idx]);
        _constructor.ReorderFields(ids);
        Refresh();
    }

    [RelayCommand]
    private void MoveDown(int fieldId)
    {
        var ids = Fields.Select(f => f.Id!.Value).ToList();
        var idx = ids.IndexOf(fieldId);
        if (idx < 0 || idx >= ids.Count - 1) return;
        (ids[idx], ids[idx + 1]) = (ids[idx + 1], ids[idx]);
        _constructor.ReorderFields(ids);
        Refresh();
    }
}
