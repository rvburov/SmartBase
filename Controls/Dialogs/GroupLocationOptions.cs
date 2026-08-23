using SmartBase.Core.Models;

namespace SmartBase.Controls.Dialogs;

// Построение вариантов выпадающего списка «Расположение» с полным путем вложенности
// групп — «1», «1 / 1», «1 (копия) / 2» … — чтобы одноименные группы на разных
// уровнях были различимы.
internal static class GroupLocationOptions
{
    // groups — все группы; excludeSubtreeRoots — корни поддеревьев, исключаемых из вариантов
    // (для перемещения групп: нельзя вложить группу в саму себя или своего потомка; при мультивыборе
    // исключаем все выбранные группы сразу).
    public static List<(string Label, int? Id)> Build(
        IEnumerable<DbGroup> groups, IReadOnlySet<int>? excludeSubtreeRoots = null)
    {
        // ToLookup (в отличие от ToDictionary) допускает null-ключ — это группы
        // корневого уровня (ParentId == null) — и возвращает пустую последовательность
        // для отсутствующих ключей. Сортируем по Position до группировки: порядок
        // внутри каждого уровня сохраняется.
        var byParent = groups.OrderBy(g => g.Position).ToLookup(g => g.ParentId);

        var result = new List<(string Label, int? Id)> { ("Корневой уровень", null) };

        void AddChildren(int? parentId, string prefix)
        {
            foreach (var g in byParent[parentId])
            {
                if (g.Id is int gid && excludeSubtreeRoots is not null && excludeSubtreeRoots.Contains(gid))
                    continue; // группа и все под ней (рекурсия в нее не заходит)
                var label = prefix.Length == 0 ? g.Name : $"{prefix} / {g.Name}";
                result.Add((label, g.Id));
                AddChildren(g.Id, label);
            }
        }
        AddChildren(null, "");
        return result;
    }
}
