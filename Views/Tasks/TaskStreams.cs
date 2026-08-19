namespace SmartBase.Views.Tasks;

/// <summary>
/// Разбиение куска группы на потоки чтения. Порядок задач в коллекции («срок ↑, без даты в
/// конец») одним запросом не выражается, поэтому группа читается двумя потоками встык:
/// строки <c>[0, dated)</c> — «со сроком» (проекция <c>calendar_index</c>), дальше — «без
/// срока» (записи без строки в проекции). См.
/// <c>ConstructorRepository.TasksPaging.cs</c>.
///
/// Кусок, который просит виртуальный список, границу потоков не уважает: страница легко
/// начинается в первом и заканчивается во втором. Пересчёт «сколько взять из какого и с каким
/// смещением» — чистая арифметика, поэтому живёт отдельно от загрузки и проверяется тестами
/// (<c>TaskStreamsTests</c>): ошибка здесь не падает, а тихо повторяет или теряет задачи ровно
/// на стыке — там, где это заметит только внимательный человек.
/// </summary>
public static class TaskStreams
{
    /// <summary>Один запрос к БД: из какого потока читать, сколько пропустить и сколько взять.</summary>
    /// <param name="Dated">true — поток «со сроком», false — «без срока».</param>
    /// <param name="Skip">Смещение от начала ПОТОКА (не группы).</param>
    /// <param name="Take">Сколько строк взять.</param>
    public readonly record struct Slice(bool Dated, int Skip, int Take);

    /// <summary>
    /// Разложить кусок группы на запросы. Возвращает один срез, если кусок целиком внутри
    /// одного потока, и два — если он пересекает границу.
    /// </summary>
    /// <param name="dated">Длина первого потока: сколько задач коллекции со сроком.</param>
    /// <param name="from">Номер первой нужной задачи ВНУТРИ группы (0 — первая под заголовком).</param>
    /// <param name="take">Сколько задач нужно.</param>
    public static List<Slice> Split(int dated, int from, int take)
    {
        var result = new List<Slice>(2);
        if (take <= 0 || from < 0) return result;

        dated = Math.Max(0, dated);

        // Часть до границы — из потока «со сроком».
        if (from < dated)
        {
            var n = Math.Min(take, dated - from);
            result.Add(new Slice(Dated: true, Skip: from, Take: n));
            from  += n;
            take  -= n;
        }

        // Остаток — из «без срока»; его смещение отсчитывается от начала СВОЕГО потока.
        if (take > 0) result.Add(new Slice(Dated: false, Skip: from - dated, Take: take));

        return result;
    }
}
