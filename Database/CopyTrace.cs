using SmartBase.Core.Models;
using SmartBase.Utils;

namespace SmartBase.Database;

/// <summary>
/// След копирования значений таблицы в буфер обмена — в журнале безопасности, рядом с выгрузкой
/// в файл (<see cref="ExportTrace"/>).
///
/// Копирование выносит данные наружу так же, как выгрузка, только без файла: скопированное
/// уходит в любую другую программу. Оставлять его единственным бесследным каналом нельзя. Но и
/// правкой данных оно не является: раньше след лежал в журнале изменений как «записи», и
/// каждый Ctrl+C будил сценарии с триггером «изменение данных» и перечитывание таблицы у
/// соседей. Журнал безопасности читает проверяющий, а не триггеры, — место следа здесь.
///
/// <b>Правило то же, что у выгрузки: не записалось — не копируем.</b> Здесь оно даже проще:
/// объем и отпечаток известны ДО того, как текст ляжет в буфер, поэтому строка пишется первой, и
/// если она не легла, в буфер ничего не попадает.
///
/// В строке — имена столбцов, число строк и ячеек, объем и отпечаток скопированного текста;
/// самих значений нет, как и в следе выгрузки.
/// </summary>
public static class CopyTrace
{
    /// <summary>Сколько имен столбцов попадает в строку — тот же предел, что у выгрузки.</summary>
    public const int MaxFields = ExportTrace.MaxFields;

    /// <summary>Готов ли журнал принять след. Спрашивается до копирования.</summary>
    public static bool Ready => SecurityJournal.Current is not null;

    /// <summary>Почему копирование не состоялось — журнала нет.</summary>
    public const string NoJournal =
        "Журнал безопасности не открылся, и копирование отменено: вынос данных, о котором не "
        + "осталось записи, не допускается. Сообщите ответственному за интеграции.";

    /// <summary>Почему копирование не состоялось — строка не легла.</summary>
    public const string NotWritten =
        "След о копировании не лег в журнал безопасности, и в буфер обмена ничего не попало: "
        + "вынос данных, о котором не осталось записи, не допускается. Сообщите ответственному за интеграции.";

    /// <summary>
    /// Записать след копирования. <c>false</c> — следа нет, и вызывающий не должен класть текст
    /// в буфер.
    /// </summary>
    /// <param name="dbId">База, из которой скопировано.</param>
    /// <param name="collection">Имя коллекции — как его видит человек.</param>
    /// <param name="columns">Имена скопированных столбцов.</param>
    /// <param name="rows">Сколько строк таблицы скопировано.</param>
    /// <param name="cells">Сколько ячеек.</param>
    /// <param name="text">Скопированный текст — для объема и отпечатка; сам он не пишется.</param>
    /// <param name="label">Самая строгая метка среди базы и скопированных столбцов.</param>
    /// <param name="now">Момент. Передается явно — журнал проверяется тестами построчно.</param>
    public static bool Note(int dbId, string collection, IReadOnlyList<string> columns, int rows, int cells,
                            string text, ConfidentialityLabel label, DateTimeOffset now)
    {
        var mark = label == ConfidentialityLabel.Open ? "" : $"{Confidentiality.Title(label)}. ";

        return SecurityJournal.Add(SecurityEntry.New(
            SecurityEvent.Copy,
            allowed:     true,
            who:         CurrentUser.DisplayName,
            scenario:    "",
            address:     "буфер обмена",
            fields:      Fields(columns),
            rows:        rows,
            bytes:       System.Text.Encoding.UTF8.GetByteCount(text ?? ""),
            bodyHash:    SecurityJournal.Sha256(text ?? ""),
            explanation: $"{mark}скопировано в буфер обмена: «{collection}», ячеек: {cells:N0}",
            dbId:        dbId,
            at:          now));
    }

    // Имена столбцов через запятую, без повторов и не больше MaxFields — как у выгрузки.
    private static string Fields(IReadOnlyList<string> columns)
    {
        var names = columns.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return names.Count <= MaxFields
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(MaxFields)) + $" и еще {names.Count - MaxFields}";
    }
}
