using SmartBase.Core.Models;

namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository
{
    /// <summary>Ключи автоматического подбора: течение времени само не запускает запись связей.
    /// Обычный показ, поиск и ручной выбор продолжают видеть все свойства.</summary>
    public List<RelationPropertyOption> GetAutoRelationPropertyOptions(int collectionId)
    {
        var options = GetRelationPropertyOptions(collectionId);
        var check = new AutoRelationTimeDependencies();
        check.Seed(this, collectionId, options);
        return options.Where(p => check.Problem(this, collectionId, p.Key) is null).ToList();
    }

    // Читается только схема. Один пакет свойств на коллекцию, общий кеш для всех ключей окна;
    // рекурсия не читает значения записей и не запускает вычисление или подбор.
    private sealed class AutoRelationTimeDependencies
    {
        private readonly Dictionary<(string Path, int Collection), Dictionary<string, RelationPropertyOption>> _fields = [];
        private readonly Dictionary<(string Path, int Collection, string Key), string?> _done = [];
        private readonly HashSet<(string Path, int Collection, string Key)> _active = [];

        internal void Seed(ConstructorRepository owner, int collection, IEnumerable<RelationPropertyOption> options) =>
            _fields[(owner.DbPath, collection)] = options.ToDictionary(p => p.Key, StringComparer.Ordinal);

        internal string? Problem(ConstructorRepository owner, int collection, string key)
        {
            var identity = (owner.DbPath, collection, key);
            if (_done.TryGetValue(identity, out var previous)) return previous;
            // Общие ошибки схемы/циклы проверяет существующий валидатор подбора.
            // Здесь ограничиваем только новую зависимость от текущего времени.
            if (_active.Count >= 64 || !_active.Add(identity)) return null;
            string? problem;
            try { problem = Inspect(owner, collection, key); }
            finally { _active.Remove(identity); }
            _done[identity] = problem;
            return problem;
        }

        private string? Inspect(ConstructorRepository owner, int collection, string key)
        {
            if (!owner.CanReadRelationCollection(collection)) return null;
            var parts = RelationConfig.SplitKeys(key);
            if (parts.Count > 1)
            {
                foreach (var part in parts)
                    if (Problem(owner, collection, part) is { } error) return error;
                return null;
            }
            if (!_fields.TryGetValue((owner.DbPath, collection), out var options))
            {
                Seed(owner, collection, owner.GetRelationPropertyOptions(collection));
                options = _fields[(owner.DbPath, collection)];
            }
            if (!options.TryGetValue(key, out var field)) return null;
            if (field.FieldType == "date_period")
            {
                if (DatePeriodConfig.FromConfig(field.ConfigJson) is not { } period)
                    return "Настройка периода времени повреждена. Откройте свойство и задайте ее заново.";
                return period is { Automatic: true, EndFieldId: null }
                    ? "Период до текущего времени нельзя использовать в автоматическом подборе: течение времени не запускает обновление связей. Выберите фиксированный период или две даты."
                    : null;
            }
            if (field.FieldType is "lookup" or "rollup")
            {
                if (RelationComputedConfig.FromConfig(field.ConfigJson) is not { } computed
                    || !options.TryGetValue(computed.RelationFieldId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var relation)
                    || RelationConfig.FromConfig(relation.ConfigJson) is not { } config)
                    return null;
                if (Problem(owner, collection, relation.Key) is { } relationProblem) return relationProblem;
                if (computed.Aggregate == "count") return null;
                var target = config.TargetDbId == 0 ? owner : RelationDbResolver.RepoFor(config.TargetDbId);
                return target is null ? null
                    : Problem(target, config.TargetCollectionId, computed.TargetField);
            }
            if (field.FieldType == "relation" && RelationConfig.FromConfig(field.ConfigJson) is { Auto: { } rule } configAuto)
            {
                foreach (var source in rule.SourceKeys)
                    if (Problem(owner, collection, source) is { } error) return error;
                var target = configAuto.TargetDbId == 0 ? owner : RelationDbResolver.RepoFor(configAuto.TargetDbId);
                if (target is null) return null;
                foreach (var targetKey in rule.TargetKeys.Concat((configAuto.Where ?? []).Select(c => c.Field)))
                    if (Problem(target, configAuto.TargetCollectionId, targetKey) is { } error) return error;
            }
            return null;
        }
    }
}
