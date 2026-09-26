using System.Collections.Concurrent;
using System.Diagnostics;
using Serilog;
using SmartBase.Utils;

namespace SmartBase.Logging;

/// <summary>
/// Диагностика работоспособности: замер операций с порогом медленности, реестр активных
/// операций для сторожа UI-потока (<see cref="UiWatchdog"/>) и защита журнала от шума
/// (разовые и редкие записи).
///
/// Правило: в журнал уходит не факт операции, а ее ОТКЛОНЕНИЕ — операция закончилась, но
/// дольше порога. Штатно быстрые операции не пишутся вовсе, иначе журнал за день зарастает
/// строками «страница загружена за 40 мс». Пороги — по виду работы и по тому, ждет ли ее
/// человек: открытие раздела, коллекции, окна — <see cref="UiOpen"/>; фильтр, сортировка,
/// поиск — <see cref="UiQuery"/>; страница при прокрутке — <see cref="UiPage"/>; фоновая
/// работа, которую интерфейс не ждет, — <see cref="Background"/>; монтирование базы —
/// <see cref="Mount"/>. Пороги человеческого ожидания — доли секунды (этап 8 плана
/// drawn-grid.md): журнал сам показывает все, что не моментально.
///
/// Использование: <c>using var _ = Diag.Measure("открытие коллекции", Diag.UiOpen, "«Свод», 650 000 записей");</c>
/// В записи — операция, длительность, порог и контекст (база, коллекция, объем): по ней видно,
/// что тормозит конкретная общая база на сотни тысяч строк, а не программа вообще.
/// </summary>
public static class Diag
{
    /// <summary>Открытие раздела, коллекции, страницы записи, окна; окно календаря и список
    /// раздела — человек ждет содержимое.</summary>
    public static readonly TimeSpan UiOpen  = TimeSpan.FromMilliseconds(300);
    /// <summary>Фильтр, сортировка, список значений фильтра, поиск, вид раздела — человек ждет
    /// результат.</summary>
    public static readonly TimeSpan UiQuery = TimeSpan.FromMilliseconds(500);
    /// <summary>Страница строк при прокрутке: до ее прихода на месте строк заглушки.</summary>
    public static readonly TimeSpan UiPage  = TimeSpan.FromMilliseconds(200);
    /// <summary>Фоновая работа, которую интерфейс не ждет: счет записей для «из N», индексы
    /// строк с ручной высотой, пилюли фильтров. Пишется, только когда она непомерна.</summary>
    public static readonly TimeSpan Background = TimeSpan.FromSeconds(5);
    /// <summary>Монтирование базы (миграции, сжатие, индексация).</summary>
    public static readonly TimeSpan Mount   = TimeSpan.FromSeconds(60);

    // Активные операции: имя → глубина вложенности. Сторож UI-потока называет их в записи
    // о зависании — «висело 7 с» без «при открытии коллекции» ничего не объясняет.
    private static readonly ConcurrentDictionary<string, int> _active = new();

    /// <summary>Имена операций, которые идут прямо сейчас (через запятую; пусто — ни одной).</summary>
    public static string ActiveOperations => string.Join(", ", _active.Keys.OrderBy(k => k, StringComparer.Ordinal));

    /// <summary>Наблюдатель завершенных замеров — для стендов (<c>Tests/Sections</c>): получает
    /// каждую операцию с длительностью и контекстом, а не только медленные. В программе не задан.</summary>
    internal static Action<string, TimeSpan, string?>? Observer;

    /// <summary>Начать замер операции: по <c>Dispose</c> запись в журнал, если длительность
    /// превысила <paramref name="threshold"/>. <paramref name="context"/> — что именно
    /// открывали/считали (база, коллекция, объем); его можно уточнить позже через
    /// <see cref="Scope.Context"/>, когда объем станет известен.</summary>
    public static Scope Measure(string operation, TimeSpan threshold, string? context = null) =>
        new(operation, threshold, context);

    /// <summary>Замер одной операции. Потокобезопасен для вложенных и параллельных замеров.</summary>
    public sealed class Scope : IDisposable
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly TimeSpan  _threshold;
        private bool _done;

        public string  Operation { get; }
        /// <summary>Контекст записи: база, коллекция, объем. Можно дополнить до Dispose.</summary>
        public string? Context   { get; set; }
        public TimeSpan Elapsed  => _watch.Elapsed;

        internal Scope(string operation, TimeSpan threshold, string? context)
        {
            Operation  = operation;
            _threshold = threshold;
            Context    = context;
            _active.AddOrUpdate(operation, 1, (_, n) => n + 1);
        }

        /// <summary>Операцию обогнала новая (другой вид, другие галочки) и ее результат выброшен:
        /// замер снимается без записи — брошенная загрузка медленностью не считается.</summary>
        public void Discard()
        {
            if (_done) return;
            _done = true;
            _watch.Stop();
            Release(Operation);
            Observer?.Invoke(Operation + " (брошена)", _watch.Elapsed, Context);
        }

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _watch.Stop();
            Release(Operation);
            Observer?.Invoke(Operation, _watch.Elapsed, Context);
            if (_watch.Elapsed <= _threshold) return;
            Log.Warning("Медленно: {Operation:l} — {Ms:N0} мс при пороге {ThresholdMs:N0} мс{Context:l}",
                        Operation, _watch.Elapsed.TotalMilliseconds, _threshold.TotalMilliseconds,
                        string.IsNullOrEmpty(Context) ? "" : " · " + Context);
        }
    }

    private static void Release(string operation)
    {
        while (_active.TryGetValue(operation, out var n))
        {
            if (n <= 1) { if (_active.TryRemove(new KeyValuePair<string, int>(operation, n))) return; }
            else if (_active.TryUpdate(operation, n - 1, n)) return;
        }
    }

    // ── Защита от шума ───────────────────────────────────────────────────────────

    private static readonly ConcurrentDictionary<string, byte> _once     = new();
    private static readonly ConcurrentDictionary<string, long> _throttle = new();

    /// <summary>Разовая запись за сеанс: первый вызов с ключом — true, остальные — false.
    /// Для дефектов, которые иначе писались бы на каждом обращении (поврежденный конфиг
    /// свойства читается при каждой отрисовке ячейки).</summary>
    public static bool Once(string key) => _once.TryAdd(key, 0);

    /// <summary>Не чаще раза в <paramref name="period"/> на ключ: первый вызов и каждый
    /// после истечения периода — true. Для серий отказов (чтение при недоступной базе
    /// срабатывает на каждый скролл).</summary>
    public static bool Throttle(string key, TimeSpan period)
    {
        var now = Stopwatch.GetTimestamp();
        var min = (long)(period.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            if (!_throttle.TryGetValue(key, out var last))
            {
                if (_throttle.TryAdd(key, now)) return true;
                continue;
            }
            if (now - last < min) return false;
            if (_throttle.TryUpdate(key, now, last)) return true;
        }
    }

    /// <summary>Сброс состояния между тестами.</summary>
    public static void ResetForTests()
    {
        _active.Clear();
        _once.Clear();
        _throttle.Clear();
    }

    // ── Форматирование для записей ───────────────────────────────────────────────

    /// <summary>Размер по-человечески: «812 КБ», «1,7 ГБ».</summary>
    public static string Size(long bytes) => Sizes.Text(bytes, tiny: true);

    /// <summary>Длительность по-человечески: «48 с», «12 мин», «2 ч 15 мин».</summary>
    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.FromMinutes(1)) return $"{t.TotalSeconds:0} с";
        if (t < TimeSpan.FromHours(1))   return $"{t.TotalMinutes:0} мин";
        return t.Minutes == 0 ? $"{(int)t.TotalHours} ч" : $"{(int)t.TotalHours} ч {t.Minutes} мин";
    }
}
