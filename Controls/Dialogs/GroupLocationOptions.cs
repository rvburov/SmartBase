using DataBase.Core.Models;

namespace DataBase.Controls.Dialogs;

// Построение вариантов выпадающего списка «Расположение» с полным путём вложенности
// групп — «1», «1 / 1», «1 (копия) / 2» … — чтобы одноимённые группы на разных
// уровнях были различимы.
internal static class GroupLocationOptions
{
    // groups — все группы; excludeSubtreeRootId — корень поддерева, исключаемого из
    // вариантов (для перемещения группы: нельзя вложить её в саму себя или потомка).
    public static List<(string Label, int? Id)> Build(
        IEnumerable<DbGroup> groups, int? excludeSubtreeRootId = null)
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
                if (excludeSubtreeRootId is int ex && g.Id == ex) continue; // группа и всё под ней
                var label = prefix.Length == 0 ? g.Name : $"{prefix} / {g.Name}";
                result.Add((label, g.Id));
                AddChildren(g.Id, label);
            }
        }
        AddChildren(null, "");
        return result;
    }
}
