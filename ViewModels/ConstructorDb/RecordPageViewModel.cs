using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DataBase.Core.Models;
using DataBase.Database.Repositories;

namespace DataBase.ViewModels.ConstructorDb;

/// <summary>
/// Модель состояния правой панели «Страница записи» (Obsidian-стиль):
/// заголовок + свойства + markdown-описание. Сохранение — потаймеру/при потере фокуса,
/// поэтому VM держит только текущие значения (буфер) и пробрасывает их в репозиторий.
/// </summary>
public partial class RecordPageViewModel : ObservableObject
{
    private readonly ConstructorRepository _repository;

    public int RecordId      { get; }
    public int CollectionId  { get; }

    /// <summary>Прочие поля для отображения в списке свойств (без Title/Description).</summary>
    public ObservableCollection<PropertyRowVm> Properties { get; } = [];

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
    }

    public void SaveTitle() =>
        _repository.SetRecordTitle(RecordId, Title ?? "");

    public void SaveMarkdown() =>
        _repository.SetRecordDescription(RecordId, MarkdownText);
}

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
        _repository.SetFieldValue(_recordId, Field.Id!.Value, Value ?? "");
    }
}
