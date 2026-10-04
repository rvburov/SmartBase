namespace SmartBase.Core;

/// <summary>Одна долгая работа, идущая в фоне.</summary>
/// <param name="Id">Ключ работы: повторный отчет с тем же ключом обновляет ее, а не заводит вторую.</param>
/// <param name="Title">Что делается — «Чтение документов», «Обновление базы «Архив»».</param>
/// <param name="Detail">Подробности одной строкой — числа, оценка времени, чей это раздел.</param>
/// <param name="Done">Сделано; 0 при неизвестном объеме.</param>
/// <param name="Total">Всего; 0 — объем неизвестен, полоса не рисуется.</param>
/// <param name="Failed">Работа сорвалась — значок краснеет.</param>
/// <param name="Stop">Чем остановить; null — остановить нельзя.</param>
public sealed record BackgroundJob(
    string  Id,
    string  Title,
    string  Detail,
    int     Done,
    int     Total,
    bool    Failed,
    Action? Stop);

/// <summary>
/// Реестр идущих фоновых работ — один на приложение.
///
/// Заведен потому, что долгая фоновая работа перестала быть делом одного экрана. Чтение
/// документов человек запускает в библиотеке, а уходит из нее сразу же — и дальше ход работы
/// виден только там, где ее нет. Значок в панели разделов решает это: он появляется, пока
/// что-то идет, и показывает, что именно, из любого раздела.
///
/// <b>Реестр не знает про Avalonia</b> — сюда отчитываются службы из <c>Database/</c>, которым
/// про интерфейс знать нечего. Подписчик (панель разделов) сам уходит в UI-поток.
///
/// <b>Второй жилец уже есть.</b> Кроме чтения документов в программе идет подключение и
/// обновление баз при запуске (<c>Database/MountQueue.cs</c>) — его ход сейчас виден только
/// подписью под именем базы в сайдбаре, и ему место здесь же.
/// </summary>
public static class BackgroundJobs
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, BackgroundJob> Jobs = new(StringComparer.Ordinal);

    /// <summary>Список работ изменился. Приходит с ЛЮБОГО потока — подписчик маршалит сам.</summary>
    public static event Action? Changed;

    /// <summary>Идущие работы в порядке появления.</summary>
    public static IReadOnlyList<BackgroundJob> All
    {
        get { lock (Lock) return [.. Jobs.Values]; }
    }

    /// <summary>Идет ли что-нибудь.</summary>
    public static bool Any { get { lock (Lock) return Jobs.Count > 0; } }

    /// <summary>Сорвалась ли какая-нибудь из идущих работ.</summary>
    public static bool AnyFailed { get { lock (Lock) return Jobs.Values.Any(j => j.Failed); } }

    /// <summary>
    /// Общая доля готовности по всем работам: сумма сделанного к сумме всего. Работы с
    /// неизвестным объемом в счет не идут — иначе одна такая обнулила бы полосу у остальных.
    /// Возвращает null, когда мерить нечего: кольцо тогда просто серое.
    /// </summary>
    public static double? Progress()
    {
        lock (Lock)
        {
            var done = 0;
            var all  = 0;
            foreach (var j in Jobs.Values)
            {
                if (j.Total <= 0) continue;
                done += Math.Clamp(j.Done, 0, j.Total);
                all  += j.Total;
            }
            return all > 0 ? done / (double)all : null;
        }
    }

    /// <summary>Завести работу или обновить уже заведенную с тем же ключом.</summary>
    public static void Report(string id, string title, string detail,
                              int done = 0, int total = 0, Action? stop = null)
    {
        lock (Lock) Jobs[id] = new BackgroundJob(id, title, detail, done, total, false, stop);
        Changed?.Invoke();
    }

    /// <summary>
    /// Работа сорвалась. Остается в списке — иначе человек не узнал бы, почему она кончилась;
    /// уходит по <see cref="Finish"/>, когда о ней прочитали (ссылка «Убрать» в панели фоновых
    /// работ), или при следующем запуске: <see cref="Report"/> с тем же ключом заменяет ее идущей.
    /// </summary>
    public static void Fail(string id, string title, string detail)
    {
        lock (Lock) Jobs[id] = new BackgroundJob(id, title, detail, 0, 0, true, null);
        Changed?.Invoke();
    }

    /// <summary>Убрать работу из списка — закончилась сама, остановлена или о сбое прочитали.</summary>
    public static void Finish(string id)
    {
        bool removed;
        lock (Lock) removed = Jobs.Remove(id);
        if (removed) Changed?.Invoke();
    }
}
