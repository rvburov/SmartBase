using System.IO;
using SmartBase.Core.Models;

namespace SmartBase.Core.Documents;

/// <summary>
/// Отметки файлов на диске — время правки и размер. По ним кеш текста узнает, изменился ли
/// документ с прошлого разбора, кеш миниатюр — годится ли готовая картинка, а карточка — есть
/// ли файл вообще.
///
/// Одна отметка стоит одного вопроса к диску, и на локальном диске это копейки. Но библиотека
/// обычно живет на общей папке сервера, и там каждый вопрос — поход по сети: двадцать тысяч
/// документов по одному — это двадцать тысяч походов на каждый заход в раздел.
///
/// Поэтому набор путей спрашивается ПАПКАМИ: листинг отдает время и размер всех файлов папки
/// разом, за несколько заходов на сервер, а не за заход на каждый файл. Замер
/// (<c>Tests/Benchmarks docfeed</c>, 20 000 файлов по 200 в папке): по файлу — 769 мс на
/// локальном диске и 978 мс через сетевую шару, листингом — 58 и 198 мс. Шара там петлевая,
/// на той же машине, и Windows кеширует ее ответы; на настоящем сервере разрыв больше.
///
/// Листинг выгоден не всегда: из папки на сто тысяч файлов, где нужны два, дешевле спросить
/// эти два. Поэтому папка листается, только когда из нее нужно не меньше
/// <see cref="ListingThreshold"/> файлов, и листинг бросается, если прочитал в
/// <see cref="ListingBudget"/> раз больше записей, чем нужно файлов (но не меньше тысячи), а
/// нашел не все. Остальное
/// спрашивается по файлу, но в несколько потоков: сетевой диск держит много вопросов в полете,
/// и ждать каждый ответ по очереди незачем.
///
/// Чего листинг не нашел, всегда переспрашивается по файлу. Поэтому ответ набором совпадает с
/// ответом <see cref="Of(string)"/> для любого пути, а листинг остается только ускорением.
/// Имя сверяется ТОЧНО, с учетом регистра, и папки группируются так же: в папке,
/// чувствительной к регистру, «A.pdf» и «a.pdf» — разные файлы, и отдать отметку соседа было бы
/// тихой ошибкой. Путь, записанный не в том регистре, просто не найдется листингом и будет
/// спрошен по файлу, как раньше, — так же, как имя с хвостовой точкой или путь к папке.
/// </summary>
public static class DocStamps
{
    /// <summary>Со скольких нужных файлов папку выгоднее листать, чем спрашивать по одному.</summary>
    public const int ListingThreshold = 8;

    /// <summary>Во сколько раз листинг может прочитать больше записей, чем нужно файлов.</summary>
    public const int ListingBudget = 16;

    // Сколько вопросов держим в полете, когда спрашиваем по файлу. Больше восьми на петлевой
    // шаре уже не ускоряет (замер: 4 → 504 мс, 8 → 414, 16 → 526), а занимает пул потоков.
    private const int Parallelism = 8;

    /// <summary>Время правки (тики UTC) и размер файла; (0, −1) — файла нет или он недоступен.</summary>
    public static (long Mtime, long Size) Of(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc.Ticks, info.Length) : (0, -1);
        }
        catch { return (0, -1); }
    }

    /// <summary>
    /// Отметки набора путей. Ключ ответа — путь ровно в том написании, в каком его передали;
    /// повторы допустимы и спрашиваются один раз.
    /// </summary>
    public static Dictionary<string, (long Mtime, long Size)> Of(IEnumerable<string> paths, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var result  = new Dictionary<string, (long Mtime, long Size)>(StringComparer.Ordinal);
        var singles = new List<string>();

        foreach (var folder in paths.Distinct(StringComparer.Ordinal).GroupBy(FolderOf, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var wanted = folder.ToList();
            if (folder.Key.Length == 0 || wanted.Count < ListingThreshold)
            {
                singles.AddRange(wanted);
                continue;
            }
            singles.AddRange(List(folder.Key, wanted, result, ct));
        }

        var stamps = new (long Mtime, long Size)[singles.Count];
        Parallel.For(0, singles.Count, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
                     i => stamps[i] = Of(singles[i]));
        for (var i = 0; i < singles.Count; i++) result[singles[i]] = stamps[i];

        ct.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>
    /// Спросить у диска файлы документов — размер (он же «есть ли файл») и время правки — и
    /// записать ответ в сами документы. Зовется в фоне, сразу после чтения страницы ленты, чтобы
    /// карточка в UI-потоке собиралась из готового и к диску не ходила.
    /// </summary>
    public static void Probe(IReadOnlyCollection<DocRecord> docs)
    {
        var stamps = Of(docs.Where(d => d.Path.Length > 0).Select(d => d.Path));
        foreach (var d in docs)
        {
            (d.Mtime, d.SizeBytes) = d.Path.Length > 0 ? stamps[d.Path] : (0, -1);
            d.OnDiskKnown = true;
        }
    }

    // Папка пути в нормальном виде: одна и та же папка, записанная прямыми и обратными слешами,
    // должна листаться один раз, а не дважды.
    private static string FolderOf(string path)
    {
        try { return Path.GetDirectoryName(Path.GetFullPath(path)) ?? ""; }
        catch { return ""; }
    }

    /// <summary>
    /// Листинг одной папки: найденное кладется в <paramref name="result"/>, а пути, которых
    /// листинг не нашел или не дочитал, возвращаются — их спросят по одному.
    /// </summary>
    private static List<string> List(string folder, List<string> wanted,
                                     Dictionary<string, (long Mtime, long Size)> result, CancellationToken ct)
    {
        // Имя → пути с этим именем: одна папка, записанная двумя написаниями, дает два пути к
        // одному файлу, и отметку получают оба.
        var byName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var path in wanted)
        {
            var name = Path.GetFileName(path);
            if (!byName.TryGetValue(name, out var list)) byName[name] = list = [];
            list.Add(path);
        }

        var found  = new Dictionary<string, (long Mtime, long Size)>(StringComparer.Ordinal);
        var budget = Math.Max(1024, wanted.Count * ListingBudget);
        var seen   = 0;
        try
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
            {
                ct.ThrowIfCancellationRequested();
                if (++seen > budget) break;
                if (!byName.TryGetValue(file.Name, out var paths)) continue;

                // Время и размер пришли вместе с листингом: FileInfo из перечисления уже
                // заполнен, и обращение к свойствам диск больше не трогает.
                var stamp = (file.LastWriteTimeUtc.Ticks, file.Length);
                foreach (var p in paths) found[p] = stamp;
                if (found.Count == wanted.Count) break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Папку не дочитали (нет прав, сеть моргнула) — спросим все ее пути по одному,
            // как спрашивали до листинга.
            return wanted;
        }

        foreach (var (p, stamp) in found) result[p] = stamp;
        return [.. wanted.Where(p => !found.ContainsKey(p))];
    }
}
