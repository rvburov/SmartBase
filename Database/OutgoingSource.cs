using SmartBase.Integrations;

namespace SmartBase.Database;

/// <summary>
/// Откуда берется тело исходящего запроса, когда его несет коллекция.
///
/// Между исходящей привязкой (<see cref="OutgoingBinding"/> — «какие поля и под какими именами»)
/// и отправкой (<c>RequestRunner</c> — «что уходит в сокет») лежит работа с базой: найти
/// открытую базу, прочитать строки и сложить тело. Она собрана здесь
/// одним вызовом, а не повторена у каждой кнопки, ровно по той же причине, что и замок на
/// отправке: повторенная проверка однажды повторяется не целиком.
///
/// <b>Тело собирается ОДИН раз на запуск и передается всем.</b> Сухой прогон и сама отправка обязаны говорить об одном и том же теле; собери его каждый
/// заново — и экран показывал бы одно, в журнал легло бы второе, а ушло бы третье.
/// </summary>
public static class OutgoingSource
{
    /// <summary>
    /// Собрать тело из коллекции.
    ///
    /// <c>(null, null)</c> — у запроса нет исходящей привязки: он ничего из коллекции не уносит,
    /// и тело берется из карточки как напечатано. Это обычный случай, а не ошибка.
    /// </summary>
    /// <param name="registry">Реестр интеграций — в нем лежит привязка.</param>
    /// <param name="request">Запрос, который собираются запустить.</param>
    /// <param name="databases">Базы, открытые на этой машине прямо сейчас.</param>
    /// <param name="records">Какие записи отправлять; <c>null</c> или пусто — всю коллекцию.
    /// Список приходит от точки вызова в разделе данных: человек выделил строки и позвал
    /// действие для них.</param>
    /// <param name="ct">Отмена.</param>
    /// <returns>Готовое тело либо причина, по которой его не собрать, — готовой фразой.</returns>
    public static (OutgoingPayload? Payload, string? Trouble) Build(
        IntegrationsRegistry           registry,
        IntegrationRequest             request,
        IReadOnlyList<DatabaseManager> databases,
        IReadOnlyList<int>?            records = null,
        CancellationToken              ct      = default)
    {
        if (registry.Outgoing(request.Id) is not { } binding) return (null, null);

        if (binding.Problems() is { Count: > 0 } problems)
            return (null, "исходящая привязка неполна: " + string.Join("; ", problems));

        if (databases.FirstOrDefault(d => d.DbId == binding.DbId) is not { } source)
            return (null, "база данных, записи которой уходят, сейчас не открыта");

        var limit = binding.Single ? 1 : int.MaxValue;

        var read = source.Constructor.ReadOutgoingIsolated(binding, limit, ct, records);

        if (read.Trouble is { Length: > 0 } trouble) return (null, trouble);

        if (binding.Single && read.Rows.Count > 1)
            return (null, $"в коллекции «{read.Collection}» больше одной записи, а этот обмен "
                        + "настроен отправлять одну");

        if (binding.Single && read.Rows.Count == 0)
            return (null, $"в коллекции «{read.Collection}» нет записи, которую надо отправить");

        try
        {
            return (OutgoingBody.Build(binding, read.Columns, read.Rows, read.Collection), null);
        }
        catch (OutgoingBodyLimitException error)
        {
            return (null, error.Message);
        }
    }
}
