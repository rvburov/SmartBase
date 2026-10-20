using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.ViewModels.ConstructorDb;

/// <summary>
/// Модель состояния правой панели «Страница записи» (Obsidian-стиль):
/// заголовок + свойства + нижний текстовый редактор. Сохранение — потаймеру/при потере
/// фокуса, поэтому VM держит только текущие значения (буфер) и пробрасывает их в репозиторий.
/// </summary>
public partial class RecordPageViewModel : ObservableObject
{
    private readonly ConstructorRepository _repository;

    public int RecordId      { get; }
    public int CollectionId  { get; }

    /// <summary>Поля для отображения в списке свойств (без системного «Названия»).</summary>
    public ObservableCollection<PropertyRowVm> Properties { get; } = [];

    /// <summary>
    /// Свойства в ТОМ ЖЕ порядке, что колонки в таблице (column_order, иначе дефолт), вместе с
    /// карточкой системного «Названия» (<see cref="TitleItem"/>): оно и в шапке записи, и среди
    /// свойств — там длинное название правится в нижнем блоке. «Добавить свойство» добавляет
    /// уже представление и всегда стоит последним.
    /// </summary>
    public List<PanelItem> OrderedItems { get; } = [];

    /// <summary>Подпись колонки «Название» этой коллекции (ее можно переименовать в таблице).</summary>
    public string TitleLabel { get; }

    /// <summary>
    /// Id поля с ролью «Описание» (null — коллекция без описания). Нижний блок панели по
    /// умолчанию открыт именно на нем; поля нет — снизу пусто.
    /// </summary>
    public int? DescriptionFieldId { get; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _collectionName = "";

    public RecordPageViewModel(ConstructorRepository repository, int recordId, int collectionId)
    {
        _repository   = repository;
        RecordId      = recordId;
        CollectionId  = collectionId;

        var record = repository.GetRecord(recordId);
        var fields = repository.GetFields(collectionId);
        var values = repository.GetFieldValues(recordId);

        _title        = record?.Title ?? "";

        var col = repository.GetAllCollections().FirstOrDefault(c => c.Id == collectionId);
        _collectionName = col?.Name ?? "";

        foreach (var f in fields)
        {
            // «Вычисляемые» типы значения в db_field_values не имеют — берем из аудит-метаданных записи.
            var initial = FieldTypeInfo.IsAuditComputed(f.FieldType)
                ? FieldTypeInfo.ComputedValue(record, f.FieldType)
                : values.GetValueOrDefault(f.Id!.Value, "");
            Properties.Add(new PropertyRowVm(repository, recordId, f, initial));
        }

        // Поле-описание ищем по роли: имя у него пользовательское, как у любого другого.
        DescriptionFieldId = fields.FirstOrDefault(f => f.IsDescription)?.Id;

        TitleLabel = repository.GetSystemFieldNames(collectionId)
                               .GetValueOrDefault(TableRowVm.TitleKey, "Название");

        // Порядок как в таблице: эффективная позиция колонки = из column_order, иначе дефолт
        // (Title=1000, поле=3000+Position). Та же логика, что и в TableView.
        var order = repository.GetColumnOrder(collectionId);
        int Pos(string key, int fallback) => order.TryGetValue(key, out var p) ? p : fallback;

        // «Название» первым в списке до сортировки: при равных позициях (стабильный OrderBy)
        // оно идет раньше полей — как и колонка в таблице.
        var ordered = new List<(int pos, PanelItem item)> { (Pos(TableRowVm.TitleKey, 1000), new TitleItem()) };
        foreach (var row in Properties)
            ordered.Add((Pos(row.Field.Id!.Value.ToString(), 3000 + row.Field.Position), new FieldItem(row)));

        foreach (var (_, item) in ordered.OrderBy(x => x.pos))
            OrderedItems.Add(item);
    }

    // Ошибки записи (файл занят/пропал в многопользовательском режиме) гасятся тостом —
    // панель не падает, значение останется в буфере до следующего сохранения. Возвращают побочные
    // изменения авто-привязки файлов (если «Название» — источник авто-file поля), чтобы панель
    // обновила файл-редакторы сразу.
    public IReadOnlyList<(int FieldId, string Value)> SaveTitle()
    {
        try { return _repository.SetRecordTitle(RecordId, Title ?? ""); }
        catch (Exception ex) when (DbErrors.Handle(ex, _repository.DbPath)) { return []; }
    }

    /// <summary>Название, как его показывают поля ввода: «Без названия» у них — плейсхолдер,
    /// а не текст.</summary>
    public string EditableTitle => TitleText.Editable(Title);

    /// <summary>
    /// Сохранить название, набранное в поле ввода (шапка, карточка «Название», нижний блок) —
    /// одно правило для всех трех: крайние пробелы снимаются, пустое хранится как «Без названия».
    /// </summary>
    public IReadOnlyList<(int FieldId, string Value)> SaveTitleInput(string text)
    {
        Title = TitleText.Stored(text.Trim());
        return SaveTitle();
    }
}

/// <summary>Элемент списка панели записи: пользовательское свойство или системное «Название».</summary>
public abstract record PanelItem;
public sealed record FieldItem(PropertyRowVm Row) : PanelItem;
public sealed record TitleItem : PanelItem;

/// <summary>
/// Одна строка свойств в RecordPageView — пара «название поля → редактор значения».
/// </summary>
public partial class PropertyRowVm : ObservableObject
{
    private readonly ConstructorRepository _repository;
    private readonly int _recordId;

    public DbField Field { get; }

    [ObservableProperty] private string _value;

    public PropertyRowVm(ConstructorRepository repository, int recordId, DbField field, string initialValue)
    {
        _repository = repository;
        _recordId   = recordId;
        Field       = field;
        _value      = initialValue;
    }

    /// <summary>Сохранить значение. Возвращает побочные изменения авто-привязки файлов
    /// (файл-столбцы этой записи пересчитались из-за правки источника) — панель записи применяет
    /// их к соответствующим файл-редакторам, чтобы обновить показ сразу.</summary>
    public IReadOnlyList<(int FieldId, string Value)> Persist()
    {
        try { return _repository.SetFieldValue(_recordId, Field.Id!.Value, Value ?? ""); }
        catch (Exception ex) when (DbErrors.Handle(ex, _repository.DbPath)) { return []; }
    }
}
