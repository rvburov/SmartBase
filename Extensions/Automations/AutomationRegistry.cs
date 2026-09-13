using System.Reflection;

namespace SmartBase.Extensions.Automations;

/// <summary>
/// Реестр автоматизаций: находит их САМ, по сборке (docs/ROADMAP-API.md, шаг 13).
///
/// <b>Список нигде не перечислен, и это главная мысль шага.</b> Автоматизация должна заводиться
/// одним файлом: скопировал заготовку, вписал логику, собрал — и новый класс уже стоит в списке
/// шагов сценария. Список в коде означал бы второе место, которое надо не забыть, а забытое
/// второе место — это автоматизация, которой нет в интерфейсе, и полдня на выяснение почему.
///
/// <b>Это не загрузка чужих DLL.</b> Ищутся типы СВОЕЙ сборки, приехавшей дистрибутивом и
/// прошедшей анализатор запрещенных вызовов; папки с плагинами программа не читает и читать не
/// будет (docs/ROADMAP-API.md, раздел 13).
///
/// Поиск идет один раз за запуск — список ленивый и статический: перебор типов сборки стоит
/// миллисекунды, но делать его на каждую перерисовку окна сценария незачем.
/// </summary>
public static class AutomationRegistry
{
    /// <summary>Пространство имен, в котором живут автоматизации, — папка <c>Extensions/Automations/</c>.</summary>
    private const string Home = "SmartBase.Extensions.Automations";

    /// <summary>Все автоматизации по имени.</summary>
    public static IReadOnlyList<IAutomation> All { get; } = Discover();

    /// <summary>Автоматизация по ключу; <c>null</c> — такой нет.</summary>
    public static IAutomation? FindByKey(string key) =>
        All.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal));

    /// <summary>
    /// Проверить, что ключи не повторяются. Совпадение молча ломает шаг сценария: он хранит
    /// ключ, а по ключу нашлась бы не та автоматизация — и обмен делал бы не то, что настроили.
    /// Ошибка возможна только при правке кода, поэтому падаем на старте с явным текстом.
    /// </summary>
    public static void EnsureUniqueKeys()
    {
        var duplicates = All.GroupBy(a => a.Key, StringComparer.Ordinal)
                            .Where(g => g.Count() > 1)
                            .Select(g => g.Key)
                            .ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException(
                $"Повторяющийся ключ автоматизации: {string.Join(", ", duplicates)}. "
                + "Ключ должен быть уникальным — см. Extensions/Automations/.");
    }

    /// <summary>
    /// Найти классы автоматизаций в своей сборке. Берутся только те, что создаются без
    /// аргументов: автоматизация — это логика над строками, и зависимостей у нее быть не должно.
    /// Абстрактные и вложенные в тесты классы отсеиваются тем же правилом.
    /// </summary>
    private static IReadOnlyList<IAutomation> Discover()
    {
        var made = new List<IAutomation>();

        foreach (var type in Types())
        {
            if (type.IsAbstract || type.IsInterface || !type.IsClass)  continue;
            if (!typeof(IAutomation).IsAssignableFrom(type))           continue;
            if ((type.Namespace ?? "") != Home)                        continue;
            if (type.GetConstructor(Type.EmptyTypes) is null)          continue;

            if (Activator.CreateInstance(type) is IAutomation automation) made.Add(automation);
        }

        // Порядок — по имени: список шагов человек читает глазами, а порядок отражения типов в
        // сборке не значит ничего и меняется от перестановки файлов.
        return [.. made.OrderBy(a => a.Name, StringComparer.CurrentCulture)];
    }

    /// <summary>
    /// Типы сборки. Не загрузившиеся пропускаются молча: одна автоматизация, потерявшая тип,
    /// не должна уносить с собой список остальных.
    /// </summary>
    private static IEnumerable<Type> Types()
    {
        try
        {
            return typeof(AutomationRegistry).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException failure)
        {
            return failure.Types.Where(t => t is not null)!;
        }
    }
}
