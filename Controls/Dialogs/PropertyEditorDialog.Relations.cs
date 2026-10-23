using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Controls.Dialogs;

public partial class PropertyEditorDialog
{
    private sealed record RelationPropertyItem(string Key, string Name, string FieldType, string? ConfigJson = null)
    {
        public override string ToString() => Name;
    }

    private sealed record RelationFieldItem(int Id, string Name, string ConfigJson)
    {
        public override string ToString() => Name;
    }

    private static readonly string[] RelationAggregates = ["count", "sum", "avg", "min", "max"];
    private (int Database, int Collection)? _relationPropertyTarget;
    private int? _computedRelationField;
    private bool _savingProperty;

    private void InitializeExtendedRelations()
    {
        RelationTargetCombo.SelectionChanged += (_, _) => PopulateRelationProperties();
        RelationModeCombo.SelectionChanged += (_, _) =>
            RelationAutoOptions.IsVisible = RelationModeCombo.SelectedIndex == 1;
        ComputedRelationCombo.SelectionChanged += (_, _) => PopulateComputedTargetProperties();
        ComputedAggregateCombo.SelectionChanged += (_, _) => UpdateRelationComputedVisibility();
        PopulateRelationProperties();
        PopulateComputedRelations();
    }

    private List<RelationPropertyItem> RelationProperties(ConstructorRepository? repo, int collectionId) =>
        repo is null ? [] : repo.GetRelationPropertyOptions(collectionId)
            .Select(p => new RelationPropertyItem(p.Key, p.Name, p.FieldType, p.ConfigJson)).ToList();

    private void PopulateRelationProperties()
    {
        if (_systemMode || _repository is null) return;
        var dbId = (RelationDbCombo.SelectedItem as RelationDbItem)?.Id ?? _currentDbId;
        var target = RelationTargetCombo.SelectedItem as TargetCollectionItem;
        var targetIdentity = target is null ? ((int, int)?)null : (dbId, target.Id);
        var keepSelection = targetIdentity == _relationPropertyTarget;
        _relationPropertyTarget = targetIdentity;
        var properties = target is null ? [] : RelationProperties(RepoForDb(dbId), target.Id);
        SetRelationProperties(RelationDisplayCombo, properties, keepSelection);
        SetRelationProperties(RelationSearchCombo, properties, keepSelection);
        SetRelationProperties(RelationAutoTargetCombo, properties, keepSelection);
        var sources = RelationProperties(_repository, _collectionId)
            .Where(p => p.Key != _editFieldId?.ToString()).ToList();
        SetRelationProperties(RelationAutoSourceCombo, sources);
        RefreshRelationKeyRows();
        // Правило более новой версии относится к прежней цели: с новой его пары задаются заново.
        if (!keepSelection && _keptAutoRule is not null) ForgetKeptAutoRule();
        RefreshRelationAutoRows();
        RefreshRelationConditionRows(keepSelection);
        RefreshRelationDataProperties();
    }

    private static void SetRelationProperties(ComboBox combo, List<RelationPropertyItem> properties, bool keepSelection = true)
    {
        var selected = keepSelection ? (combo.SelectedItem as RelationPropertyItem)?.Key ?? "__title__" : "__title__";
        combo.ItemsSource = properties;
        SelectRelationProperty(combo, selected);
    }

    private static void SelectRelationProperty(ComboBox combo, string key)
    {
        if (combo.ItemsSource is not IEnumerable<RelationPropertyItem> items) return;
        var list = items.ToList();
        combo.SelectedIndex = list.FindIndex(p => p.Key == key);
    }

    private void PopulateComputedRelations()
    {
        if (_systemMode || _repository is null) return;
        ComputedRelationCombo.ItemsSource = _repository.GetFields(_collectionId)
            .Where(f => f.FieldType == "relation" && f.Id != _editFieldId)
            .Select(f => new RelationFieldItem(f.Id!.Value, f.Name, f.ConfigJson)).ToList();
        ComputedRelationCombo.SelectedIndex = -1;
    }

    private void PopulateComputedTargetProperties()
    {
        var selected = ComputedRelationCombo.SelectedItem as RelationFieldItem;
        var keepSelection = selected?.Id == _computedRelationField;
        _computedRelationField = selected?.Id;
        var config = RelationConfig.FromConfig(selected?.ConfigJson);
        var properties = config is null ? [] : RelationProperties(
            config.TargetDbId == 0 ? _repository : RepoForDb(config.TargetDbId), config.TargetCollectionId);
        SetRelationProperties(ComputedTargetCombo, properties, keepSelection);
    }

    private void RestoreRelationComputedConfig(string? configJson)
    {
        var config = RelationComputedConfig.FromConfig(configJson);
        if (config is null) return;
        if (ComputedRelationCombo.ItemsSource is IEnumerable<RelationFieldItem> items)
            ComputedRelationCombo.SelectedIndex = items.ToList().FindIndex(f => f.Id == config.RelationFieldId);
        SelectRelationProperty(ComputedTargetCombo, config.TargetField);
        var index = Array.IndexOf(RelationAggregates, config.Aggregate);
        ComputedAggregateCombo.SelectedIndex = index >= 0 ? index : 0;
    }

    private void UpdateRelationComputedVisibility()
    {
        ComputedAggregateOptions.IsVisible = _selectedType == "rollup";
        ComputedTargetOptions.IsVisible = _selectedType != "rollup" || ComputedAggregateCombo.SelectedIndex != 0;
    }

    private string? BuildRelationComputedConfigJson()
    {
        if (ComputedRelationCombo.SelectedItem is not RelationFieldItem relation)
        { ShowError("Выберите поле-связь"); return null; }
        var aggregate = _selectedType == "lookup" ? "lookup"
            : RelationAggregates[Math.Clamp(ComputedAggregateCombo.SelectedIndex, 0, RelationAggregates.Length - 1)];
        var key = (ComputedTargetCombo.SelectedItem as RelationPropertyItem)?.Key;
        if (aggregate != "count" && key is null)
        { ShowError("Выберите свойство связанной записи"); return null; }
        return new RelationComputedConfig(relation.Id, key ?? "__title__", aggregate).ToConfigJson();
    }

    private async Task<int?> SaveAutoRelationAsync(DbField proposed)
    {
        using var cancellation = new CancellationTokenSource();
        var preview = await RunRelationWorkAsync(
            token => _repository.PreviewAutoRelationAsync(proposed, token, detailed: true), "Подбор связей", cancellation);
        if (!preview.CanApply) { ShowError(preview.Message); return null; }
        var accepted = await new ConfirmDialog(AutoPreviewText(preview, proposed), "Применить").ShowDialog<bool>(this);
        if (!accepted) return null;
        return await RunRelationWorkAsync(
            token => _repository.SaveAutoRelationFieldAsync(proposed, preview, token), "Сохранение связей", cancellation);
    }

    // Итог подбора для подтверждения; каждая дополнительная строка — только при ненулевом числе.
    private string AutoPreviewText(RelationAutoPreview preview, DbField proposed)
    {
        var lines = new List<string>
        {
            $"Автоматические связи: добавить — {preview.Added}, изменить — {preview.Changed}, очистить — {preview.Removed}.",
        };
        if (preview.Unmatched > 0)
        {
            var first = RelationConfig.FromConfig(proposed.ConfigJson)?.Auto?.Pairs.FirstOrDefault()?.SourceField;
            var name = (RelationAutoSourceCombo.ItemsSource as IEnumerable<RelationPropertyItem>)?
                .FirstOrDefault(p => p.Key == first)?.Name;
            lines.Add($"Без совпадения — {preview.Unmatched}" +
                      (preview.UnmatchedEmpty > 0 && name is not null ? $", из них не заполнено «{name}» — {preview.UnmatchedEmpty}" : "") + ".");
        }
        if (preview.FoldOnly > 0) lines.Add($"Совпали только без учета регистра и пробелов — {preview.FoldOnly}.");
        if (preview.OutsideCondition > 0) lines.Add($"Не подходят под условие выбора — {preview.OutsideCondition}.");
        lines.Add("Применить?");
        return string.Join("\n", lines);
    }

    private async Task<T> RunRelationWorkAsync<T>(Func<CancellationToken, Task<T>> work, string title,
                                                CancellationTokenSource cancellation)
    {
        var task = work(cancellation.Token);
        if (await Task.WhenAny(task, Task.Delay(200)) == task) return await task;
        var progress = new ProgressDialog { Title = title };
        progress.SetSaving(title + "…");
        progress.EnableCancel(cancellation.Cancel);
        var closed = progress.ShowDialog(this);
        try { return await task; }
        finally { progress.Done(); await closed; }
    }
}
