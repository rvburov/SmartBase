using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SmartBase.Core.Documents;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.DocIndex;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Benchmarks;

/// <summary>
/// Замер ленты «Библиотеки документов» на большой библиотеке: чем платит раздел за открытие,
/// касание галочки, смену типа и прокрутку.
///
/// Зачем: до этого замера раздел мерили только чтением текста (<c>docread</c>), а все, что
/// происходит при КАЖДОЙ перезагрузке ленты, — счет документов, страницы в глубине, перепись
/// кеша с обращением к диску, — не мерил никто. Генераторы стенда поле «Файл» в раздел не
/// включают, поэтому библиотека здесь строится своя.
///
/// Библиотека генерируется один раз и остается рядом (<c>--db</c>), повторный запуск ее
/// переиспользует — сравнивать «до» и «после» правки надо на одном и том же файле.
/// С <c>--files N</c> первые N документов получают настоящие файлы на диске, и меряется
/// перепись (<c>DocTextIndex.Survey</c>): без файлов ей нечего спрашивать у диска. С
/// <c>--root</c> файлы кладутся в заданную папку — например, на сетевую шару
/// (<c>\\localhost\C$\…</c>), чтобы увидеть цену вопроса к диску по сети.
///
/// <b>Не мерить в папке с коротким именем.</b> Если в пути есть компонент вида
/// <c>RV4F61~1.BUR</c> (так Windows отдает <c>%TEMP%</c> профилям с точкой в имени),
/// <c>Path.GetFullPath</c> разворачивает его, спрашивая диск, — на каждом пути. Перепись там
/// выходит вдесятеро дороже, и замер меряет не ее. Поэтому библиотека по умолчанию лежит в
/// <c>~/TestData</c>, как стенды остальных замеров.
/// </summary>
public static class DocFeedBench
{
    private const int TypeCount = 10;

    public static int Run(string[] args)
    {
        SQLitePCL.Batteries_V2.Init();

        var docs  = BenchUtil.ArgInt(args, "--docs")  ?? 20_000;
        var cols  = BenchUtil.ArgInt(args, "--cols")  ?? 4;
        var files = BenchUtil.ArgInt(args, "--files") ?? 0;
        var runs  = BenchUtil.ArgInt(args, "--runs")  ?? 3;
        var home  = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path  = BenchUtil.ArgStr(args, "--db")
                    ?? Path.Combine(home, "TestData", $"docfeed-{docs}x{cols}.db");
        var fileDir = BenchUtil.ArgStr(args, "--root")
                      ?? Path.Combine(Path.GetDirectoryName(path)!, $"sb-docfeed-files-{docs}x{cols}");

        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Console.WriteLine($"Генерирую библиотеку: {docs:N0} документов в {cols} коллекциях → {path}");
            var sw = Stopwatch.StartNew();
            Generate(path, docs, cols, fileDir);
            Console.WriteLine($"  готово за {BenchUtil.Fmt(sw.ElapsedMilliseconds)}");
        }
        else Console.WriteLine($"Библиотека: {path} (переиспользуется)");
        Console.WriteLine();

        using var mgr = DatabaseManager.Open(0, "docfeed", path);
        var repo = mgr.Constructor;

        var stats  = repo.GetDocCollectionStats();
        var total  = stats.Values.Sum(s => s.Total);
        var biggest = stats.OrderByDescending(p => p.Value.Total).First();
        var colId  = biggest.Key;
        var typeId = biggest.Value.TypeFieldId ?? 0;
        var docsInCol = biggest.Value.Total;
        Console.WriteLine($"Документов: {total:N0}, в крупнейшей коллекции {docsInCol:N0}");
        Console.WriteLine();

        var all    = ConstructorRepository.DocView.All;
        var narrow = new ConstructorRepository.DocView(new HashSet<string> { "Тип 1", "Тип 2", "Тип 3" });

        Console.WriteLine("── Размеры (на каждую перезагрузку ленты) ──");
        Time("GetDocCollectionStats, все типы",       runs, () => repo.GetDocCollectionStats(null, all));
        Time("GetDocCollectionStats, три типа из 10", runs, () => repo.GetDocCollectionStats(null, narrow));
        Time("GetDocTypeCounts (выпадашка типов)",    runs, () => repo.GetDocTypeCounts());
        Console.WriteLine();

        Console.WriteLine("── Страница 200 карточек крупнейшей коллекции ──");
        foreach (var (label, skip) in new[]
                 {
                     ("начало",   0),
                     ("середина", docsInCol / 2),
                     ("конец",    Math.Max(0, docsInCol - 200)),
                 })
            Time($"GetDocPage OFFSET, {label}", runs,
                 () => repo.GetDocPage(colId, typeId, all, skip, 200));
        Time("GetDocPage OFFSET, три типа, конец", runs,
             () => repo.GetDocPage(colId, typeId, narrow, Math.Max(0, docsInCol * 3 / 10 - 200), 200));
        Console.WriteLine();

        Console.WriteLine("── Поиск и пути ──");
        Time("SearchDocs по названию",            runs, () => repo.SearchDocs(null, "Документ 1234", []));
        Time("GetDocPaths (перепись, чтение)",    runs, () => repo.GetDocPaths(null, int.MaxValue));
        Console.WriteLine();

        if (files > 0)
        {
            MakeFiles(repo.GetDocPaths(null, int.MaxValue).Take(files));
            Census(repo, runs);
        }

        return 0;
    }

    // ── Перепись кеша ─────────────────────────────────────────────────────────

    private static void Census(ConstructorRepository repo, int runs)
    {
        var paths = repo.GetDocPaths(null, int.MaxValue);
        var onDisk = paths.Where(File.Exists).ToList();
        Console.WriteLine($"── Перепись кеша: {paths.Count:N0} путей, на диске {onDisk.Count:N0} ──");

        var indexPath = Path.Combine(Path.GetTempPath(), $"sb-docfeed-index-{Guid.NewGuid():N}.db");
        try
        {
            var index = new DocTextIndex(indexPath);
            // Половина прочитана — обычное состояние библиотеки, по которой уже искали.
            foreach (var p in onDisk.Where((_, i) => i % 2 == 0))
                index.Put(p, new DocText(DocTextStatus.Ok, "текст документа", Pages: 1));

            Time("Survey — перепись целиком",          runs, () => index.Survey(paths));
            Time("чтение кеша (Known)",                runs, () => index.Known([.. paths.Select(DocTextIndex.KeyOf)]));
            Time("диск по файлу (как было)",           runs, () =>
            {
                foreach (var p in paths) DocStamps.Of(p);
            });
            Time("диск набором (DocStamps)",           runs, () => DocStamps.Of(paths));

            // Карточки страницы ленты: двести документов подряд, как их спрашивает фон.
            var page = repo.GetDocPage(repo.GetDocCollectionStats().Keys.Min(), 0,
                                       ConstructorRepository.DocView.All, 0, 200);
            Time("страница 200 карточек по файлу",     runs, () =>
            {
                foreach (var d in page) DocStamps.Of(d.Path);
            });
            Time("страница 200 карточек (Probe)",      runs, () => DocStamps.Probe(page));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var s in new[] { "", "-wal", "-shm" })
                try { File.Delete(indexPath + s); } catch { /* временный файл */ }
        }
        Console.WriteLine();
    }

    // ── Генерация ────────────────────────────────────────────────────────────

    private static void Generate(string path, int docs, int cols, string fileDir)
    {
        var shells = new List<(int ColId, int FileId, int TypeId, int[] Text)>();
        using (var mgr = DatabaseManager.Open(0, "docfeed", path))
        {
            var repo = mgr.Constructor;
            for (var c = 0; c < cols; c++)
            {
                var colId = repo.AddCollection(new DbCollection { Name = $"Документы {c + 1}" }).Id!.Value;
                var file  = repo.AddField(new DbField { CollectionId = colId, Name = "Файл", FieldType = "file" }).Id!.Value;
                repo.SetFileFieldInLibrary(file, true);
                var text = Enumerable.Range(0, 3)
                    .Select(k => repo.AddField(new DbField
                    {
                        CollectionId = colId, Name = $"Поле {k + 1}", FieldType = "text", Position = 2000 + k,
                    }).Id!.Value)
                    .ToArray();
                shells.Add((colId, file, repo.DocTypeFieldId(colId)!.Value, text));
            }
        }

        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using (var p = conn.CreateCommand())
        {
            p.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=OFF; PRAGMA cache_size=-262144;";
            p.ExecuteNonQuery();
        }

        using var tx = conn.BeginTransaction();
        using var rec = conn.CreateCommand();
        rec.Transaction = tx;
        rec.CommandText = """
            INSERT INTO db_records (CollectionId, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy, Position, AllDay, Title)
            VALUES (@c, '2026-01-01T00:00:00', '2026-01-01T00:00:00', '', '', @pos, 0, @t);
            SELECT last_insert_rowid();
            """;
        var pc = rec.Parameters.Add("@c", SqliteType.Integer);
        var pp = rec.Parameters.Add("@pos", SqliteType.Integer);
        var pt = rec.Parameters.Add("@t", SqliteType.Text);

        using var val = conn.CreateCommand();
        val.Transaction = tx;
        val.CommandText = "INSERT INTO db_field_values (RecordId, FieldId, Value) VALUES (@r, @f, @v)";
        var vr = val.Parameters.Add("@r", SqliteType.Integer);
        var vf = val.Parameters.Add("@f", SqliteType.Integer);
        var vv = val.Parameters.Add("@v", SqliteType.Text);

        void Value(long record, int field, string value)
        {
            vr.Value = record; vf.Value = field; vv.Value = value;
            val.ExecuteNonQuery();
        }

        var perCol = (docs + cols - 1) / cols;
        for (var i = 0; i < docs; i++)
        {
            var shell = shells[i / perCol];
            pc.Value = shell.ColId;
            pp.Value = (i % perCol + 1) * 10;
            pt.Value = $"Документ {i + 1}";
            var id = (long)rec.ExecuteScalar()!;

            // Файл в папке по двести — как лежат документы в настоящих библиотеках.
            Value(id, shell.FileId, Path.Combine(fileDir, $"d{i / 200:D4}", $"doc{i + 1:D7}.pdf"));
            // Десятая часть без типа — это «(Пусто)» в выпадашке.
            if (i % 10 != 9) Value(id, shell.TypeId, $"Тип {i % TypeCount + 1}");
            foreach (var f in shell.Text) Value(id, f, $"значение {f}-{i}");
        }
        tx.Commit();
    }

    /// <summary>Создать сами файлы — только для замера переписи.</summary>
    public static void MakeFiles(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            if (!File.Exists(p)) File.WriteAllBytes(p, [0x25, 0x50, 0x44, 0x46]);
        }
    }

    // Медиана из нескольких прогонов после одного прогревочного: первое обращение к файлу
    // базы меряет холодный кеш ОС, а не запрос.
    private static void Time(string label, int runs, Action action)
    {
        action();
        var ms = new List<double>();
        for (var r = 0; r < runs; r++)
        {
            var sw = Stopwatch.StartNew();
            action();
            ms.Add(sw.Elapsed.TotalMilliseconds);
        }
        ms.Sort();
        var median = ms[ms.Count / 2];
        var text   = median < 10 ? $"{median:0.0} мс" : BenchUtil.Fmt(median);
        Console.WriteLine($"  {label,-44} {text,10}");
    }
}
