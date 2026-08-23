using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Utils;

namespace SmartBase.Notifications;

/// <summary>
/// Читатель очередей событий задач по всем допущенным базам: курсор на базу, свое
/// подключение на базу, отвалившаяся база не гасит остальные.
///
/// Курсоры персистентные (app.db) — за счет этого работает догон: события, случившиеся
/// при закрытом приложении, доедут при следующем запуске. Первое знакомство с базой историю
/// не проигрывает: курсор садится на текущий хвост очереди — иначе обновление приложения
/// вываливало бы разом все, что накопилось (тот же прием, что у водяной метки календаря
/// и у ChangePoller).
///
/// Разделение потоков строгое: <see cref="Read"/> и <see cref="Reseed"/> зовутся из фона и
/// только ЧИТАЮТ базы, намечая новые курсоры; единственный, кто пишет app.db, — это
/// <see cref="Commit"/> с UI-потока. Состояние под локом: Dictionary из двух потоков
/// портится молча.
/// </summary>
public sealed class TaskEventReader(Func<IReadOnlyList<DatabaseManager>> grantedDbs) : ITaskEventSource
{
    // Сколько событий берем с одной базы за проход: больше пользователю все равно покажут
    // сводкой, а хвост приедет следующим тиком.
    private const int BatchLimit = 200;

    private readonly Lock _lock = new();
    private Dictionary<int, long> _cursors = AppSettings.LoadTaskEventCursors();
    private Dictionary<int, long> _pending = [];

    public IReadOnlyList<TaskEvent> Read()
    {
        var events  = new List<TaskEvent>();
        var pending = new Dictionary<int, long>();

        Dictionary<int, long> cursors;
        lock (_lock) cursors = new Dictionary<int, long>(_cursors);

        foreach (var db in grantedDbs())
        {
            try
            {
                if (!cursors.TryGetValue(db.DbId, out var cursor))
                {
                    // Базу видим впервые: сажаем курсор на хвост, событий не отдаем.
                    pending[db.DbId] = db.Constructor.MaxTaskEventIdIsolated();
                    continue;
                }

                var (rows, next) = db.Constructor.ReadTaskEventsAfterIsolated(
                    cursor, CurrentUser.Key, CurrentUser.DisplayName, BatchLimit);
                pending[db.DbId] = next;

                foreach (var e in rows)
                {
                    // DbId в самой базе нет и быть не может — проставляем на чтении, иначе
                    // одинаковые RecordId разных баз схлопнулись бы при дедупе и по метке.
                    e.DbId = db.DbId;

                    // Изолированное чтение прав не проверяет: коллекция могла стать
                    // недоступной с тех пор, как событие записали.
                    if (db.Permissions.Can(PermAction.ViewCollection, e.CollectionId))
                        events.Add(e);
                }
            }
            catch
            {
                // База отвалилась (сеть, файл занят) — пропускаем ее до следующего тика,
                // курсор не двигаем: события не потеряются.
            }
        }

        lock (_lock) _pending = pending;
        return events;
    }

    public void Commit()
    {
        lock (_lock)
        {
            if (_pending.Count == 0) return;
            foreach (var (dbId, id) in _pending) _cursors[dbId] = id;
            _pending = [];
            try { AppSettings.SaveTaskEventCursors(_cursors); }
            catch { /* запись курсора не критична: в худшем случае увидим событие дважды */ }
        }
    }

    public void Reseed()
    {
        var tails = new Dictionary<int, long>();
        foreach (var db in grantedDbs())
        {
            try { tails[db.DbId] = db.Constructor.MaxTaskEventIdIsolated(); }
            catch { /* база недоступна — курсор посеется при следующем чтении */ }
        }

        // Намеченное чтением отбрасываем: включили тумблер — показываем только то, что
        // случится дальше.
        lock (_lock) _pending = tails;
    }
}
