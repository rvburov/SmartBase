using System.ComponentModel;
using System.Runtime.CompilerServices;
using DataBase.Core.Models;
using DataBase.Database.Repositories;

namespace DataBase.ViewModels.ConstructorDb;

/// <summary>
/// Одна строка таблицы конструктора. Хранит значения полей по field.Id и
/// пишет изменения сразу в БД (аналог set_field_value по itemChanged в PyQt).
/// Последняя строка-«плюс» представлена IsAddRow=true и не имеет RecordId.
/// </summary>
public class TableRowVm : INotifyPropertyChanged
{
    // Виртуальные ключи для колонок, которые хранятся не в db_field_values,
    // а прямо в DbRecordEntity.Title / DbRecordEntity.Description.
    public const string TitleKey = "__title__";
    public const string DescKey  = "__desc__";

    private readonly ConstructorRepository? _repository;
    private readonly Dictionary<int, string> _values;
    private string _title;
    private string _description;

    public int  RecordId    { get; }
    public int  RowNumber   { get; private set; }
    public bool IsAddRow    { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Обычная строка с данными.</summary>
    public TableRowVm(ConstructorRepository repository, int recordId, int rowNumber,
                     Dictionary<int, string> values, string title, string? description)
    {
        _repository  = repository;
        RecordId     = recordId;
        RowNumber    = rowNumber;
        _values      = values;
        _title       = title       ?? "";
        _description = description ?? "";
    }

    /// <summary>Строка-«плюс» в конце таблицы.</summary>
    private TableRowVm()
    {
        IsAddRow     = true;
        _values      = [];
        _title       = "";
        _description = "";
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
        get
        {
            if (key == TitleKey) return _title;
            if (key == DescKey)  return _description;
            return int.TryParse(key, out var id) && _values.TryGetValue(id, out var v) ? v : "";
        }
        set
        {
            if (IsAddRow) return;
            var newVal = value ?? "";

            if (key == TitleKey)
            {
                if (_title == newVal) return;
                _title = newVal;
                _repository?.SetRecordTitle(RecordId, newVal);
                OnPropertyChanged($"Item[{key}]");
                return;
            }
            if (key == DescKey)
            {
                if (_description == newVal) return;
                _description = newVal;
                _repository?.SetRecordDescription(RecordId, newVal);
                OnPropertyChanged($"Item[{key}]");
                return;
            }
            if (!int.TryParse(key, out var id)) return;
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
