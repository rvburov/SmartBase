namespace DataBase.Core.Models;

/// <summary>
/// Точные агрегаты окна Диаграммы по одной коллекции: число событий и охват
/// min(начало)…max(конец) по ВСЕМ строкам окна, а не только по доставленным
/// (чтение окна Диаграммы обрезано перебором на коллекцию — см.
/// ConstructorRepository.GetDiagramRecordsCapped). Считаются фоновой фазой 2
/// (GetDiagramStats) индексными сканами и тихо уточняют счётчики групп,
/// «+N ещё» и суммарные бары.
/// </summary>
public sealed class DiagramGroupStat
{
    public int Count;          // все события коллекции, пересекающие окно
    public int AllDayCount;    // из них all-day (Count == AllDayCount — группа целиком all-day)
    public DateTime  MinStart;
    public DateTime? MaxEndTimed;    // max(конец) среди timed; null — timed нет
    public DateTime? MaxEndAllDay;   // max(конец) среди all-day; null — all-day нет
}
