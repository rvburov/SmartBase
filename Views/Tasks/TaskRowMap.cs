namespace SmartBase.Views.Tasks;

/// <summary>Что стоит в строке списка задач.</summary>
public enum TaskRowKind
{
    /// <summary>Заголовок коллекции — первая строка каждой группы.</summary>
    Header,
    /// <summary>Карточка задачи.</summary>
    Task,
}

/// <summary>Место группы в плоской нумерации строк.</summary>
/// <param name="FirstRow">Индекс строки-заголовка.</param>
/// <param name="RowCount">Сколько строк занимает группа целиком (заголовок + карточки + «+N ещё»).</param>
public readonly record struct TaskRowSpan(int FirstRow, int RowCount);

/// <summary>
/// Раскладка списка задач по строкам: где начинается каждая группа и что стоит в строке с
/// заданным номером. Отдельно от <see cref="TaskVirtualList"/>, потому что это чистая
/// арифметика — её и проверяют тесты (<c>TaskRowMapTests</c>), а не отрисовку.
///
/// Виртуальный список обращается к строкам по индексу и обязан отвечать, не материализуя
/// соседей: сколько всего строк, какой группе принадлежит тысячная и какая она по счёту внутри
/// своей коллекции. Всё это — префиксные суммы плюс двоичный поиск, поэтому цена ответа не
/// зависит ни от числа задач, ни от числа коллекций.
/// </summary>
public sealed class TaskRowMap
{
    /// <summary>Одна группа на входе.</summary>
    /// <param name="Tasks">Сколько карточек она показывает.</param>
    /// <param name="Collapsed">Свёрнута ли: тогда от неё остаётся один заголовок.</param>
    public readonly record struct Group(int Tasks, bool Collapsed);

    private readonly TaskRowSpan[] _spans;
    private readonly Group[] _groups;

    public TaskRowMap(IReadOnlyList<Group> groups)
    {
        _groups = [.. groups];
        _spans  = new TaskRowSpan[_groups.Length];

        var row = 0;
        for (var i = 0; i < _groups.Length; i++)
        {
            var g = _groups[i];
            // Свёрнутая группа — это ровно заголовок: её карточек не видно.
            var count = 1 + (g.Collapsed ? 0 : Math.Max(0, g.Tasks));
            _spans[i] = new TaskRowSpan(row, count);
            row += count;
        }
        Count = row;
    }

    /// <summary>Всего строк в списке — длина полосы прокрутки.</summary>
    public int Count { get; }

    public int GroupCount => _groups.Length;

    public TaskRowSpan this[int groupIndex] => _spans[groupIndex];

    /// <summary>Группа, которой принадлежит строка; −1 — строки нет (список пуст либо индекс
    /// за его пределами).</summary>
    public int GroupOf(int row)
    {
        if (row < 0 || row >= Count) return -1;
        int lo = 0, hi = _spans.Length - 1, ans = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_spans[mid].FirstRow <= row) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }

    /// <summary>
    /// Что стоит в строке: номер группы, вид строки и — для карточки — её номер ВНУТРИ группы
    /// (0 — первая под заголовком). У заголовка номер не значит ничего и равен −1.
    /// Группа −1 означает, что строки нет.
    /// </summary>
    public (int Group, TaskRowKind Kind, int Local) Resolve(int row)
    {
        var gi = GroupOf(row);
        if (gi < 0) return (-1, TaskRowKind.Header, -1);

        var span = _spans[gi];
        return row == span.FirstRow
            ? (gi, TaskRowKind.Header, -1)
            : (gi, TaskRowKind.Task, row - span.FirstRow - 1);
    }
}
