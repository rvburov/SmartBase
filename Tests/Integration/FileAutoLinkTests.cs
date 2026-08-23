using System.Threading;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Авто-привязка файлов типа «файл»: сканирование папки и материализация путей по совпадению
/// «значение_текст-столбца.формат» (регистр учитывается, точное имя), поиск во вложенных
/// папках, реакция на правку столбца-источника, статус недоступной папки.
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
