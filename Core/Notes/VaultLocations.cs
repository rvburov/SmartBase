namespace SmartBase.Core.Notes;

/// <summary>
/// Варианты выпадающего списка «Расположение» — полный путь вложенности папок, чтобы одноименные
/// папки на разных уровнях были различимы («Работа / Отчеты» и «Личное / Отчеты»). Зеркало
/// <c>GroupLocationOptions</c> конструктора, только ключ — относительный путь, а не id.
/// </summary>
public static class VaultLocations
{
    public const string RootLabel = "Корень хранилища";

    /// <param name="folderRoots">Дерево ТОЛЬКО из папок — <see cref="VaultScanner.ScanFolders"/>.
    /// Ленивое дерево сайдбара сюда не годится: у нераскрытых папок нет детей, и половина
    /// хранилища не попала бы в список.</param>
    /// <param name="excludeSubtree">Относительный путь поддерева, которое нельзя предлагать целью:
    /// папку не переместить внутрь самой себя или своего потомка.</param>
    public static List<(string Label, string RelativePath)> Build(
        IEnumerable<NoteNode> folderRoots, string? excludeSubtree = null)
    {
        var result = new List<(string, string)> { (RootLabel, "") };
        Add(folderRoots, "");
        return result;

        void Add(IEnumerable<NoteNode> nodes, string prefix)
        {
            foreach (var n in nodes.Where(n => n.IsFolder))
            {
                // Папка и все под ней (рекурсия в нее не заходит).
                if (!string.IsNullOrEmpty(excludeSubtree) && VaultRules.IsInside(n.RelativePath, excludeSubtree))
                    continue;

                var label = prefix.Length == 0 ? n.Name : $"{prefix} / {n.Name}";
                result.Add((label, n.RelativePath));
                Add(n.Children, label);
            }
        }
    }
}
