namespace SmartBase.Core.Models;

/// <summary>
/// Человеческие подписи строк журнала изменений: действие, тип объекта, текст объекта
/// и время. Один источник для таблицы «История изменений» и для выгрузки журнала в CSV —
/// иначе файл и экран расходились бы в формулировках («update» против «Изменение»),
/// и читать выгрузку пришлось бы со словарем.
///
/// Работает с примитивами (а не со строкой репозитория) — чтобы жить в Core и быть
/// доступным и слою данных, и интерфейсу.
/// </summary>
public static class ChangeLogLabels
{
    public static string ActionLabel(string action) => action switch
    {
        "create"       => "Создание",
        "update"       => "Изменение",
        "delete"       => "Удаление",
        "duplicate"    => "Дублирование",
        "import"       => "Импорт",
        "export"       => "Экспорт",
        "move"         => "Перенос",
        "user.join"    => "Присоединение",
        "user.approve" => "Допуск",
        "user.revoke"  => "Отзыв допуска",
        "user.rights"  => "Изменение прав",
        "user.role"    => "Смена роли",
        "user.remove"  => "Удаление пользователя",
        "user.unlock"  => "Доступ по паролю",
        "user.rename"  => "Смена имени",
        _              => action,
    };

    public static string TypeLabel(string targetType) => targetType switch
    {
        "collection" => "Коллекция",
        "group"      => "Группа",
        "field"      => "Свойство",
        "record"     => "Запись",
        "records"    => "Записи",
        "user"       => "Пользователь",
        _            => targetType,
    };

    /// <summary>
    /// Объект строки: правки с «до → после» показывают готовый Detail («„А" → „Б"»,
    /// «Договор 12 (Телефон: «123» → «456»)»); иначе имя цели; пакетные операции —
    /// «10 000 записей», дубль/удаление коллекции с записями — «Имя (N записей)».
    /// </summary>
    public static string ObjectText(string targetType, string targetName, long? count, string? detail)
    {
        if (!string.IsNullOrEmpty(detail)) return detail;
        if (targetType == "records") return RecordsCount(count ?? 0);
        if (targetName.Length == 0)   return "—";
        return targetType is "collection" or "group" && count is > 0
            ? $"{targetName} ({RecordsCount(count.Value)})"
            : targetName;
    }

    /// <summary>Русское склонение: 1 запись, 2 записи, 10 000 записей.</summary>
    public static string RecordsCount(long n)
    {
        var word = (n % 100 is >= 11 and <= 14) ? "записей" : (n % 10) switch
        {
            1           => "запись",
            2 or 3 or 4 => "записи",
            _           => "записей",
        };
        return $"{n:N0} {word}";
    }

    /// <summary>
    /// ISO UTC → локальное «дд.MM.гггг ЧЧ:мм». В выгрузке просят секунды
    /// (<paramref name="withSeconds"/>): по журналу разбирают порядок событий внутри минуты.
    /// </summary>
    public static string FormatTs(string iso, bool withSeconds = false) =>
        DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToLocalTime().ToString(withSeconds ? "dd.MM.yyyy HH:mm:ss" : "dd.MM.yyyy HH:mm")
            : iso;
}
