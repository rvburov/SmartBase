namespace SmartBase.Database;

/// <summary>
/// Копилка своих правок для триггера «изменение данных» (docs/ROADMAP-API.md, раздел 2):
/// помнит, в какой коллекции какой базы последний раз что-то менялось.
///
/// <b>Своих, а не чужих.</b> Слушается <see cref="ChangeBus"/> — коммиты ЭТОГО процесса, — а не
/// <c>ChangePoller</c>, который ловит правки соседей по хвосту <c>change_log</c>. База открыта на
/// пяти машинах, и правка, видимая всем, завела бы сценарий на каждой: один обмен превратился бы
/// в пять одновременных. Обмен принадлежит той машине, где его настроили.
///
/// <b>Обработчик только запоминает факт и уходит.</b> Событие шины поднимается синхронно внутри
/// <c>SaveChanges</c> и на том же потоке, где шла запись, — любая работа с базой отсюда была бы
/// реентерабельным вызовом контекста посреди его сохранения. Поэтому здесь словарь под замком и
/// ни одного обращения к диску.
///
/// <b>Решает не эта копилка, а служба.</b> Она лишь отвечает на вопрос «когда в последний раз
/// менялось»; ждать ли тишины и пора ли запускать — дело <see cref="ScenarioService"/>, у
/// которого и часы, и реестр. Разделение то же, что у расписания: здесь «что случилось», там
/// «пора ли».
///
/// Считаются только правки ДАННЫХ. Добавленная колонка или переименованная коллекция — не
/// «данные изменились», и обмен от них не уходит: уйдет он с теми же записями, что и до правки.
/// </summary>
public sealed class ChangeWatch : IDisposable
{
    /// <summary>За какой коллекцией какой базы следим.</summary>
    /// <param name="DbPath">Путь к файлу базы (или строка подключения) — как его знает шина.</param>
    /// <param name="Collection">Имя коллекции — как оно попадает в журнал изменений.</param>
    private readonly record struct Spot(string DbPath, string Collection);

    private readonly Lock                        _lock = new();
    private readonly Dictionary<Spot, DateTimeOffset> _seen = [];

    private Action<string, IReadOnlyList<ChangeBus.Change>>? _handler;

    /// <summary>
    /// Виды правок, которые считаются изменением ДАННЫХ. Словами журнала изменений: одна запись
    /// и пачка записей.
    /// </summary>
    public static bool IsData(string targetType) => targetType is "record" or "records";

    /// <summary>Подписаться на шину. Идемпотентно: окно возвращается из трея снова и снова.</summary>
    public void Attach()
    {
        if (_handler is not null) return;

        _handler = (dbPath, changes) =>
        {
            var at = DateTimeOffset.Now;

            foreach (var change in changes)
            {
                if (!IsData(change.TargetType))                    continue;
                if (change.CollectionName is not { Length: > 0 } name) continue;

                Note(dbPath, name, at);
            }
        };

        ChangeBus.Committed += _handler;
    }

    public void Dispose()
    {
        if (_handler is null) return;

        ChangeBus.Committed -= _handler;
        _handler = null;

        lock (_lock) _seen.Clear();
    }

    /// <summary>
    /// Запомнить правку. Открыт наружу ради тестов: так триггер проверяется без шины и без
    /// настоящих коммитов — тем же приемом, каким расписание проверяется без подведения часов.
    /// </summary>
    public void Note(string dbPath, string collection, DateTimeOffset at)
    {
        if (string.IsNullOrEmpty(dbPath) || string.IsNullOrEmpty(collection)) return;

        lock (_lock) _seen[Key(dbPath, collection)] = at;
    }

    /// <summary>Когда здесь последний раз менялись данные; <c>null</c> — не менялись.</summary>
    public DateTimeOffset? LastChange(string dbPath, string collection)
    {
        if (string.IsNullOrEmpty(dbPath) || string.IsNullOrEmpty(collection)) return null;

        lock (_lock)
            return _seen.TryGetValue(Key(dbPath, collection), out var at) ? at : null;
    }

    /// <summary>
    /// Забыть правки — сценарий их отработал. Зовется в НАЧАЛЕ запуска, а не в конце: правка,
    /// пришедшая во время работы, должна завести сценарий заново, а не потеряться вместе с
    /// отметкой.
    /// </summary>
    public void Forget(string dbPath, string collection)
    {
        if (string.IsNullOrEmpty(dbPath) || string.IsNullOrEmpty(collection)) return;

        lock (_lock) _seen.Remove(Key(dbPath, collection));
    }

    /// <summary>Сколько мест помнится. Для «Диагностики» и тестов.</summary>
    public int Count
    {
        get { lock (_lock) return _seen.Count; }
    }

    /// <summary>
    /// Ключ места. Путь сравнивается без учета регистра: Windows отдает один и тот же файл то
    /// как <c>P:\Обмен</c>, то как <c>p:\обмен</c>, и разный регистр развел бы одну базу на две.
    /// Имя коллекции — тоже: тезки в одной базе запрещены самим конструктором.
    /// </summary>
    private static Spot Key(string dbPath, string collection) =>
        new(dbPath.ToLowerInvariant(), collection.ToLowerInvariant());
}
