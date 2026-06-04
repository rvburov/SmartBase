using System.ComponentModel;
using System.Runtime.CompilerServices;
using MyPlanner.Core.Models;
using MyPlanner.Database.Repositories;

namespace MyPlanner.ViewModels.ConstructorDb;

/// <summary>
/// Одна строка таблицы конструктора. Хранит значения полей по field.Id и
/// пишет изменения сразу в БД (аналог set_field_value по itemChanged в PyQt).
/// Последняя строка-«плюс» представлена IsAddRow=true и не имеет RecordId.
/// </summary>
public class TableRowVm : INotifyPropertyChanged
{
    private readonly ConstructorRepository? _repository;
    private readonly Dictionary<int, string> _values;

    public int  RecordId  { get; }
    public int  RowNumber { get; private set; }
    public bool IsAddRow  { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Обычная строка с данными.</summary>
    public TableRowVm(ConstructorRepository repository, int recordId, int rowNumber, Dictionary<int, string> values)
    {
        _repository = repository;
        RecordId    = recordId;
        RowNumber   = rowNumber;
        _values     = values;
    }

    /// <summary>Строка-«плюс» в конце таблицы.</summary>
    private TableRowVm()
    {
        IsAddRow = true;
        _values  = [];
    }

    public static TableRowVm AddRow() => new();

    public void SetRowNumber(int n)
    {
        if (RowNumber == n) return;
        RowNumber = n;
        OnPropertyChanged(nameof(RowNumber));
    }

    /// <summary>
    /// Индексатор по строковому ключу field.Id — DataGrid биндится сюда как [123].
    /// Запись прокидывается в БД, чекбоксы хранят "0"/"1".
    /// </summary>
    public string this[string key]
    {
        get => int.TryParse(key, out var id) && _values.TryGetValue(id, out var v) ? v : "";
        set
        {
            if (IsAddRow || !int.TryParse(key, out var id)) return;
            var newVal = value ?? "";
            if (_values.TryGetValue(id, out var old) && old == newVal) return;

            _values[id] = newVal;
            _repository?.SetFieldValue(RecordId, id, newVal);
            OnPropertyChanged($"Item[{key}]");
        }
    }

    /// <summary>Прочитать значение без обращения к БД (для not-binding кода).</summary>
    public string GetRaw(int fieldId) => _values.TryGetValue(fieldId, out var v) ? v : "";

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
