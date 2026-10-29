namespace SmartBase.Integrations;

/// <summary>Папки обмена под одним корнем.</summary>
/// <param name="Root">Корень обмена — то, что человек выбирает на экране.</param>
/// <param name="Incoming">Куда сторонняя программа кладет файлы.</param>
/// <param name="Processed">Куда уезжает принятый файл, по месяцам.</param>
/// <param name="Failed">Куда уезжает файл, который принять не удалось.</param>
/// <param name="Receipts">Ответы отправителю; они же — память о принятых сообщениях.</param>
public sealed record InboxLayout(
    string Root,
    string Incoming,
    string Processed,
    string Failed,
    string Receipts);

/// <summary>Файл во «Входящих», готовый к разбору.</summary>
/// <param name="Path">Полный путь.</param>
/// <param name="Name">Имя файла — оно попадает в квитанцию, по нему отправитель узнает свой.</param>
public sealed record InboxFile(string Path, string Name);

/// <summary>
/// Папка-приемник как ПРОТОКОЛ, а не «читаем файлы из папки» (docs/ROADMAP-API.md, раздел 8).
/// Четыре подводных камня, каждый из которых на сетевой шаре выстрелит обязательно, и ответ на
/// каждый:
///
/// <list type="number">
///   <item><b>Недописанный файл.</b> Приемник увидит файл раньше, чем отправитель закончит
///   писать. Правильный отправитель пишет <c>заказы.json.tmp</c> и переименовывает — переименование
///   атомарно. На тех, кто так не умеет, работает запасное правило: файл берется, только если его
///   не трогали <see cref="Settle"/> и его удается открыть монопольно.</item>
///   <item><b>Две копии программы на одну папку.</b> База открыта на нескольких машинах — файл
///   увидят все. Захват идет переименованием: <c>заказы.json</c> → <c>заказы.json.занял-ПК17</c>.
///   Перед переименованием исходник открывается с запретом второго захвата; кто успел, тот и
///   обрабатывает, остальные получают отказ файловой системы и идут дальше.</item>
///   <item><b>Повторы.</b> Отправитель при обрыве пришлет файл дважды, и это норма. Память о
///   принятых сообщениях — сами квитанции: есть <c>Квитанции/&lt;id&gt;.json</c> — сообщение уже
///   приходило. Память намеренно лежит в папке обмена, а не в <c>app.db</c>: папку видят все
///   машины, а <c>app.db</c> — только своя, и вторая доставка вполне может попасть на соседний
///   компьютер.</item>
///   <item><b>Отправитель не знает результат.</b> Рядом кладется квитанция: сколько принято,
///   сколько обновлено, что не прошло и почему.</item>
/// </list>
///
/// Здесь только файловая работа. Разбор формата — <see cref="InboxMessage"/>, укладка в
/// коллекцию — привязка, и оба слоя об этих папках ничего не знают.
/// </summary>
public static class InboxFolder
{
    /// <summary>Имена папок — по-русски: их читает человек, зашедший на общий диск.</summary>
    public const string IncomingName  = "Входящие";

    /// <inheritdoc cref="IncomingName"/>
    public const string ProcessedName = "Обработанные";

    /// <inheritdoc cref="IncomingName"/>
    public const string FailedName    = "Ошибки";

    /// <inheritdoc cref="IncomingName"/>
    public const string ReceiptsName  = "Квитанции";

    /// <summary>Метка захвата, дописываемая к имени файла.</summary>
    public const string ClaimMark = ".занял-";

    /// <summary>
    /// Сколько файл должен пролежать нетронутым, прежде чем его брать. Страховка на отправителя,
    /// который пишет прямо в конечное имя: пять секунд ничего не стоят, а половина выгрузки,
    /// уложенная в коллекцию, стоит дорого.
    /// </summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Через сколько брошенный захват считается брошенным. Окно намеренно большое: обмен на
    /// миллион строк идет долго, и отобрать файл у работающего соседа хуже, чем подождать лишний
    /// час. Захват переживает только выключение машины посреди работы — редкий случай, ради
    /// которого не стоит рисковать двойной обработкой.
    /// </summary>
    public static readonly TimeSpan StaleClaim = TimeSpan.FromHours(4);

    /// <summary>
    /// Потолок размера файла, читаемого В СТРОКУ (<see cref="ReadText"/>) — тот же, что потолок
    /// ответа сторонней программы (<c>EgressHttp.MaxResponseBytes</c>): беда одна — перепутанный
    /// источник не должен доводить программу до нехватки памяти. Строка стоит нескольких своих
    /// размеров: сам текст, разбор поверх него, вырезанный из него кусок.
    ///
    /// <b>К службе приема этот потолок больше не относится.</b> Раньше относился, и это было
    /// единственной причиной, по которой файл обмена вообще ограничивался размером: конверт
    /// разбирался из строки. Теперь служба приема идет по своему файлу потоком
    /// (<see cref="InboxMessage.TryParseFile"/>), записи читаются прямо из него, и память не
    /// зависит от размера файла — потолка у того канала нет вовсе.
    ///
    /// Остался этот потолок у шага сценария «файл из папки»: туда приходит что угодно — текст,
    /// XML, чужая выгрузка, — и шагу нужна строка целиком. Формат неизвестен, значит неизвестно и
    /// где в файле что лежит, а читать потоком то, о чем ничего не знаешь, нечем.
    ///
    /// Потолок нарочно НЕ из политики. Политика ограничивает то, что уходит наружу; здесь же
    /// дело не в правах, а в том, что программа обязана пережить любой файл, который кто угодно
    /// положил в папку обмена.
    /// </summary>
    public const long MaxFileBytes = 32L * 1024 * 1024;

    // ── Корень обмена ─────────────────────────────────────────────────────────

    /// <summary>
    /// Разложить папки под корнем и вернуть их. <c>null</c> — корень не годится, причина в
    /// <paramref name="explanation"/>.
    ///
    /// Разрешение спрашивается ЗДЕСЬ, у политики организации: папка-приемник — такая же дверь,
    /// как сетевой адрес, и открыть ее себе самому пользователь не должен. Это единственная
    /// точка, где появляются пути обмена, поэтому проверку не обойти, не начав с нее.
    /// </summary>
    public static InboxLayout? Open(string? root, out string explanation)
    {
        explanation = "";

        if (string.IsNullOrWhiteSpace(root))
        {
            explanation = "папка обмена не выбрана";
            return null;
        }

        var decision = EgressGate.CheckFolder(root);
        if (!decision.Allowed)
        {
            explanation = decision.Explanation;
            return null;
        }

        string full;
        try
        {
            full = Path.GetFullPath(root);
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException
                                                or PathTooLongException)
        {
            explanation = "путь не разобрался: " + failure.Message;
            return null;
        }

        var layout = new InboxLayout(
            full,
            Path.Combine(full, IncomingName),
            Path.Combine(full, ProcessedName),
            Path.Combine(full, FailedName),
            Path.Combine(full, ReceiptsName));

        try
        {
            foreach (var path in new[] { layout.Incoming, layout.Processed, layout.Failed, layout.Receipts })
                Directory.CreateDirectory(path);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            explanation = "папки обмена не создались: " + failure.Message;
            return null;
        }

        return layout;
    }

    // ── Что готово к разбору ──────────────────────────────────────────────────

    /// <summary>
    /// Файлы, которые можно брать: подходящее расширение, пролежали нетронутыми
    /// <see cref="Settle"/> и открываются монопольно. Порядок — по времени записи: обмен
    /// зависит от порядка, если отправитель шлет правки одной и той же записи подряд.
    /// </summary>
    /// <param name="layout">Разложенные папки обмена.</param>
    /// <param name="now">Момент, от которого считается выдержка.</param>
    /// <param name="extension">Какие файлы брать; пусто — ЛЮБЫЕ. Приемник берет только
    /// <c>.json</c>: у него свой формат, и чужой файл он все равно отверг бы. Шагу сценария
    /// формат задает расширение-преобразователь, и знать его заранее папке незачем — сообщение
    /// приходит и текстом, и XML, и чем угодно еще.</param>
    public static IReadOnlyList<InboxFile> Ready(InboxLayout layout, DateTimeOffset now,
                                                 string extension = ".json")
    {
        if (!Directory.Exists(layout.Incoming)) return [];

        var ready = new List<(InboxFile File, DateTime Written)>();

        foreach (var path in Enumerate(layout.Incoming))
        {
            // Файл с меткой захвата и половинка вида «заказы.json.tmp» сюда не попадают: у них
            // другое расширение, и это ровно та защита, ради которой отправителя просят
            // переименовывать.
            if (extension.Length > 0)
            {
                if (!Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            else
            {
                // Берем любой — значит, отсеиваем то же самое, но руками: захват соседа и
                // недописанную половинку. Иначе «заказы.json.занял-ПК17» прочитался бы как
                // обычный файл с невиданным расширением.
                if (path.Contains(ClaimMark, StringComparison.Ordinal))                     continue;
                if (Path.GetExtension(path).Equals(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            }

            DateTime written;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;   // забрал сосед, пока шло перечисление
                written = info.LastWriteTimeUtc;

                if (now - new DateTimeOffset(written, TimeSpan.Zero) < Settle) continue;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (!CanOpenExclusively(path)) continue;

            ready.Add((new InboxFile(path, Path.GetFileName(path)), written));
        }

        return [.. ready.OrderBy(r => r.Written).ThenBy(r => r.File.Name, StringComparer.Ordinal)
                        .Select(r => r.File)];
    }

    /// <summary>
    /// Открывается ли файл монопольно. Пока отправитель пишет, монопольное открытие не удастся —
    /// это и есть запасное правило готовности для тех, кто не переименовывает.
    /// </summary>
    private static bool CanOpenExclusively(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ── Захват ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Забрать файл себе переименованием. Возвращает путь захваченного файла либо <c>null</c>,
    /// если его уже забрал сосед.
    ///
    /// Исходник остается открытым с запретом второго захвата до конца переименования и отметки
    /// времени. Одного переименования мало: на Windows два MoveFileEx могут заранее открыть
    /// один исходник, затем оба переименовать его через свои дескрипторы в разные имена.
    ///
    /// <b>Время записи переставляется на момент захвата</b>, потому что именно от него считается
    /// брошенность (<see cref="ReclaimStale"/>). Переименование время не трогает, и без этой
    /// отметки файл, пролежавший во «Входящих» дольше <see cref="StaleClaim"/> — ночная
    /// доставка, база была недоступна, — сразу после захвата выглядел бы брошенным: сосед
    /// вернул бы его во «Входящие» посреди обработки, и сообщение разобралось бы дважды.
    /// </summary>
    /// <param name="file">Файл во «Входящих».</param>
    /// <param name="machine">Имя машины — оно уходит в метку захвата.</param>
    /// <param name="now">Момент захвата; от него считается брошенность.</param>
    public static string? TryClaim(InboxFile file, string machine, DateTimeOffset now)
    {
        var claimed = file.Path + ClaimMark + SafeMachine(machine);

        try
        {
            using var ownership = OpenForClaim(file.Path);
            File.Move(file.Path, claimed);

            // Отметка не вышла — захват все равно состоялся. Отдавать файл обратно из-за
            // неудавшейся отметки хуже, чем рискнуть лишним возвратом через четыре часа.
            try { File.SetLastWriteTimeUtc(claimed, now.UtcDateTime); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // Права на шаре бывают такие, что переименовать можно, а тронуть время нельзя.
            }

            return claimed;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Прочитать взятый файл целиком, отказав слишком большому ДО чтения. Возвращает текст либо
    /// <c>null</c> и причину словами — ту, что уедет отправителю в квитанции.
    ///
    /// Размер спрашивается у файловой системы, а не считается по прочитанному: узнать о беде
    /// после того, как тридцать три мегабайта уже в памяти, — не защита.
    ///
    /// Зовется шагом сценария «файл из папки», которому нужна строка целиком. Служба приема сюда
    /// не заходит — она идет по своему файлу потоком (см. <see cref="MaxFileBytes"/>).
    /// </summary>
    /// <param name="claimed">Путь захваченного файла.</param>
    /// <param name="trouble">Причина отказа; пусто — прочитано.</param>
    public static string? ReadText(string claimed, out string trouble)
    {
        try
        {
            var size = new FileInfo(claimed).Length;

            if (size > MaxFileBytes)
            {
                trouble = $"файл слишком велик: {size / 1024.0 / 1024:N1} МБ "
                        + $"при потолке {MaxFileBytes / 1024 / 1024} МБ";
                return null;
            }

            // Кодировка — по метке в начале файла, иначе UTF-8.
            trouble = "";
            return File.ReadAllText(claimed);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            trouble = "файл не прочитался: " + failure.Message;
            return null;
        }
    }

    /// <summary>
    /// Отметить, что захваченным файлом ЗАНИМАЮТСЯ прямо сейчас, — переставить время записи на
    /// текущий момент. Best-effort: не вышло переставить — обработка продолжается.
    ///
    /// <b>Зачем это понадобилось.</b> Брошенность захвата считается от времени записи
    /// (<see cref="ReclaimStale"/>), и пока файл был ограничен потолком в 32 МБ, обработка не
    /// могла идти четыре часа физически. Теперь потолка нет, и файл на миллионы записей вполне
    /// может обрабатываться дольше срока. Без отметки сосед счел бы такой захват брошенным,
    /// вернул файл во «Входящие» и разобрал его ВТОРОЙ РАЗ — параллельно с первым. В режиме
    /// «добавить» это ровно то, от чего защищает идентификатор сообщения; но квитанция ложится
    /// только по окончании укладки, и защититься ею посреди нее нечем.
    /// </summary>
    public static void Touch(string claimed, DateTimeOffset at)
    {
        try { File.SetLastWriteTimeUtc(claimed, at.UtcDateTime); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // Права на шаре бывают такие, что переименовать можно, а тронуть время нельзя.
            // Тогда все как раньше: длинная обработка рискует возвратом через четыре часа.
        }
    }

    /// <summary>
    /// Вернуть захваченный файл во «Входящие» под прежним именем — когда обработка не началась
    /// (программу закрывают, база отцепилась). Держать чужой захват до срока незачем.
    ///
    /// Время записи у вернувшегося файла — момент захвата (<see cref="TryClaim"/>), поэтому
    /// выдержку <see cref="Settle"/> он выждет заново: пять секунд при обходе раз в полминуты.
    /// Восстанавливать прежнее время нечем и незачем — прошлого значения после переименования
    /// уже нет, а платой был бы либо выдуманный момент записи, либо второй способ считать
    /// брошенность.
    /// </summary>
    public static void Release(string claimed, InboxFile file)
    {
        try
        {
            if (File.Exists(claimed) && !File.Exists(file.Path)) File.Move(claimed, file.Path);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // Не вернулся — заберет уборка брошенных захватов. Молчание тут уместно: это
            // не потеря данных, а лишняя задержка.
        }
    }

    /// <summary>
    /// Вернуть во «Входящие» захваты, брошенные дольше <see cref="StaleClaim"/> назад. Возвращает,
    /// сколько вернулось. Без этого выключенная посреди обмена машина уносит файл с собой
    /// навсегда, а отправитель никогда не узнает почему.
    /// </summary>
    public static int ReclaimStale(InboxLayout layout, DateTimeOffset now)
    {
        if (!Directory.Exists(layout.Incoming)) return 0;

        var reclaimed = 0;

        foreach (var path in Enumerate(layout.Incoming))
        {
            var mark = path.LastIndexOf(ClaimMark, StringComparison.Ordinal);
            if (mark <= 0) continue;

            try
            {
                // Не отбираем переименованный файл, пока владелец переставляет старое
                // время записи на момент захвата. Проверяем возраст уже ПОД защитой:
                // до открытия сосед еще мог успеть обновить отметку.
                using var ownership = OpenForClaim(path);
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                if (now - new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) < StaleClaim) continue;

                var original = path[..mark];
                if (File.Exists(original)) continue;   // отправитель прислал файл заново

                File.Move(path, original);
                reclaimed++;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // Забрал сосед или нет прав — не наше дело, следующий заход попробует снова.
            }
        }

        return reclaimed;
    }

    // ── Память о принятых сообщениях ──────────────────────────────────────────

    /// <summary>
    /// Приходило ли уже сообщение с таким идентификатором. Проверяется НАЛИЧИЕ файла квитанции,
    /// а не его содержимое: битая квитанция все равно означает, что сообщение уже приходило, и
    /// обработать его второй раз хуже, чем не понять подробности прошлого раза.
    /// </summary>
    public static bool AlreadySeen(InboxLayout layout, string? messageId) =>
        InboxMessage.ValidId(messageId) && File.Exists(ReceiptPath(layout, messageId!, ""));

    /// <summary>Прошлая квитанция сообщения; <c>null</c> — ее нет или она не читается.</summary>
    public static InboxReceipt? PastReceipt(InboxLayout layout, string? messageId)
    {
        if (!InboxMessage.ValidId(messageId)) return null;

        try
        {
            var path = ReceiptPath(layout, messageId!, "");
            return File.Exists(path) ? InboxReceipt.TryParse(File.ReadAllText(path)) : null;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Положить квитанцию. Пишется через временное имя и переименование — то самое правило,
    /// которое приемник требует от отправителей: сам он тоже не должен подсовывать половину
    /// файла тому, кто его читает.
    /// </summary>
    public static void WriteReceipt(InboxLayout layout, InboxReceipt receipt)
    {
        var path = ReceiptPath(layout, receipt.Id, receipt.File);
        var temp = path + ".tmp";

        try
        {
            Directory.CreateDirectory(layout.Receipts);
            File.WriteAllText(temp, receipt.ToJson());
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* и уборка не вышла */ }
            throw new InboxException("Квитанция не записалась: " + failure.Message, failure);
        }
    }

    /// <summary>
    /// Имя квитанции: по идентификатору сообщения, если он есть, — тогда она же служит памятью о
    /// повторах. Без идентификатора остается только ответ отправителю рядом с именем его файла,
    /// и защиты от повтора нет; поэтому режим «добавить» идентификатор и требует.
    /// </summary>
    private static string ReceiptPath(InboxLayout layout, string id, string file) =>
        Path.Combine(layout.Receipts,
            InboxMessage.ValidId(id)
                ? id + ".json"
                : Path.GetFileNameWithoutExtension(file) + ".result.json");

    // ── Раскладка обработанного ───────────────────────────────────────────────

    /// <summary>
    /// Убрать захваченный файл: принятый — в «Обработанные/2026-09», непринятый — в «Ошибки».
    /// Возвращает, куда он уехал.
    ///
    /// Зовется ПОСЛЕ записи квитанции, и порядок принципиален. Упади программа между укладкой и
    /// переносом — файл найдется снова, но квитанция уже лежит, и повтор отсекется по ней.
    /// Обратный порядок оставил бы отправителя без ответа, а сообщение — без памяти о нем.
    /// </summary>
    public static string Finish(InboxLayout layout, string claimed, InboxFile file,
                                bool accepted, DateTimeOffset now)
    {
        // Помесячные папки: за год обмена во «Входящих» набирается столько файлов, что папка
        // перестает открываться, — беда известная и лечится заранее, а не потом.
        var folder = accepted
            ? Path.Combine(layout.Processed, now.ToString("yyyy-MM"))
            : layout.Failed;

        try
        {
            Directory.CreateDirectory(folder);
            var target = Unique(Path.Combine(folder, file.Name));
            File.Move(claimed, target);
            return target;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw new InboxException("Обработанный файл не убрался из «Входящих»: " + failure.Message,
                                     failure);
        }
    }

    /// <summary>
    /// Свободное имя рядом с занятым: «заказы (2).json». Отправитель шлет файлы с повторяющимися
    /// именами постоянно, и затирать вчерашний разбор сегодняшним нельзя — по нему разбираются,
    /// когда что-то разошлось.
    /// </summary>
    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;

        var folder    = Path.GetDirectoryName(path) ?? "";
        var stem      = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){extension}");
    }

    // ── Мелочи ────────────────────────────────────────────────────────────────

    /// <summary>Один участник меняет имя и отметку времени; остальные не входят.</summary>
    private static FileStream OpenForClaim(string path) =>
        // Windows: запрещаем соседу открыть исходник для чтения, но разрешаем своему
        // File.Move открыть его для DELETE/rename. Unix: FileShare.Delete дает общий
        // flock, поэтому нужен FileShare.None; переименованию на Unix он не мешает.
        new(path, FileMode.Open, FileAccess.Read,
            OperatingSystem.IsWindows() ? FileShare.Delete : FileShare.None);

    private static IEnumerable<string> Enumerate(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder).ToList();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Имя машины в метке захвата: только то, что безопасно в имени файла. Имя компьютера
    /// задает администратор, и рассчитывать на его безобидность нельзя.
    /// </summary>
    private static string SafeMachine(string? machine)
    {
        var text = new string((machine ?? "")
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')
            .Take(40)
            .ToArray());

        return text.Length > 0 ? text : "неизвестно";
    }
}

/// <summary>Сбой файловой работы приемника — отдельный тип, чтобы отличать его от сбоя разбора.</summary>
public sealed class InboxException(string message, Exception? inner = null)
    : Exception(message, inner);
