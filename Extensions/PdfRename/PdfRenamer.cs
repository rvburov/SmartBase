using System.IO;
using System.Threading;

namespace DataBase.Extensions.PdfRename;

/// <summary>Что произойдёт (или произошло) с конкретным файлом.</summary>
public enum RenameOutcome
{
    /// <summary>Будет переименован (режим проверки).</summary>
    WillRename,
    /// <summary>Переименован на диске.</summary>
    Renamed,
    /// <summary>Имя не меняется — пропуск.</summary>
    Unchanged,
    /// <summary>Конфликт: целевое имя занято или совпадает у нескольких файлов.</summary>
    Conflict,
    /// <summary>Ошибка файловой операции.</summary>
    Error,
}

/// <summary>Один шаг плана: исходное имя → целевое имя + исход.</summary>
public sealed record RenameItem(string OldName, string NewName, RenameOutcome Outcome, string? Note = null);

/// <summary>Прогресс по ходу обработки (текущий из общего + только что обработанный элемент).</summary>
public sealed record RenameProgress(int Current, int Total, RenameItem Item);

/// <summary>Итоги прогона.</summary>
public sealed record RenameSummary(
    int Total, int Renamed, int Planned, int Unchanged, int Conflicts, int Errors, bool Cancelled);

/// <summary>
/// Пакетное переименование PDF-файлов: имя обрезается по первому пробелу (берётся всё
/// до него), расширение <c>.pdf</c> сохраняется. Чистая логика без UI — окно лишь
/// вызывает <see cref="Run"/>, репортя прогресс и отменяясь по токену. Поддерживаются
/// отмена через <see cref="CancellationToken"/>, обнаружение
/// внутрипакетных конфликтов (несколько файлов → одно имя), кросс-платформенный фильтр.
/// </summary>
public static class PdfRenamer
{
    public const string Extension = ".pdf";

    private static readonly StringComparer NameCmp = StringComparer.OrdinalIgnoreCase;

    /// <summary>Целевое имя по правилу «всё до первого пробела + .pdf».</summary>
    public static string Transform(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var head = stem.Split(' ', 2)[0];      // часть до первого пробела (или всё имя)
        return head + Extension;
    }

    /// <summary>Список PDF-файлов в папке (верхний уровень, регистр расширения не важен).</summary>
    public static List<string> ListPdfFiles(string folder) =>
        Directory.EnumerateFiles(folder)
                 .Where(p => NameCmp.Equals(Path.GetExtension(p), Extension))
                 .Select(Path.GetFileName)
                 .Where(n => n is not null)
                 .Select(n => n!)
                 .OrderBy(n => n, NameCmp)
                 .ToList();

    /// <summary>
    /// Построить план без изменений на диске (режим проверки) — для каждого файла исход:
    /// будет переименован / без изменений / конфликт.
    /// </summary>
    public static List<RenameItem> BuildPlan(string folder)
    {
        var files = ListPdfFiles(folder);

        // Целевые имена тех файлов, которые реально меняют имя, — для поиска
        // внутрипакетных коллизий (несколько разных файлов метят в одно имя).
        var targetCounts = new Dictionary<string, int>(NameCmp);
        foreach (var name in files)
        {
            var target = Transform(name);
            if (!NameCmp.Equals(target, name))
                targetCounts[target] = targetCounts.GetValueOrDefault(target) + 1;
        }

        var plan = new List<RenameItem>(files.Count);
        foreach (var name in files)
        {
            var target = Transform(name);

            if (NameCmp.Equals(target, Extension))                 // пустая «голова» имени
                plan.Add(new RenameItem(name, target, RenameOutcome.Conflict, "имя стало бы пустым"));
            else if (NameCmp.Equals(target, name))                 // менять нечего
                plan.Add(new RenameItem(name, target, RenameOutcome.Unchanged, "имя не изменилось"));
            else if (targetCounts.GetValueOrDefault(target) > 1)   // коллизия внутри пакета
                plan.Add(new RenameItem(name, target, RenameOutcome.Conflict, "несколько файлов → одно имя"));
            else if (File.Exists(Path.Combine(folder, target)))    // занято на диске
                plan.Add(new RenameItem(name, target, RenameOutcome.Conflict, "файл уже существует"));
            else
                plan.Add(new RenameItem(name, target, RenameOutcome.WillRename));
        }
        return plan;
    }

    /// <summary>
    /// Прогон по <b>готовому</b> плану (его строит <see cref="BuildPlan"/> заранее — один
    /// раз и вне UI-потока). Если <paramref name="dryRun"/> = false, применяет план:
    /// переименовывает файлы на диске. Репортит каждый шаг через <paramref name="progress"/>,
    /// прерывается по <paramref name="ct"/>. Возвращает итоговую сводку.
    /// </summary>
    public static RenameSummary Process(
        string folder, IReadOnlyList<RenameItem> plan, bool dryRun,
        IProgress<RenameProgress>? progress, CancellationToken ct)
    {
        int total = plan.Count;
        int renamed = 0, planned = 0, unchanged = 0, conflicts = 0, errors = 0;
        bool cancelled = false;

        for (int i = 0; i < plan.Count; i++)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }

            var item = plan[i];

            switch (item.Outcome)
            {
                case RenameOutcome.Unchanged: unchanged++; break;
                case RenameOutcome.Conflict:  conflicts++; break;

                case RenameOutcome.WillRename when dryRun:
                    planned++;
                    break;

                case RenameOutcome.WillRename:  // реальное переименование
                    try
                    {
                        File.Move(Path.Combine(folder, item.OldName), Path.Combine(folder, item.NewName));
                        item = item with { Outcome = RenameOutcome.Renamed };
                        renamed++;
                    }
                    catch (Exception ex)
                    {
                        item = item with { Outcome = RenameOutcome.Error, Note = ex.Message };
                        errors++;
                    }
                    break;
            }

            progress?.Report(new RenameProgress(i + 1, total, item));
        }

        return new RenameSummary(total, renamed, planned, unchanged, conflicts, errors, cancelled);
    }
}
