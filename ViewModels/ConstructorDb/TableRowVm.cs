using System.ComponentModel;
using System.Runtime.CompilerServices;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

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
    // Аудит-метаданные записи — источник для «вычисляемых» типов свойств
    // (created_at/updated_at/created_by/updated_by). Ячейки этих типов биндятся
    // к одноимённым свойствам ниже, а не к индексатору значений полей.
    private string _createdAt;
    private string _updatedAt;
    private string _createdBy;
    private string _updatedBy;

    public int  RecordId    { get; private set; }
    public int  RowNumber   { get; private set; }
    public bool IsAddRow    { get; }

    /// <summary>Аудит-метаданные записи для «вычисляемых» типов свойств (read-only).</summary>
    public string CreatedAt => _createdAt;
    public string UpdatedAt => _updatedAt;
    public string CreatedBy => _createdBy;
    public string UpdatedBy => _updatedBy;
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
                     Dictionary<int, string> values, string title, string? description,
                     string createdAt = "", string updatedAt = "",
                     string createdBy = "", string updatedBy = "")
    {
        _repository  = repository;
        RecordId     = recordId;
        RowNumber    = rowNumber;
        _values      = values;
        _title       = title       ?? "";
        _description = description ?? "";
        _createdAt   = createdAt   ?? "";
        _updatedAt   = updatedAt   ?? "";
        _createdBy   = createdBy   ?? "";
        _updatedBy   = updatedBy   ?? "";
    }

    /// <summary>Строка-«плюс» в конце таблицы.</summary>
    private TableRowVm()
    {
        IsAddRow     = true;
        _values      = [];
        _title       = "";
        _description = "";
        _createdAt   = "";
        _updatedAt   = "";
        _createdBy   = "";
        _updatedBy   = "";
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
        _createdAt   = "";
        _updatedAt   = "";
        _createdBy   = "";
        _updatedBy   = "";
    }

    /// <summary>
    /// Заполнить строку-заглушку реальными данными и обновить ячейки.
    /// </summary>
    public void Fill(ConstructorRepository repository, int recordId,
                     Dictionary<int, string> values, string title, string? description,
                     string createdAt = "", string updatedAt = "",
                     string createdBy = "", string updatedBy = "")
    {
        _repository  = repository;
        RecordId     = recordId;
        _values      = values;
        _title       = title       ?? "";
        _description = description ?? "";
        _createdAt   = createdAt   ?? "";
        _updatedAt   = updatedAt   ?? "";
        _createdBy   = createdBy   ?? "";
        _updatedBy   = updatedBy   ?? "";
        IsLoading    = false;

        // Уведомляем именем индексатора "Item": Avalonia ReflectionIndexerNode.ShouldUpdate
        // реагирует на имя самого индексатора (GetDeclaredProperty("Item")), а НЕ на WPF-стиль
        // "Item[key]" — тот он молча игнорирует. Одно "Item" перечитывает все ячейки строки.
        NotifyAllCells();
        NotifyMeta();
    }

    // Перечитать все привязки ячеек строки (каждая по своему ключу): см. комментарий в Fill.
    private void NotifyAllCells() => OnPropertyChanged("Item");

    // Обновить привязки «вычисляемых» ячеек (created_at/updated_at/created_by/updated_by),
    // которые биндятся к именованным свойствам, а не к индексатору значений.
    private void NotifyMeta()
    {
        OnPropertyChanged(nameof(CreatedAt));
        OnPropertyChanged(nameof(UpdatedAt));
        OnPropertyChanged(nameof(CreatedBy));
        OnPropertyChanged(nameof(UpdatedBy));
    }

    // Локально отражаем «последнюю правку» записи (БД её проставила в SetFieldValue/TouchUpdated),
    // чтобы «вычисляемые» ячейки «Дата/Изменение пользователем» обновились сразу, без перечитывания
    // строки из БД. Значения совпадают с записанными в БД с точностью до момента правки.
    private void BumpUpdatedLocal()
    {
        _updatedAt = System.DateTime.Now.ToString("o");
        _updatedBy = CurrentUser.DisplayName;
        NotifyMeta();
    }

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

            // Запись в БД может не пройти в многопользовательском режиме (файл занят чужим
            // писателем дольше busy_timeout / база пропала): известные ошибки гасим тостом
            // (DbErrors), буфер строки не меняем — ячейка показывает прежнее значение.
            if (key == TitleKey)
            {
                if (_title == newVal) return;
                try { _repository?.SetRecordTitle(RecordId, newVal); }
                catch (Exception ex) when (DbErrors.Handle(ex, _repository?.DbPath)) { NotifyAllCells(); return; }
                _title = newVal;
                BumpUpdatedLocal();
                NotifyAllCells();
                Edited?.Invoke(this, RecordId);
                return;
            }
            if (key == DescKey)
            {
                if (_description == newVal) return;
                try { _repository?.SetRecordDescription(RecordId, newVal); }
                catch (Exception ex) when (DbErrors.Handle(ex, _repository?.DbPath)) { NotifyAllCells(); return; }
                _description = newVal;
                BumpUpdatedLocal();
                NotifyAllCells();
                Edited?.Invoke(this, RecordId);
                return;
            }
            if (!int.TryParse(key, out var id)) return;
            if (_values.TryGetValue(id, out var old) && old == newVal) return;

            IReadOnlyList<(int FieldId, string Value)> autoChanges;
            try { autoChanges = _repository?.SetFieldValue(RecordId, id, newVal) ?? []; }
            catch (Exception ex) when (DbErrors.Handle(ex, _repository?.DbPath)) { NotifyAllCells(); return; }
            _values[id] = newVal;
            // Побочные изменения авто-привязки файлов (правка столбца-источника пересчитала
            // файл-столбец этой записи) — отражаем в кеше, чтобы файл-ячейка обновилась сразу.
            // Само редактируемое поле (id) не трогаем — каретка ввода не сбивается.
            foreach (var (fid, val) in autoChanges)
            {
                if (fid == id) continue;
                if (val.Length == 0) _values.Remove(fid); else _values[fid] = val;
            }
            BumpUpdatedLocal();
            NotifyAllCells();
            Edited?.Invoke(this, RecordId);
        }
    }

    /// <summary>Прочитать значение без обращения к БД (для not-binding кода).</summary>
    public string GetRaw(int fieldId) => _values.TryGetValue(fieldId, out var v) ? v : "";

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
