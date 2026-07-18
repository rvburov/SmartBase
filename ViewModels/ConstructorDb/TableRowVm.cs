using System.ComponentModel;
using System.Runtime.CompilerServices;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.ViewModels.ConstructorDb;

/// <summary>
/// Одна строка таблицы конструктора. Хранит значения полей по field.Id и
/// пишет изменения сразу в БД.
/// Последняя строка-«плюс» представлена IsAddRow=true и не имеет RecordId.
/// </summary>
public class TableRowVm : INotifyPropertyChanged
{
    // Виртуальные ключи для колонок, которые хранятся не в db_field_values,
    // а прямо в DbRecordEntity.Title / DbRecordEntity.Description.
    public const string TitleKey = "__title__";
    public const string DescKey  = "__desc__";

    private ConstructorRepository? _repository;
    private Dictionary<int, string> _values;
    private string _title;
    private string _description;

    public int  RecordId    { get; private set; }
    public int  RowNumber   { get; private set; }
    public bool IsAddRow    { get; }
    // Строка-заглушка: данные страницы ещё не пришли (async-подгрузка), ячейки показывают
    // «Загрузка…». Заполняется методом Fill, который обновляет ячейки.
    public bool IsLoading   { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Пользователь отредактировал ячейку этой строки прямо в таблице (имя/описание/значение
    /// уже записаны в БД). Аргумент — RecordId. Поднимается ТОЛЬКО из сеттера индексатора (правка из
    /// таблицы), но НЕ из Fill (это загрузка данных страницы). Нужно для живой синхронизации открытой
    /// панели «Страница записи»: правка в таблице сразу отражается в записи, и наоборот.</summary>
    public event EventHandler<int>? Edited;

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

    /// <summary>Строка-заглушка «Загрузка…» до прихода данных страницы (async-подгрузка).</summary>
    public static TableRowVm Loading(int rowNumber) => new(rowNumber);

    private TableRowVm(int rowNumber)
    {
        IsLoading    = true;
        RowNumber    = rowNumber;
        _values      = [];
        _title       = "";
        _description = "";
    }

    /// <summary>
    /// Заполнить строку-заглушку реальными данными и обновить ячейки.
    /// </summary>
    public void Fill(ConstructorRepository repository, int recordId,
                     Dictionary<int, string> values, string title, string? description)
    {
        _repository  = repository;
        RecordId     = recordId;
        _values      = values;
        _title       = title       ?? "";
        _description = description ?? "";
        IsLoading    = false;

        // Уведомляем именем индексатора "Item": Avalonia ReflectionIndexerNode.ShouldUpdate
        // реагирует на имя самого индексатора (GetDeclaredProperty("Item")), а НЕ на WPF-стиль
        // "Item[key]" — тот он молча игнорирует. Одно "Item" перечитывает все ячейки строки.
        NotifyAllCells();
    }

    // Перечитать все привязки ячеек строки (каждая по своему ключу): см. комментарий в Fill.
    private void NotifyAllCells() => OnPropertyChanged("Item");

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
            if (IsLoading) return key == TitleKey ? "Загрузка…" : "";
            if (key == TitleKey) return _title;
            if (key == DescKey)  return _description;
            return int.TryParse(key, out var id) && _values.TryGetValue(id, out var v) ? v : "";
        }
        set
        {
            if (IsAddRow || IsLoading) return;
            var newVal = value ?? "";

            if (key == TitleKey)
            {
                if (_title == newVal) return;
                _title = newVal;
                _repository?.SetRecordTitle(RecordId, newVal);
                NotifyAllCells();
                Edited?.Invoke(this, RecordId);
                return;
            }
            if (key == DescKey)
            {
                if (_description == newVal) return;
                _description = newVal;
                _repository?.SetRecordDescription(RecordId, newVal);
                NotifyAllCells();
                Edited?.Invoke(this, RecordId);
                return;
            }
            if (!int.TryParse(key, out var id)) return;
            if (_values.TryGetValue(id, out var old) && old == newVal) return;

            _values[id] = newVal;
            _repository?.SetFieldValue(RecordId, id, newVal);
            NotifyAllCells();
            Edited?.Invoke(this, RecordId);
        }
    }

    /// <summary>Прочитать значение без обращения к БД (для not-binding кода).</summary>
    public string GetRaw(int fieldId) => _values.TryGetValue(fieldId, out var v) ? v : "";

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
