namespace SmartBase.Database.DocIndex;

/// <summary>
/// Скорость прогона чтения — без разогрева.
///
/// Первые <see cref="WarmUp"/> документов в счет не идут: на них прогреваются разборщики и кеш
/// диска, и они медленнее остальных. Средняя от начала прогона завышала бы и «осталось около …»,
/// и скорость, которую раздел запоминает для окна подтверждения (<c>LibrarySettings.RememberSpeed</c>).
/// Мерить начинаем с конца разогрева, а верить замеру — когда за ним разобрано еще
/// <see cref="WarmUp"/> документов: на одном-двух средняя пляшет, и «осталось 4 часа» сменялось бы
/// на «2 минуты» через секунду.
///
/// Время — по последнему разобранному документу, а не «сейчас»: в конце прогона идет
/// обслуживание индекса, и его секунды к скорости разбора не относятся.
/// </summary>
public sealed class DocReadPace
{
    /// <summary>Сколько первых документов прогона — разогрев.</summary>
    public const int WarmUp = 5;

    private TimeSpan? _warmedAt;
    private TimeSpan  _lastAt;
    private int       _lastDone;

    /// <summary>Разобран документ номер <paramref name="done"/>, от начала прогона прошло <paramref name="elapsed"/>.</summary>
    public void Done(int done, TimeSpan elapsed)
    {
        if (done == WarmUp) _warmedAt = elapsed;
        _lastDone = done;
        _lastAt   = elapsed;
    }

    /// <summary>Секунд на документ после разогрева; <c>null</c> — мерить еще не на чем.</summary>
    public double? SecondsPerDocument
    {
        get
        {
            var measured = _lastDone - WarmUp;
            if (_warmedAt is not { } warmed || measured < WarmUp) return null;

            var span = _lastAt - warmed;
            return span > TimeSpan.Zero ? span.TotalSeconds / measured : null;
        }
    }

    /// <summary>Сколько еще ждать до <paramref name="total"/> документов; <c>null</c> — мерить еще не на чем или все прочитано.</summary>
    public TimeSpan? Left(int total) =>
        _lastDone < total && SecondsPerDocument is { } perDoc
            ? TimeSpan.FromSeconds(perDoc * (total - _lastDone))
            : null;
}
