using DataBase.Core.Models;
using DataBase.Database.Repositories;

namespace DataBase.Views.ConstructorDb.Calendar;

/// <summary>
/// Ленивая forward-пагинация событий ОДНОГО дня для «Расписания». Тянет события порциями
/// (keyset — O(страницы), не зависит от глубины), сливая несколько подключённых БД в единый
/// поток в порядке Расписания: all-day первыми, затем по времени начала. Позволяет не
/// материализовать сразу все события дня (даже если их миллионы) — грузим по мере скролла.
///
/// Каждый источник читается двумя фазами: сначала all-day (AllDay=1), затем timed (AllDay=0),
/// и каждая фаза упорядочена по StartIso ASC. Это ключево для скорости: такой порядок ложится
/// на индекс (CollectionId, AllDay, StartIso) и идёт seek'ом; общий же ORDER BY по AllDay DESC
/// заставлял SQLite сортировать все строки дня во временной таблице (секунды фриза на 5 млн).
/// Работает синхронно и быстро (страница ~сотни строк), поэтому не морозит UI и безопасно
/// использует общее подключение контекста.
/// </summary>
public sealed class CalendarDayPager
{
    private const int DbPageSize = 400;   // сколько тянуть из одной БД за один запрос

    private sealed class Src
    {
        public int DbId;
        public ConstructorRepository Repo = null!;
        public ISet<int>? Active;
        public int Phase = 1;              // 1 = all-day, затем 0 = timed
        public string? CurStart;           // keyset внутри фазы (null = с начала фазы)
        public int CurRec, CurField;
        public readonly Queue<CalendarRecord> Buffer = new();
        public bool Exhausted;
    }

    private readonly List<Src> _srcs;
    private readonly DateTime _dayStart, _dayEnd;

    public CalendarDayPager(IEnumerable<(int dbId, ConstructorRepository repo, ISet<int>? active)> dbs,
                            DateTime dayStart, DateTime dayEnd)
    {
        _dayStart = dayStart;
        _dayEnd   = dayEnd;
        _srcs = dbs.Select(d => new Src { DbId = d.dbId, Repo = d.repo, Active = d.active }).ToList();
    }

    /// <summary>Есть ли ещё непрочитанные события (для футера «Загрузка…»).</summary>
    public bool HasMore => _srcs.Any(s => !s.Exhausted || s.Buffer.Count > 0);

    /// <summary>Следующие до <paramref name="count"/> событий в порядке Расписания (merge голов буферов БД).</summary>
    public List<CalendarRecord> Next(int count)
    {
        var outp = new List<CalendarRecord>(count);
        for (var i = 0; i < count; i++)
        {
            Src? best = null;
            foreach (var s in _srcs)
            {
                EnsureBuffered(s);
                if (s.Buffer.Count == 0) continue;
                if (best is null || Less(s.Buffer.Peek(), best.Buffer.Peek())) best = s;
            }
            if (best is null) break;
            outp.Add(best.Buffer.Dequeue());
        }
        return outp;
    }

    // Догружает буфер источника, переходя от фазы all-day к timed по мере исчерпания.
    private void EnsureBuffered(Src s)
    {
        while (s.Buffer.Count == 0 && !s.Exhausted)
        {
            var page = s.Repo.GetCalendarDayEventsPage(
                _dayStart, _dayEnd, s.Active, s.Phase, s.CurStart, s.CurRec, s.CurField, DbPageSize);
            if (page.Count > 0)
            {
                foreach (var ev in page) { ev.DbId = s.DbId; s.Buffer.Enqueue(ev); }
                var last = page[^1];
                s.CurStart = DateTime.SpecifyKind(last.StartDt, DateTimeKind.Unspecified).ToString("o");
                s.CurRec   = last.Id ?? 0;
                s.CurField = last.FieldId;
                if (page.Count < DbPageSize) AdvancePhase(s);   // фаза исчерпана — дальше следующая
            }
            else AdvancePhase(s);
        }
    }

    // all-day (1) → timed (0) → источник исчерпан.
    private static void AdvancePhase(Src s)
    {
        if (s.Phase == 1) { s.Phase = 0; s.CurStart = null; s.CurRec = 0; s.CurField = 0; }
        else s.Exhausted = true;
    }

    // Порядок Расписания: all-day раньше, затем по времени начала; тай-брейки — для стабильного merge.
    private static bool Less(CalendarRecord a, CalendarRecord b)
    {
        if (a.AllDay != b.AllDay) return a.AllDay;
        var c = a.StartDt.CompareTo(b.StartDt);
        if (c != 0) return c < 0;
        if (a.DbId != b.DbId) return a.DbId < b.DbId;
        var idc = (a.Id ?? 0).CompareTo(b.Id ?? 0);
        if (idc != 0) return idc < 0;
        return a.FieldId < b.FieldId;
    }
}
