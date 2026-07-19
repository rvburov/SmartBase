using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.ViewModels.ConstructorDb;

/// <summary>
/// Модель состояния правой панели «Страница записи» (Obsidian-стиль):
/// заголовок + свойства + markdown-описание. Сохранение — потаймеру/при потере фокуса,
/// поэтому VM держит только текущие значения (буфер) и пробрасывает их в репозиторий.
/// </summary>
public partial class RecordPageViewModel : ObservableObject
{
    private readonly ConstructorRepository _repository;

    public const string DescKey = "__desc__";

    public int RecordId      { get; }
    public int CollectionId  { get; }

    /// <summary>Прочие поля для отображения в списке свойств (без Title/Description).</summary>
    public ObservableCollection<PropertyRowVm> Properties { get; } = [];

    /// <summary>
    /// Свойства и системное «Описание» в ТОМ ЖЕ порядке, что колонки в таблице
    /// (column_order, иначе дефолт). «Название» сюда не входит — оно в шапке записи;
    /// «Добавить свойство» добавляет уже представление и всегда стоит последним.
    /// </summary>
    public List<PanelItem> OrderedItems { get; } = [];

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _markdownText = "";
    [ObservableProperty] private string _collectionName = "";

    public RecordPageViewModel(ConstructorRepository repository, int recordId, int collectionId)
    {
        _repository   = repository;
        RecordId      = recordId;
        CollectionId  = collectionId;

        var record = repository.GetRecord(recordId);
        var fields = repository.GetFields(collectionId);
        var values = repository.GetFieldValues(recordId);

        _title        = record?.Title       ?? "";
        _markdownText = record?.Description ?? "";

        var col = repository.GetAllCollections().FirstOrDefault(c => c.Id == collectionId);
        _collectionName = col?.Name ?? "";

        foreach (var f in fields)
        {
            Properties.Add(new PropertyRowVm(repository, recordId, f, values.GetValueOrDefault(f.Id!.Value, "")));
        }

        // Порядок как в таблице: эффективная позиция колонки = из column_order, иначе дефолт
        // (Title=1000 — пропускаем, Desc=2000, поле=3000+Position). Та же логика, что и в TableView.
        var order = repository.GetColumnOrder(collectionId);
        int Pos(string key, int fallback) => order.TryGetValue(key, out var p) ? p : fallback;

        var ordered = new List<(int pos, PanelItem item)>
        {
            (Pos(DescKey, 2000), new DescriptionItem()),
        };
        foreach (var row in Properties)
            ordered.Add((Pos(row.Field.Id!.Value.ToString(), 3000 + row.Field.Position), new FieldItem(row)));

        foreach (var (_, item) in ordered.OrderBy(x => x.pos))
            OrderedItems.Add(item);
    }

    // Ошибки записи (файл занят/пропал в многопользовательском режиме) гасятся тостом —
    // панель не падает, значение останется в буфере до следующего сохранения.
    public void SaveTitle()
    {
        try { _repository.SetRecordTitle(RecordId, Title ?? ""); }
        catch (Exception ex) when (DbErrors.Handle(ex, _repository.DbPath)) { }
    }

    public void SaveMarkdown()
    {
        try { _repository.SetRecordDescription(RecordId, MarkdownText); }
        catch (Exception ex) when (DbErrors.Handle(ex, _repository.DbPath)) { }
    }
}

/// <summary>Элемент списка панели записи: пользовательское свойство либо системное «Описание».</summary>
public abstract record PanelItem;
public sealed record FieldItem(PropertyRowVm Row) : PanelItem;
public sealed record DescriptionItem : PanelItem;

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

    public void Persist()
    {
        try { _repository.SetFieldValue(_recordId, Field.Id!.Value, Value ?? ""); }
        catch (Exception ex) when (DbErrors.Handle(ex, _repository.DbPath)) { }
    }
}
