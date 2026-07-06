using DataBase.Core.Importers.TableSources;

namespace DataBase.Core.Importers;

/// <summary>Единица импорта: одна будущая коллекция (файл, лист книги или файл из папки).</summary>
public sealed record ImportUnit(string CollectionName, string FilePath, Func<ITableSource> OpenSource);

/// <summary>
/// Разворачивает заявку диалога в плоский список юнитов «одна коллекция — один источник»:
/// одиночный файл, листы книги (в одну/разные коллекции) или все поддерживаемые файлы папки
/// (рекурсивно, включая подпапки; названия коллекций — из имён файлов, занятые имена
/// уникализирует репозиторий). Manual-параметры применяются только к Excel; CSV/JSON — всегда
/// по стандартным правилам. Ошибки — <see cref="ImportException"/>.
/// </summary>
public static class ImportPlanner
{
    private static readonly string[] ExcelExts = [".xlsx", ".xlsm", ".xlsb", ".xls"];
    private static readonly string[] AllExts   = [".xlsx", ".xlsm", ".xlsb", ".xls", ".csv", ".json"];

    /// <summary>Список поддерживаемых расширений для сообщений об ошибках.</summary>
    public const string SupportedList = ".xlsx, .xlsm, .xlsb, .xls, .csv, .json";

    public static bool IsSupported(string path) => AllExts.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static bool IsExcel(string path)     => ExcelExts.Contains(Path.GetExtension(path).ToLowerInvariant());

    // Разобранные исключения ручного режима — одни на все юниты заявки.
    private sealed record Exclusions(HashSet<string> Sheets, RangeSet Rows, RangeSet Cols);

    public static IReadOnlyList<ImportUnit> Plan(ImportSpec spec)
    {
        var titleCol = SourceHelpers.ParseColumn(spec.TitleColumn, "Столбец названия");
        var excl = new Exclusions(
            SourceHelpers.ParseSheetExclusions(spec.ExcludeSheets),
            SourceHelpers.ParseRowExclusions(spec.ExcludeRows),
            SourceHelpers.ParseColumnExclusions(spec.ExcludeColumns));
        if (excl.Cols.Contains(titleCol))
            throw new ImportException("Столбец названия нельзя исключать («Исключить столбцы»).");

        var units = new List<ImportUnit>();

        if (spec.FolderMode)
        {
            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(spec.Path, "*", SearchOption.AllDirectories)
                                 .Where(IsSupported)
                                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                 .ToList();
            }
            catch (Exception ex) when (ex is not ImportException)
            {
                throw ImportErrors.Map(ex, spec.Path);
            }

            if (files.Count == 0)
                throw new ImportException($"В папке нет поддерживаемых файлов ({SupportedList}).");

            foreach (var file in files)
                AddUnitsForFile(units, file, Path.GetFileNameWithoutExtension(file), spec, titleCol, excl,
                                lenientSheets: true);
        }
        else
        {
            AddUnitsForFile(units, spec.Path, spec.BaseName, spec, titleCol, excl, lenientSheets: false);
        }

        return units;
    }

    private static void AddUnitsForFile(List<ImportUnit> units, string file, string baseName,
        ImportSpec spec, int titleCol, Exclusions excl, bool lenientSheets)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();

        // CSV/JSON — всегда стандартные правила (первая строка — заголовки, первый столбец —
        // название); исключения ручного режима на них не действуют.
        switch (ext)
        {
            case ".csv":
                units.Add(new ImportUnit(baseName, file, () => new CsvTableSource(file, 1, 2, 1)));
                return;
            case ".json":
                units.Add(new ImportUnit(baseName, file, () => new JsonTableSource(file)));
                return;
        }

        if (!ExcelExts.Contains(ext))
            throw new ImportException($"Формат файла не поддерживается. Доступны: {SupportedList}.");

        switch (spec.Sheets)
        {
            case SheetMode.MergeAll:
                units.Add(new ImportUnit(baseName, file,
                    () => new ExcelMergedSource(file, spec.HeaderRow, spec.DataRow, titleCol,
                                                excl.Rows, excl.Cols, excl.Sheets)));
                return;

            case SheetMode.SplitAll:
            {
                List<string>? sheets;
                try { sheets = ExcelTableSource.SheetNames(file); }
                catch (Exception) when (lenientSheets)
                {
                    // Нечитаемый файл в папке: ошибка всплывёт на его юните (файлы до него успеют
                    // импортироваться), а не завалит весь импорт ещё на планировании.
                    sheets = null;
                }
                if (sheets is null)
                {
                    units.Add(new ImportUnit(baseName, file,
                        () => new ExcelTableSource(file, null, spec.HeaderRow, spec.DataRow, titleCol,
                                                   excl.Rows, excl.Cols, excl.Sheets)));
                    return;
                }

                sheets = sheets.Where(s => !excl.Sheets.Contains(s.Trim())).ToList();
                if (sheets.Count == 0)
                {
                    // В папке файл, у которого исключены все листы, просто пропускается.
                    if (lenientSheets) return;
                    throw new ImportException("Все листы исключены — нет данных для импорта.");
                }
                foreach (var sheet in sheets)
                    units.Add(new ImportUnit(sheets.Count == 1 ? baseName : $"{baseName} — {sheet}", file,
                        () => new ExcelTableSource(file, sheet, spec.HeaderRow, spec.DataRow, titleCol,
                                                   excl.Rows, excl.Cols, excl.Sheets)));
                return;
            }

            default:
                units.Add(new ImportUnit(baseName, file,
                    () => new ExcelTableSource(file, spec.Manual ? spec.SheetName : null,
                                               spec.HeaderRow, spec.DataRow, titleCol,
                                               excl.Rows, excl.Cols, excl.Sheets)));
                return;
        }
    }
}
