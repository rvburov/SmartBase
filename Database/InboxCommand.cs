using System.Globalization;
using SmartBase.Database.Entities;
using SmartBase.Integrations;
using SmartBase.Utils;

namespace SmartBase.Database;

/// <summary>
/// Совсем простой канал: <c>SmartBase --inbox сообщение.json</c>
/// (docs/ROADMAP-API.md, раздел 7 — «аргументы командной строки»).
///
/// Он нужен там, где отправитель умеет запустить программу, но не умеет ни HTTP, ни общей папки:
/// планировщик задач, командный файл, обработчик учетной системы, «выполнить и посмотреть код
/// возврата». Ни порта, ни токена, ни прав администратора; ответ приходит СРАЗУ, в поток вывода.
///
/// <b>Отличие от сетевого канала — в том, кто отвечает за допуск.</b> Здесь его нет и не нужно:
/// запустить программу с этим ключом может только тот, кто уже сидит за машиной и открывает
/// файл базы своими руками. Поэтому записи подписаны ИМ, а не роботом, и права спрашиваются его.
/// Токен тут защищал бы дверь, рядом с которой нет стены.
///
/// <b>Работает при закрытой программе.</b> Это второе его свойство и половина смысла: обмен по
/// расписанию не должен требовать, чтобы кто-то оставил окно открытым. Второй экземпляр при этом
/// не поднимается — лок единственного экземпляра не берется, а SQLite разводит писателей сам,
/// ровно как две машины над общей базой.
///
/// Код возврата — тоже часть ответа: 0 принято, 1 отклонено, 2 не с чего было начать.
/// Планировщик читает именно его, а не текст.
/// </summary>
public static class InboxCommand
{
    /// <summary>Ключ канала; за ним — путь к файлу сообщения.</summary>
    public const string Flag = "--inbox";

    /// <summary>Необязательный ключ: в какую базу класть. Без него — база по умолчанию.</summary>
    public const string DatabaseFlag = "--db";

    /// <summary>Принято.</summary>
    public const int Accepted = 0;

    /// <summary>Сообщение дошло, но записей по нему не появилось.</summary>
    public const int Rejected = 1;

    /// <summary>Не с чего было начать: нет файла, нет базы, нечего разбирать.</summary>
    public const int Failed = 2;

    /// <summary>Запущены ли мы этим ключом.</summary>
    public static bool Wanted(string[] args) => Array.IndexOf(args, Flag) >= 0;

    /// <summary>
    /// Выполнить канал. Возвращает код возврата процесса; текст ответа — квитанция тем же
    /// видом, что кладется в папку обмена: у двух каналов один ответ, и отправителю не надо
    /// разбирать два формата.
    /// </summary>
    public static int Run(string[] args, TextWriter output)
    {
        var file = Value(args, Flag);

        if (file.Length == 0)
            return Fail(output, $"Не указан файл: {Flag} <путь к сообщению>.");

        if (!File.Exists(file))
            return Fail(output, $"Файл не найден: {file}.");

        // Файл читается ПОТОКОМ, и потолка на его размер здесь нет — как и у папки обмена.
        // Канал этот и заводят под ночную выгрузку из планировщика, то есть под самые большие
        // файлы, какие вообще приходят; отказать им по размеру значило бы отказать по сути.
        // Потолок остается у сети: тело обращения надо принять целиком, прежде чем станет
        // понятно, что это (см. ApiDescription — там он назван отправителю прямо).
        var message = InboxMessage.TryParseFile(file, out var error);

        if (message is null) return Fail(output, "Сообщение не разобралось: " + error + ".");

        var problems = message.Problems();
        if (problems.Count > 0) return Fail(output, string.Join("; ", problems) + ".");

        // Настройки и профиль — те же, что у окна: подпись записи должна быть одинаковой,
        // каким бы путем данные ни пришли.
        AppSettings.EnsureCreated();

        DatabaseRegistry registry;
        try
        {
            registry = DatabaseRegistry.Open(AppSettings.DbPath, AppSettings.SystemDbPath);
        }
        catch (Exception failure)
        {
            return Fail(output, "Системная база app.db не открылась: " + failure.Message);
        }

        using (registry)
        {
            AppSettings.AttachStore(registry);
            CurrentUser.AttachStore(registry);

            var wanted = Value(args, DatabaseFlag);
            var entry  = Choose(registry, wanted);

            if (entry is null)
                return Fail(output, wanted.Length > 0
                    ? $"База данных «{wanted}» в реестре не найдена."
                    : "В реестре нет ни одной базы данных.");

            DatabaseManager manager;
            try
            {
                manager = Mount(entry);
            }
            catch (Exception failure)
            {
                return Fail(output, $"База «{entry.Name}» не открылась: " + failure.Message);
            }

            using (manager)
            {
                var now     = DateTimeOffset.Now;
                var receipt = InboxIntake.Apply(message, manager.Constructor, entry.Id,
                                                Path.GetFileName(file), now);

                output.WriteLine(receipt.ToJson());

                return receipt.Accepted ? Accepted : Rejected;
            }
        }
    }

    // ── Мелочи ────────────────────────────────────────────────────────────────

    /// <summary>Значение ключа; пусто — ключа нет либо за ним сразу идет другой ключ.</summary>
    private static string Value(string[] args, string flag)
    {
        var at = Array.IndexOf(args, flag);

        if (at < 0 || at + 1 >= args.Length) return "";

        var next = args[at + 1];
        return next.StartsWith("--", StringComparison.Ordinal) ? "" : next;
    }

    /// <summary>
    /// Какую базу открывать. Без ключа — база по умолчанию; с ключом — по имени либо по пути к
    /// файлу. Имя, а не номер: номера баз человеку не показывают нигде, и просить его в
    /// командной строке значило бы просить то, чего он не знает.
    /// </summary>
    private static DatabaseRefEntity? Choose(DatabaseRegistry registry, string wanted)
    {
        var all = registry.GetAll();

        if (wanted.Length == 0) return all.FirstOrDefault(d => d.IsDefault) ?? all.FirstOrDefault();

        var byName = all.FirstOrDefault(d =>
            string.Equals(d.Name.Trim(), wanted.Trim(), StringComparison.CurrentCultureIgnoreCase));

        if (byName is not null) return byName;

        return all.FirstOrDefault(d =>
            string.Equals(d.Path, wanted, StringComparison.OrdinalIgnoreCase)
            || (!d.IsPostgres && SamePath(d.Path, wanted)));
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                                 OperatingSystem.IsWindows()
                                     ? StringComparison.OrdinalIgnoreCase
                                     : StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Открыть базу записи реестра — тем же путем, что и окно. У базы по умолчанию берется
    /// действующий путь приложения, а не сохраненный в реестре: папку данных могли перенести.
    /// </summary>
    private static DatabaseManager Mount(DatabaseRefEntity entry)
    {
        if (entry.IsDefault && !entry.IsPostgres)
            return DatabaseManager.Open(entry.Id, entry.Name, AppSettings.DbPath);

        var path = entry.IsPostgres
            ? entry.ToPgInfo()?.ToConnectionString() ?? entry.Path
            : entry.Path;

        return DatabaseManager.Open(entry.Id, entry.Name, path, entry.Provider);
    }

    /// <summary>
    /// Отказ словами и код возврата. Ответ идет тем же потоком, что и квитанция: тот, кто
    /// читает вывод, не должен знать заранее, куда смотреть.
    /// </summary>
    private static int Fail(TextWriter output, string explanation)
    {
        output.WriteLine(InboxReceipt
            .Rejected("", "", "", 0, explanation, DateTimeOffset.Now)
            .ToJson());

        return Failed;
    }

    /// <summary>Подсказка для <c>--help</c> и для описания отправителю.</summary>
    public static string Usage() => string.Join(Environment.NewLine,
    [
        $"SmartBase {Flag} <файл.json> [{DatabaseFlag} <имя базы>]",
        "",
        "Кладет записи из файла в коллекцию и отвечает квитанцией в поток вывода.",
        "Формат файла тот же, что у папки обмена.",
        "",
        $"Код возврата: {Accepted.ToString(CultureInfo.InvariantCulture)} — принято, "
        + $"{Rejected.ToString(CultureInfo.InvariantCulture)} — отклонено, "
        + $"{Failed.ToString(CultureInfo.InvariantCulture)} — не с чего было начать.",
    ]);
}
