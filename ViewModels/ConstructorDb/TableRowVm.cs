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
    // Виртуальный ключ колонки «Название»: она хранится не в db_field_values, а прямо
    // в DbRecordEntity.Title. «Описание» — обычное поле, у него числовой ключ, как у всех.
    public const string TitleKey = "__title__";

    private ConstructorRepository? _repository;
    private Dictionary<int, string> _values;
    private string _title;
    // Аудит-метаданные записи — источник для «вычисляемых» типов свойств
    // (created_at/updated_at/created_by/updated_by). Ячейки этих типов биндятся
    // к одноименным свойствам ниже, а не к индексатору значений полей.
    private string _createdAt;
    private string _updatedAt;
    private string _createdBy;
    private string _updatedBy;

    public int  RecordId    { get; private set; }
    public int  RowNumber   { get; private set; }
    public bool IsAddRow    { get; }

    // Строка служебной таблицы: значения по строковым ключам колонок читаются из источника при
    // каждом обращении, в базу ничего не пишется (см. Static).
    private readonly Func<string, string>? _named;

    /// <summary>Исходная строка данных служебной таблицы (строка журнала, пользователь); у строк
    /// таблицы коллекции null.</summary>
    public object? Source { get; }

    /// <summary>Оформление отдельных ячеек строки: ключ колонки → стиль. У подавляющего
    /// большинства строк null — цветные ячейки редки, словарь заводим только когда они есть.</summary>
    public IReadOnlyDictionary<string, RowStyle>? CellStyles { get; private set; }

    /// <summary>Стиль конкретной ячейки, без каскада (каскад собирает таблица — только у нее
    /// есть оформление строки и колонки, см. RowStyle.Resolve).</summary>
    public RowStyle CellStyle(string key) =>
        CellStyles is not null && CellStyles.TryGetValue(key, out var c) ? c : RowStyle.None;

    /// <summary>Оформление ячеек строки. Уведомление идет тем же «Item», что и у значений —
    /// привязки ячеек перечитывают и цвет.</summary>
    /// <param name="notify">false — когда сразу следом идет <see cref="Fill"/>: он уведомит
    /// сам, а на странице в 200 строк лишний проход по всем ячейкам не бесплатен.</param>
    public void SetStyle(IReadOnlyDictionary<string, RowStyle>? cellStyles, bool notify = true)
    {
        CellStyles = cellStyles is { Count: > 0 } ? cellStyles : null;
        if (notify) NotifyAllCells();
    }

    /// <summary>Аудит-метаданные записи для «вычисляемых» типов свойств (read-only).</summary>
    public string CreatedAt => _createdAt;
    public string UpdatedAt => _updatedAt;
    public string CreatedBy => _createdBy;
    public string UpdatedBy => _updatedBy;
    // Строка-заглушка: данные страницы еще не пришли (async-подгрузка), ячейки показывают
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
                     Dictionary<int, string> values, string title,
                     string createdAt = "", string updatedAt = "",
                     string createdBy = "", string updatedBy = "")
    {
        _repository  = repository;
        RecordId     = recordId;
        RowNumber    = rowNumber;
        _values      = values;
        _title       = title       ?? "";
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
        _createdAt   = "";
        _updatedAt   = "";
        _createdBy   = "";
        _updatedBy   = "";
    }

    public static TableRowVm AddRow() => new();

    /// <summary>
    /// Строка служебной таблицы (ServiceGrid): значения по строковым ключам колонок дает
    /// <paramref name="valueOf"/> — живое чтение из источника, поэтому правка источника видна после
    /// перерисовки без пересборки строки. Записи в базу нет, репозитория нет; номер строки служит и
    /// ключом записи (выделение строки в сетке идет по нему).
    /// </summary>
    public static TableRowVm Static(int rowNumber, Func<string, string> valueOf, object? source = null) =>
        new(rowNumber, valueOf, source);

    private TableRowVm(int rowNumber, Func<string, string> valueOf, object? source)
    {
        RecordId     = rowNumber;
        RowNumber    = rowNumber;
        _named       = valueOf;
        Source       = source;
        _values      = [];
        _title       = "";
        _createdAt   = "";
        _updatedAt   = "";
        _createdBy   = "";
        _updatedBy   = "";
    }

    /// <summary>Строка-заглушка «Загрузка…» до прихода данных страницы (async-подгрузка).</summary>
    public static TableRowVm Loading(int rowNumber) => new(rowNumber);

    private TableRowVm(int rowNumber)
    {
        IsLoading    = true;
        RowNumber    = rowNumber;
        _values      = [];
        _title       = "";
        _createdAt   = "";
        _updatedAt   = "";
        _createdBy   = "";
        _updatedBy   = "";
    }

    /// <summary>
    /// Заполнить строку-заглушку реальными данными и обновить ячейки.
    /// </summary>
    public void Fill(ConstructorRepository repository, int recordId,
                     Dictionary<int, string> values, string title,
                     string createdAt = "", string updatedAt = "",
                     string createdBy = "", string updatedBy = "")
    {
        _repository  = repository;
        RecordId     = recordId;
        _values      = values;
        _title       = title       ?? "";
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

    /// <summary>Поднять "Item" извне — перечитать привязки ячеек строки без смены данных (напр., у
    /// строки-источника изменилось название ЦЕЛИ ее связи: read-ячейка-связь перечитает показ из кеша).</summary>
    public void NotifyCellsChanged() => NotifyAllCells();

    /// <summary>Дочитанные значения колонок, которых не было в окне при загрузке страницы (прокрутка
    /// вбок): добавить к строке и перечитать ячейки. Записи без значения в словаре нет — ячейка и так
    /// пустая. Заглушку и строку «+» не трогает.</summary>
    public void MergeValues(IReadOnlyDictionary<int, string> values)
    {
        if (IsLoading || IsAddRow) return;
        foreach (var (fid, v) in values) _values[fid] = v;
        NotifyAllCells();
    }

    // Обновить привязки «вычисляемых» ячеек (created_at/updated_at/created_by/updated_by),
    // которые биндятся к именованным свойствам, а не к индексатору значений.
    private void NotifyMeta()
    {
        OnPropertyChanged(nameof(CreatedAt));
        OnPropertyChanged(nameof(UpdatedAt));
        OnPropertyChanged(nameof(CreatedBy));
        OnPropertyChanged(nameof(UpdatedBy));
    }

    // Локально отражаем «последнюю правку» записи (БД ее проставила в SetFieldValue/TouchUpdated),
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
    /// Индексатор по строковому ключу field.Id — сетка читает значение ячейки как [123].
    /// Запись прокидывается в БД, чекбоксы хранят "0"/"1".
    /// </summary>
    public string this[string key]
    {
        get
        {
            if (_named is not null) return _named(key);
            if (IsLoading) return key == TitleKey ? "Загрузка…" : "";
            if (key == TitleKey) return _title;
            return int.TryParse(key, out var id) && _values.TryGetValue(id, out var v) ? v : "";
        }
        set
        {
            if (IsAddRow || IsLoading || _named is not null) return;
            var newVal = value ?? "";

            // Запись в БД может не пройти в многопользовательском режиме (файл занят чужим
            // писателем дольше busy_timeout / база пропала): известные ошибки гасим тостом
            // (DbErrors), буфер строки не меняем — ячейка показывает прежнее значение.
            if (key == TitleKey)
            {
                // Стертое название хранится как «Без названия» — правило всех мест правки (TitleText).
                // Запись без названия, у которой в базе пусто, так и остается: вход в правку и выход
                // из нее без набора — не изменение, в журнал оно попадать не должно.
                if (TitleText.IsPlaceholder(newVal) && TitleText.IsPlaceholder(_title)) return;
                newVal = TitleText.Stored(newVal);
                if (_title == newVal) return;
                IReadOnlyList<(int FieldId, string Value)> autoTitle;
                try { autoTitle = _repository?.SetRecordTitle(RecordId, newVal) ?? []; }
                catch (Exception ex) when (DbErrors.Handle(ex, _repository?.DbPath)) { NotifyAllCells(); return; }
                _title = newVal;
                ApplyAutoFileChanges(autoTitle);   // «Название» — источник авто-file: файл-ячейка обновится
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
            ApplyAutoFileChanges(autoChanges, skipFieldId: id);
            BumpUpdatedLocal();
            NotifyAllCells();
            Edited?.Invoke(this, RecordId);
        }
    }

    // Отразить побочные изменения авто-привязки файлов в кеше строки (пустой путь → удаляем).
    // skipFieldId — редактируемое поле (не трогаем, чтобы не сбить каретку ввода); для правки
    // «Названия» пропуск не нужен (файл-поле не совпадает с системной колонкой).
    private void ApplyAutoFileChanges(IReadOnlyList<(int FieldId, string Value)> changes, int skipFieldId = 0)
    {
        foreach (var (fid, val) in changes)
        {
            if (fid == skipFieldId) continue;
            if (val.Length == 0) _values.Remove(fid); else _values[fid] = val;
        }
    }

    /// <summary>Прочитать значение без обращения к БД (для not-binding кода).</summary>
    public string GetRaw(int fieldId) => _values.TryGetValue(fieldId, out var v) ? v : "";

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
