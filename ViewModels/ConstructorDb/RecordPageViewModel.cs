using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MyPlanner.Core.Models;
using MyPlanner.Database.Repositories;

namespace MyPlanner.ViewModels.ConstructorDb;

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

    /// <summary>Поле «Название» (системное) — если есть.</summary>
    public DbField? TitleField { get; }
    /// <summary>Поле «Описание» (markdown) — если есть.</summary>
    public DbField? MarkdownField { get; }
    /// <summary>Прочие поля для отображения в списке свойств.</summary>
    public ObservableCollection<PropertyRowVm> Properties { get; } = [];

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _markdownText = "";
    [ObservableProperty] private string _collectionName = "";

    public RecordPageViewModel(ConstructorRepository repository, int recordId, int collectionId)
    {
        _repository   = repository;
        RecordId      = recordId;
        CollectionId  = collectionId;

        var fields = repository.GetFields(collectionId);
        var values = repository.GetFieldValues(recordId);

        TitleField    = fields.FirstOrDefault(f => f.Name == "Название");
        MarkdownField = fields.FirstOrDefault(f => f.FieldType == "markdown");

        if (TitleField is not null)
            _title = values.GetValueOrDefault(TitleField.Id!.Value, "");
        if (MarkdownField is not null)
            _markdownText = values.GetValueOrDefault(MarkdownField.Id!.Value, "");

        var col = repository.GetAllCollections().FirstOrDefault(c => c.Id == collectionId);
        _collectionName = col?.Name ?? "";

        foreach (var f in fields)
        {
            // Заголовок и markdown отображаются отдельно (сверху / снизу).
            if (f.Id == TitleField?.Id) continue;
            if (f.Id == MarkdownField?.Id) continue;
            Properties.Add(new PropertyRowVm(repository, recordId, f, values.GetValueOrDefault(f.Id!.Value, "")));
        }
    }

    public void SaveTitle()
    {
        if (TitleField is null) return;
        _repository.SetFieldValue(RecordId, TitleField.Id!.Value, Title ?? "");
    }

    public void SaveMarkdown()
    {
        if (MarkdownField is null) return;
        _repository.SetFieldValue(RecordId, MarkdownField.Id!.Value, MarkdownText ?? "");
    }
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
