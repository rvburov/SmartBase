namespace SmartBase.Views.Library;

/// <summary>Место потока коллекции в плоской нумерации строк ленты.</summary>
/// <param name="FirstRow">Индекс первой карточки потока.</param>
/// <param name="RowCount">Сколько строк занимает поток.</param>
public readonly record struct DocRowSpan(int FirstRow, int RowCount);

/// <summary>
/// Раскладка ленты документов по строкам: где начинается поток каждой коллекции и что стоит в
/// строке с заданным номером. Отдельно от <c>DocVirtualList</c>, потому что это чистая
/// арифметика — ее и проверяют тесты (<c>DocRowMapTests</c>), а не отрисовку.
///
/// Зеркало <c>TaskRowMap</c> с одним отличием: у задач каждая группа начинается со
/// строки-заголовка, поэтому ее размер — «1 + карточки». Лента библиотеки ПЛОСКАЯ (так решено
/// в интерфейсе раздела): заголовков нет, поток равен числу своих карточек, а к какой коллекции
/// относится документ, написано в самой карточке. Пустой поток занимает ноль строк и потому
/// невидим — отдельного «пусто» у коллекции здесь не бывает.
///
/// Виртуальный список обращается к строкам по индексу и обязан отвечать, не материализуя
/// соседей: сколько всего строк, какой коллекции принадлежит тысячная и какая она по счету
/// внутри своего потока. Все это — префиксные суммы плюс двоичный поиск, поэтому цена ответа
/// не зависит ни от числа документов, ни от числа коллекций.
/// </summary>
public sealed class DocRowMap
{
    private readonly DocRowSpan[] _spans;

    public DocRowMap(IReadOnlyList<int> streams)
    {
        _spans = new DocRowSpan[streams.Count];

        var row = 0;
        for (var i = 0; i < streams.Count; i++)
        {
            var count = Math.Max(0, streams[i]);
            _spans[i] = new DocRowSpan(row, count);
            row += count;
        }
        Count = row;
    }

    /// <summary>Всего строк в ленте — длина полосы прокрутки.</summary>
    public int Count { get; }

    public int StreamCount => _spans.Length;

    public DocRowSpan this[int streamIndex] => _spans[streamIndex];

    /// <summary>
    /// Что стоит в строке: номер потока и номер карточки ВНУТРИ него (0 — первая).
    /// Поток −1 означает, что строки нет (лента пуста либо индекс за ее пределами).
    /// </summary>
    public (int Stream, int Local) Resolve(int row)
    {
        if (row < 0 || row >= Count) return (-1, -1);

        // Двоичный поиск последнего потока, который начинается не позже строки. Пустые потоки
        // занимают ноль строк, и поиск обязан их перескочить: у пустого FirstRow совпадает с
        // началом следующего, и «последний подходящий» — как раз непустой.
        int lo = 0, hi = _spans.Length - 1, ans = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_spans[mid].FirstRow <= row) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (ans < 0) return (-1, -1);

        // Найденный поток может оказаться пустым хвостом — отступаем к ближайшему непустому.
        while (ans >= 0 && _spans[ans].RowCount == 0) ans--;
        if (ans < 0) return (-1, -1);

        return (ans, row - _spans[ans].FirstRow);
    }
}
