using System.Threading;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Авто-привязка файлов типа «файл»: сканирование папки и материализация путей по совпадению
/// «значение_текст-столбца.формат» либо частичному имени с тем же форматом и регистром,
/// все частичные совпадения, поиск во вложенных папках, реакция на правку источника.
/// </summary>
public sealed class FileAutoLinkTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sb_fileauto_" + Guid.NewGuid().ToString("N"));

    public FileAutoLinkTests()
    {
        Directory.CreateDirectory(_folder);
        // На регистронезависимой ФС нельзя держать «Отчет.pdf» и «отчет.pdf» одновременно —
        // создаем только один регистр и проверяем строгое совпадение.
        File.WriteAllText(Path.Combine(_folder, "Отчет.pdf"),  "x");
        File.WriteAllText(Path.Combine(_folder, "Отчет.xlsx"), "x");   // тот же base, другой формат
        File.WriteAllText(Path.Combine(_folder, "Смета.pdf"),  "x");
        // Файл во вложенной папке — для проверки рекурсивного режима.
        Directory.CreateDirectory(Path.Combine(_folder, "Архив", "2026"));
        File.WriteAllText(Path.Combine(_folder, "Архив", "2026", "Акт.pdf"), "x");
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    private static string FileVal(ConstructorRepository repo, int recordId, int fieldId) =>
        repo.GetFieldValuesBatch([recordId]).TryGetValue(recordId, out var m)
            && m.TryGetValue(fieldId, out var v) ? v : "";   // запись без значений вовсе → ""

    private string MakeFile(string relativePath)
    {
        var path = Path.Combine(_folder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private static string[] SortedPaths(params string[] paths) =>
        paths.OrderBy(path => path, StringComparer.Ordinal).ToArray();

    [Fact]
    public void Resolve_materializes_exact_case_sensitive_matches_by_format()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo);
        var src  = SampleData.AddField(repo, col.Id!.Value, "Имя", "text");
        var file = SampleData.AddField(repo, col.Id!.Value, "Файл", "file");
        var r1 = SampleData.AddRecord(repo, col.Id!.Value, "R1");
        var r2 = SampleData.AddRecord(repo, col.Id!.Value, "R2");
        var r3 = SampleData.AddRecord(repo, col.Id!.Value, "R3");
        repo.SetFieldValue(r1.Id!.Value, src.Id!.Value, "Отчет");   // → Отчет.pdf
        repo.SetFieldValue(r2.Id!.Value, src.Id!.Value, "отчет");   // регистр иной → не найдено
        repo.SetFieldValue(r3.Id!.Value, src.Id!.Value, "Нет");     // файла нет

        file.ConfigJson = new FileAutoRule(src.Id!.Value.ToString(), _folder, "pdf").ToConfigJson();
        repo.UpdateField(file);
        var result = repo.ResolveAutoFileField(file.Id!.Value);

        Assert.Equal(ConstructorRepository.FileAutoResolveResult.Ok, result);
        Assert.EndsWith("Отчет.pdf", FileVal(repo, r1.Id!.Value, file.Id!.Value));  // формат pdf, не xlsx
        Assert.Equal("", FileVal(repo, r2.Id!.Value, file.Id!.Value));             // регистр — нет
        Assert.Equal("", FileVal(repo, r3.Id!.Value, file.Id!.Value));             // нет файла
    }

    [Fact]
    public void Resolve_overwrites_existing_values_and_reports_inaccessible_folder()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo);
        var src  = SampleData.AddField(repo, col.Id!.Value, "Имя", "text");
        var file = SampleData.AddField(repo, col.Id!.Value, "Файл", "file");
        var r1 = SampleData.AddRecord(repo, col.Id!.Value, "R1");
        repo.SetFieldValue(r1.Id!.Value, src.Id!.Value, "Смета");
        repo.SetFieldValue(r1.Id!.Value, file.Id!.Value, "/старый/путь/вручную.pdf");   // ручное значение

        // Применение правила затирает ручное значение авто-подстановкой.
        file.ConfigJson = new FileAutoRule(src.Id!.Value.ToString(), _folder, "pdf").ToConfigJson();
        repo.UpdateField(file);
        repo.ResolveAutoFileField(file.Id!.Value);
        Assert.EndsWith("Смета.pdf", FileVal(repo, r1.Id!.Value, file.Id!.Value));

        // Недоступная папка → статус, значения не трогаем.
        file.ConfigJson = new FileAutoRule(src.Id!.Value.ToString(), Path.Combine(_folder, "нет-такой"), "pdf").ToConfigJson();
        repo.UpdateField(file);
        Assert.Equal(ConstructorRepository.FileAutoResolveResult.FolderInaccessible,
                     repo.ResolveAutoFileField(file.Id!.Value));
    }

    /// <summary>
    /// Вложенные папки: без флага виден только верхний уровень, с флагом — все дерево.
    /// Проверяются оба пути подстановки: bulk-резолв и реактивная правка источника.
    /// </summary>
    [Fact]
    public void Recursive_rule_finds_files_in_subfolders()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo);
        var src  = SampleData.AddField(repo, col.Id!.Value, "Имя", "text");
        var file = SampleData.AddField(repo, col.Id!.Value, "Файл", "file");
        var r    = SampleData.AddRecord(repo, col.Id!.Value, "R");
        repo.SetFieldValue(r.Id!.Value, src.Id!.Value, "Акт");   // лежит в Архив/2026

        // Только верхний уровень — вложенный файл не виден.
        file.ConfigJson = new FileAutoRule(src.Id!.Value.ToString(), _folder, "pdf").ToConfigJson();
        repo.UpdateField(file);
        Assert.Equal(ConstructorRepository.FileAutoResolveResult.Ok, repo.ResolveAutoFileField(file.Id!.Value));
        Assert.Equal("", FileVal(repo, r.Id!.Value, file.Id!.Value));

        // С поиском по вложенным папкам — найден.
        file.ConfigJson = new FileAutoRule(src.Id!.Value.ToString(), _folder, "pdf", Recursive: true).ToConfigJson();
        repo.UpdateField(file);
        Assert.Equal(ConstructorRepository.FileAutoResolveResult.Ok, repo.ResolveAutoFileField(file.Id!.Value));
        Assert.EndsWith(Path.Combine("Архив", "2026", "Акт.pdf"), FileVal(repo, r.Id!.Value, file.Id!.Value));

        // Точечный пересчет по правке источника тоже ходит вглубь.
        repo.SetFieldValue(r.Id!.Value, src.Id!.Value, "Смета");     // верхний уровень
        Assert.EndsWith("Смета.pdf", FileVal(repo, r.Id!.Value, file.Id!.Value));
        repo.SetFieldValue(r.Id!.Value, src.Id!.Value, "Акт");       // снова вложенный
        Assert.EndsWith(Path.Combine("Архив", "2026", "Акт.pdf"), FileVal(repo, r.Id!.Value, file.Id!.Value));
    }

    [Fact]
    public void Editing_source_value_reresolves_file_reactively()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo);
        var src  = SampleData.AddField(repo, col.Id!.Value, "Имя", "text");
        var file = SampleData.AddField(repo, col.Id!.Value, "Файл", "file");
        file.ConfigJson = new FileAutoRule(src.Id!.Value.ToString(), _folder, "pdf").ToConfigJson();
        repo.UpdateField(file);
        var r = SampleData.AddRecord(repo, col.Id!.Value, "R");

        // Правка источника пересчитывает файл сразу (реактивно, без bulk-резолва).
        repo.SetFieldValue(r.Id!.Value, src.Id!.Value, "Отчет");
        Assert.EndsWith("Отчет.pdf", FileVal(repo, r.Id!.Value, file.Id!.Value));

        repo.SetFieldValue(r.Id!.Value, src.Id!.Value, "Смета");
        Assert.EndsWith("Смета.pdf", FileVal(repo, r.Id!.Value, file.Id!.Value));

        repo.SetFieldValue(r.Id!.Value, src.Id!.Value, "Нет");
        Assert.Equal("", FileVal(repo, r.Id!.Value, file.Id!.Value));   // нет файла → очищено
    }

    [Fact]
    public void SetFieldValue_returns_auto_file_side_effects_for_live_update()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo);
        var src  = SampleData.AddField(repo, col.Id!.Value, "Имя", "text");
        var file = SampleData.AddField(repo, col.Id!.Value, "Файл", "file");
        file.ConfigJson = new FileAutoRule(src.Id!.Value.ToString(), _folder, "pdf").ToConfigJson();
        repo.UpdateField(file);
        var r = SampleData.AddRecord(repo, col.Id!.Value, "R");

        // Правка источника возвращает побочное изменение файл-столбца (для мгновенного показа).
        var effects = repo.SetFieldValue(r.Id!.Value, src.Id!.Value, "Отчет");
        var change  = Assert.Single(effects);
        Assert.Equal(file.Id!.Value, change.FieldId);
        Assert.EndsWith("Отчет.pdf", change.Value);

        // Правка НЕ источника побочных изменений не дает.
        var other = SampleData.AddField(repo, col.Id!.Value, "Прочее", "text");
        Assert.Empty(repo.SetFieldValue(r.Id!.Value, other.Id!.Value, "что-то"));
    }

    [Fact]
    public void Resolve_by_record_title_source_materializes_and_reacts_to_rename()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo);
        var file = SampleData.AddField(repo, col.Id!.Value, "Файл", "file");
        // Источник — системная колонка «Название» (в db_field_values ее нет — читается из db_records).
        file.ConfigJson = new FileAutoRule(FileAutoRule.TitleSource, _folder, "pdf").ToConfigJson();
        repo.UpdateField(file);

        var r1 = SampleData.AddRecord(repo, col.Id!.Value, "Отчет");   // → Отчет.pdf
        var r2 = SampleData.AddRecord(repo, col.Id!.Value, "Нет");     // файла нет

        // Bulk-резолв материализует пути по названиям записей.
        Assert.Equal(ConstructorRepository.FileAutoResolveResult.Ok, repo.ResolveAutoFileField(file.Id!.Value));
        Assert.EndsWith("Отчет.pdf", FileVal(repo, r1.Id!.Value, file.Id!.Value));
        Assert.Equal("", FileVal(repo, r2.Id!.Value, file.Id!.Value));

        // Переименование записи реактивно пересчитывает ее файл (без bulk-резолва).
        var effects = repo.SetRecordTitle(r2.Id!.Value, "Смета");
        var change  = Assert.Single(effects);
        Assert.Equal(file.Id!.Value, change.FieldId);
        Assert.EndsWith("Смета.pdf", change.Value);
        Assert.EndsWith("Смета.pdf", FileVal(repo, r2.Id!.Value, file.Id!.Value));
    }

    [Fact]
    public async Task Partial_resolve_collects_all_matches_but_keeps_exact_mode_as_default()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo);
        var src = SampleData.AddField(repo, col.Id!.Value, "Имя", "text");
        var file = SampleData.AddField(repo, col.Id!.Value, "Файл", "file");
        var matching = SampleData.AddRecord(repo, col.Id!.Value, "Совпадения");
        var wrongCase = SampleData.AddRecord(repo, col.Id!.Value, "Другой регистр");
        var extensionOnly = SampleData.AddRecord(repo, col.Id!.Value, "Только расширение");
        var blank = SampleData.AddRecord(repo, col.Id!.Value, "Пустой источник");
        var whitespace = SampleData.AddRecord(repo, col.Id!.Value, "Пробелы");
        var signed = MakeFile("2026 Отчет, подписан.pdf");
        var copy = MakeFile("Отчет-копия.pdf");
        MakeFile("Отчет-другой.xlsx");
        MakeFile("Отчет-верх.PDF");
        MakeFile("Отчет.pdf.bak");
        repo.SetFieldValue(matching.Id!.Value, src.Id!.Value, "Отчет");
        repo.SetFieldValue(wrongCase.Id!.Value, src.Id!.Value, "отчет");
        repo.SetFieldValue(extensionOnly.Id!.Value, src.Id!.Value, "pdf");
        repo.SetFieldValue(whitespace.Id!.Value, src.Id!.Value, " \t ");
        repo.SetFieldValue(blank.Id!.Value, file.Id!.Value, signed);

        file.ConfigJson = new FileAutoRule(src.Id!.Value.ToString(), _folder, "pdf").ToConfigJson();
        repo.UpdateField(file);
        Assert.Equal(ConstructorRepository.FileAutoResolveResult.Ok, repo.ResolveAutoFileField(file.Id!.Value));
        Assert.Equal(Path.Combine(_folder, "Отчет.pdf"), FileVal(repo, matching.Id!.Value, file.Id!.Value));
        Assert.Equal(Path.Combine(_folder, "Отчет.pdf"),
                     repo.GetFieldValues(matching.Id!.Value)[file.Id!.Value]);

        file.ConfigJson = new FileAutoRule(src.Id!.Value.ToString(), _folder, "pdf", PartialMatch: true).ToConfigJson();
        repo.UpdateField(file);
        Assert.Equal(ConstructorRepository.FileAutoResolveResult.Ok,
                     await repo.ResolveAutoFileFieldAsync(file.Id!.Value));

        Assert.Equal(SortedPaths(signed, copy, Path.Combine(_folder, "Отчет.pdf")),
                     FileValue.ReadPaths(FileVal(repo, matching.Id!.Value, file.Id!.Value)));
        // После фонового пересчета оба читателя видят новые пути, даже на прежнем контексте.
        Assert.Equal(SortedPaths(signed, copy, Path.Combine(_folder, "Отчет.pdf")),
                     FileValue.ReadPaths(repo.GetFieldValues(matching.Id!.Value)[file.Id!.Value]));
        foreach (var record in new[] { wrongCase, extensionOnly, blank, whitespace })
            Assert.Equal("", FileVal(repo, record.Id!.Value, file.Id!.Value));
    }

    [Fact]
    public void Recursive_partial_rule_keeps_equal_names_in_different_subfolders()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo);
        var src = SampleData.AddField(repo, col.Id!.Value, "Имя", "text");
        var file = SampleData.AddField(repo, col.Id!.Value, "Файл", "file");
        var record = SampleData.AddRecord(repo, col.Id!.Value, "R");
        var top = MakeFile("Акт-копия.pdf");
        var first = MakeFile(Path.Combine("Архив", "2026", "Акт-копия.pdf"));
        var second = MakeFile(Path.Combine("Другая", "Акт-копия.pdf"));
        repo.SetFieldValue(record.Id!.Value, src.Id!.Value, "Акт");

        file.ConfigJson = new FileAutoRule(src.Id!.Value.ToString(), _folder, "pdf", PartialMatch: true).ToConfigJson();
        repo.UpdateField(file);
        Assert.Equal(ConstructorRepository.FileAutoResolveResult.Ok, repo.ResolveAutoFileField(file.Id!.Value));
        Assert.Equal(top, FileVal(repo, record.Id!.Value, file.Id!.Value));

        file.ConfigJson = new FileAutoRule(src.Id!.Value.ToString(), _folder, "pdf",
                                         Recursive: true, PartialMatch: true).ToConfigJson();
        repo.UpdateField(file);
        Assert.Equal(ConstructorRepository.FileAutoResolveResult.Ok, repo.ResolveAutoFileField(file.Id!.Value));
        var expected = SortedPaths(top, first, second, Path.Combine(_folder, "Архив", "2026", "Акт.pdf"));
        Assert.Equal(expected, FileValue.ReadPaths(FileVal(repo, record.Id!.Value, file.Id!.Value)));

        // Имя папки не участвует в совпадении. Возврат к прежнему источнику проверяет и точечный путь.
        repo.SetFieldValue(record.Id!.Value, src.Id!.Value, "Архив");
        Assert.Equal("", FileVal(repo, record.Id!.Value, file.Id!.Value));
        repo.SetFieldValue(record.Id!.Value, src.Id!.Value, "Акт");
        Assert.Equal(expected, FileValue.ReadPaths(FileVal(repo, record.Id!.Value, file.Id!.Value)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Editing_text_or_title_replaces_the_whole_partial_match_list(bool titleSource)
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo);
        var src = SampleData.AddField(repo, col.Id!.Value, "Имя", "text");
        var file = SampleData.AddField(repo, col.Id!.Value, "Файл", "file");
        var copy = MakeFile("Отчет-приложение.pdf");
        var sourceKey = titleSource ? FileAutoRule.TitleSource : src.Id!.Value.ToString();
        file.ConfigJson = new FileAutoRule(sourceKey, _folder, "pdf", PartialMatch: true).ToConfigJson();
        repo.UpdateField(file);
        var record = SampleData.AddRecord(repo, col.Id!.Value, "R");

        IReadOnlyList<(int FieldId, string Value)> SetSource(string value) => titleSource
            ? repo.SetRecordTitle(record.Id!.Value, value)
            : repo.SetFieldValue(record.Id!.Value, src.Id!.Value, value);

        var effect = Assert.Single(SetSource("Отчет"));
        Assert.Equal(file.Id!.Value, effect.FieldId);
        Assert.Equal(SortedPaths(copy, Path.Combine(_folder, "Отчет.pdf")), FileValue.ReadPaths(effect.Value));
        Assert.Equal(effect.Value, FileVal(repo, record.Id!.Value, file.Id!.Value));

        effect = Assert.Single(SetSource("Смета"));
        Assert.Equal(Path.Combine(_folder, "Смета.pdf"), effect.Value);
        Assert.Equal(effect.Value, FileVal(repo, record.Id!.Value, file.Id!.Value));
        Assert.Single(SetSource(""));
        Assert.Equal("", FileVal(repo, record.Id!.Value, file.Id!.Value));
        SetSource(" \t ");
        Assert.Equal("", FileVal(repo, record.Id!.Value, file.Id!.Value));
    }

    [Fact]
    public void Bulk_paste_updates_partial_match_lists_for_text_and_title_sources()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo);
        var collectionId = col.Id!.Value;
        var src = SampleData.AddField(repo, collectionId, "Имя", "text");
        var byText = SampleData.AddField(repo, collectionId, "По тексту", "file");
        var byTitle = SampleData.AddField(repo, collectionId, "По названию", "file");
        byText.ConfigJson = new FileAutoRule(src.Id!.Value.ToString(), _folder, "pdf", PartialMatch: true).ToConfigJson();
        byTitle.ConfigJson = new FileAutoRule(FileAutoRule.TitleSource, _folder, "pdf", PartialMatch: true).ToConfigJson();
        repo.UpdateField(byText);
        repo.UpdateField(byTitle);
        // Создание записи теперь тоже использует снимок каталога: файлы сценария
        // должны существовать до первого автоматического поиска.
        var report = SortedPaths(MakeFile("Отчет-копия.pdf"), Path.Combine(_folder, "Отчет.pdf"));
        var estimate = SortedPaths(MakeFile("Смета-копия.pdf"), Path.Combine(_folder, "Смета.pdf"));
        var first = SampleData.AddRecord(repo, collectionId, "R1").Id!.Value;
        var second = SampleData.AddRecord(repo, collectionId, "R2").Id!.Value;

        var result = repo.SetCellsBulk(collectionId,
            [new(first, src.Id!.Value, "Отчет"), new(second, src.Id!.Value, "Смета")],
            [new(first, "Смета"), new(second, "Отчет")]);

        Assert.Equal(4, result.Changed);
        Assert.Equal(4, result.AutoFileChanges.Count);
        var effects = result.AutoFileChanges.ToDictionary(change => (change.RecordId, change.FieldId), change => change.Value);
        foreach (var (recordId, fieldId, paths) in new[]
                 {
                     (first, byText.Id!.Value, report), (first, byTitle.Id!.Value, estimate),
                     (second, byText.Id!.Value, estimate), (second, byTitle.Id!.Value, report),
                 })
        {
            Assert.Equal(paths, FileValue.ReadPaths(FileVal(repo, recordId, fieldId)));
            Assert.Equal(paths, FileValue.ReadPaths(effects[(recordId, fieldId)]));
        }

        var cleared = repo.SetCellsBulk(collectionId, [new(first, src.Id!.Value, "")], [new(second, "")]);
        Assert.Equal(2, cleared.AutoFileChanges.Count);
        Assert.Equal("", FileVal(repo, first, byText.Id!.Value));
        Assert.Equal("", FileVal(repo, second, byTitle.Id!.Value));
        Assert.Equal(estimate, FileValue.ReadPaths(FileVal(repo, first, byTitle.Id!.Value)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rescan_can_remove_a_tracked_link_and_recreate_it_after_a_source_edit(bool background)
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo);
        var file = SampleData.AddField(repo, col.Id!.Value, "Файл", "file");
        file.ConfigJson = new FileAutoRule(FileAutoRule.TitleSource, _folder, "pdf", PartialMatch: true).ToConfigJson();
        repo.UpdateField(file);
        var oldPath = MakeFile("Старый Отчет.pdf");
        var record = SampleData.AddRecord(repo, col.Id!.Value, "Черновик");
        repo.SetRecordTitle(record.Id!.Value, "Старый");
        Assert.Equal(oldPath, repo.GetFieldValues(record.Id!.Value)[file.Id!.Value]);

        File.Delete(oldPath);
        var newPath = MakeFile("Новый Отчет.pdf");
        var result = background ? await repo.ResolveAutoFileFieldAsync(file.Id!.Value)
                                : repo.ResolveAutoFileField(file.Id!.Value);
        Assert.Equal(ConstructorRepository.FileAutoResolveResult.Ok, result);
        Assert.False(repo.GetFieldValues(record.Id!.Value).ContainsKey(file.Id!.Value));

        var effect = Assert.Single(repo.SetRecordTitle(record.Id!.Value, "Новый"));
        Assert.Equal(file.Id!.Value, effect.FieldId);
        Assert.Equal(newPath, effect.Value);
        Assert.Equal(newPath, repo.GetFieldValues(record.Id!.Value)[file.Id!.Value]);
    }

    [Fact]
    public async Task Cancelled_resolve_leaves_values_unchanged()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo);
        var src  = SampleData.AddField(repo, col.Id!.Value, "Имя", "text");
        var file = SampleData.AddField(repo, col.Id!.Value, "Файл", "file");
        var r = SampleData.AddRecord(repo, col.Id!.Value, "R");
        repo.SetFieldValue(r.Id!.Value, src.Id!.Value, "Отчет");   // совпало бы с Отчет.pdf

        file.ConfigJson = new FileAutoRule(src.Id!.Value.ToString(), _folder, "pdf").ToConfigJson();
        repo.UpdateField(file);

        // Отмененный заранее токен → операция не трогает значения, статус Cancelled.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await repo.ResolveAutoFileFieldAsync(file.Id!.Value, null, cts.Token);

        Assert.Equal(ConstructorRepository.FileAutoResolveResult.Cancelled, result);
        Assert.Equal("", FileVal(repo, r.Id!.Value, file.Id!.Value));   // ничего не записано
    }
}
