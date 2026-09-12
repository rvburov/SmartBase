using SmartBase.Core.Importers;
using SmartBase.Core.Importers.TableSources;
using SmartBase.Database.Repositories;
using SmartBase.Integrations;

namespace SmartBase.Database;

/// <summary>
/// Укладка входящего сообщения в коллекцию — <b>общая часть обоих транспортов</b>.
///
/// Файл из папки обмена и тело HTTP-запроса приходят разными путями, но дальше обязаны идти
/// одним и тем же кодом (docs/ROADMAP-API.md, раздел 7): две реализации одной укладки разошлись
/// бы на первой же правке, и сообщение, принятое папкой, стало бы отклоняться по сети — или, что
/// хуже, наоборот.
///
/// Что остается у транспорта и сюда не попадает: чтение (файл или сокет), <b>проверка формата</b>
/// (<see cref="InboxMessage.Problems"/> спрашивается ДО памяти о повторах — негодное сообщение не
/// должно попадать в нее вовсе) и сама память о повторах: у папки это квитанция рядом с файлом,
/// у сети — своя таблица. Разница не случайная, она из 8-го раздела плана: вторая доставка файла
/// вполне может попасть на СОСЕДНЮЮ машину, а второе обращение по <c>127.0.0.1</c> — никогда.
/// </summary>
public static class InboxIntake
{
    /// <summary>
    /// Уложить записи сообщения и ответить квитанцией. Исключений не бросает: отказ — это такой
    /// же ответ отправителю, как и принятие, и молчание вместо него хуже любой причины.
    /// </summary>
    /// <param name="message">Разобранное и проверенное сообщение.</param>
    /// <param name="repo">Репозиторий целевой базы.</param>
    /// <param name="dbId">Ключ базы — привязка обязана называть базу явно.</param>
    /// <param name="source">Чем подписать квитанцию: имя файла либо имя канала.</param>
    /// <param name="now">Момент обработки.</param>
    public static InboxReceipt Apply(InboxMessage message, ConstructorRepository repo, int dbId,
                                     string source, DateTimeOffset now)
    {
        // Записи читает ТОТ ЖЕ источник, что и ответ чужой программы: один и тот же набор
        // данных, пришедший файлом и по сети, обязан ложиться в коллекцию одинаково.
        var reader = new JsonTextSource(message.RowsJson);

        ImportSchema schema;
        try
        {
            schema = reader.Analyze(null, CancellationToken.None);
        }
        catch (ImportException failure)
        {
            return Reject(message, source, failure.Message, now);
        }

        // Свод и укладка идут на ОТДЕЛЬНОМ подключении: и обход папки, и обработка обращения
        // живут в фоне, а общий контекст в это время занят интерфейсом.
        var plan = repo.TryPlanIsolated(message, schema, dbId, out var problem);

        if (plan is null) return Reject(message, source, problem, now);

        BindingResult result;
        try
        {
            result = repo.ApplyRowsIsolated(plan.Binding, schema,
                                            reader.ReadRows(CancellationToken.None),
                                            CancellationToken.None);
        }
        catch (Exception failure) when (failure is ImportException or InvalidOperationException)
        {
            return Reject(message, source, failure.Message, now);
        }

        // Про выброшенные колонки отправитель узнает из квитанции. Чужая программа отдает
        // лишнее почти всегда, и это не ошибка; но молча выбросить присланное — худшее, что
        // может сделать приемник: отправитель годами будет думать, что колонка доходит.
        var note = plan.Ignored.Count > 0
            ? "не перенесены колонки: " + string.Join(", ", plan.Ignored)
            : "";

        return InboxReceipt.Accept(source, message.MessageId, message.Collection,
                                   message.Rows, result, now, note);
    }

    private static InboxReceipt Reject(InboxMessage message, string source, string explanation,
                                       DateTimeOffset now) =>
        InboxReceipt.Rejected(source, message.MessageId, message.Collection, message.Rows,
                              explanation, now);
}
