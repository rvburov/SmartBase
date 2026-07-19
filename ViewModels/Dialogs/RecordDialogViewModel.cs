using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.ViewModels.Dialogs;

public partial class RecordDialogViewModel : ViewModelBase
{
    private readonly ConstructorRepository _constructor;
    private readonly int                   _collectionId;
    private readonly int?                  _recordId;

    [ObservableProperty] private ObservableCollection<DbField>         _fields  = [];
    [ObservableProperty] private Dictionary<int, string>               _values  = [];
    [ObservableProperty] private string?                               _errorMessage;

    public bool        IsEdit => _recordId.HasValue;
    public event Action? Accepted;
    public event Action? DeleteRequested;

    public RecordDialogViewModel(ConstructorRepository constructor, int collectionId, int? recordId = null)
    {
        _constructor  = constructor;
        _collectionId = collectionId;
        _recordId     = recordId;

        Fields = new(_constructor.GetFields(collectionId));
        Values = recordId.HasValue
            ? _constructor.GetFieldValues(recordId.Value)
            : [];
    }

    [RelayCommand]
    private void Save()
    {
        var required = Fields.FirstOrDefault(f => f.IsRequired);
        if (required is not null && !Values.TryGetValue(required.Id!.Value, out var v) || string.IsNullOrWhiteSpace(Values.GetValueOrDefault(required?.Id ?? 0)))
        {
            ErrorMessage = $"Поле «{required?.Name}» обязательно";
            return;
        }

        // Ошибки записи (файл занят чужим писателем/пропал) гасятся тостом (DbErrors) —
        // окно остаётся открытым, введённые значения не теряются, можно повторить.
        try
        {
            int recordId;
            if (_recordId is null)
            {
                var maxPos = _constructor.GetRecords(_collectionId).Select(r => r.Position).DefaultIfEmpty(0).Max();
                var rec = _constructor.AddRecord(new DbRecord
                {
                    CollectionId = _collectionId,
                    Position     = maxPos + 1000,
                });
                recordId = rec.Id!.Value;
            }
            else
            {
                recordId = _recordId.Value;
            }

            foreach (var (fieldId, value) in Values)
                _constructor.SetFieldValue(recordId, fieldId, value);
        }
        catch (Exception ex) when (SmartBase.Utils.DbErrors.Handle(ex, _constructor.DbPath))
        {
            return;
        }

        Accepted?.Invoke();
    }

    [RelayCommand]
    private void Delete()
    {
        try
        {
            if (_recordId.HasValue)
                _constructor.DeleteRecord(_recordId.Value);
        }
        catch (Exception ex) when (SmartBase.Utils.DbErrors.Handle(ex, _constructor.DbPath))
        {
            return;
        }
        DeleteRequested?.Invoke();
    }
}
