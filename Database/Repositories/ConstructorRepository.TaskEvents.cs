using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;
using SmartBase.Utils;

namespace SmartBase.Database.Repositories;

// ── События задач: очередь уведомлений (task_events) ──────────────────────────
//
// Два вида уведомлений и ровно два повода их записать:
//   • assigned — исполнителем задачи поставили ДРУГОГО человека (свое назначение не
//     уведомляет: баннер о задаче, которую сам себе и завел, — чистый шум);
//   • status   — у задачи сменилась стадия; получатель — тот, кто указан в свойстве
//     «Назначил», в том числе когда стадию сменил он сам.
//
// Пишется из ЕДИНОЙ точки правки значения (SetFieldValue) — через нее идут все
// поверхности: раздел «Задачи», ячейка таблицы, страница записи, календарь. Массовые
// пути (импорт, дубль, перенос, автозаполнение при создании записи) пишут значения
// мимо нее и уведомлений намеренно не дают: на их объемах это тысячи строк очереди.
public partial class ConstructorRepository
{
    /// <summary>Сколько держим события: очередь доставки, а не история — ретеншен без настройки.</summary>
    public const int TaskEventsRetentionDays = 90;

    // Копирование значений в ДРУГУЮ запись событий не порождает (см. SuppressTaskEvents).
    private bool _suppressTaskEvents;

    /// <summary>Метаданные правимого поля: журналу нужны имя и тип, событиям задач — еще и роли.
    /// Читаются ОДНИМ запросом внутри SetFieldValue, который там был и раньше.</summary>
    private sealed record EditedFieldMeta(string Name, string FieldType, bool IsTaskExec, bool IsTaskStatus);

    /// <summary>
    /// Записать событие задачи, если правили поле-роль. Строка ложится в тот же контекст и
    /// коммитится ближайшим <c>SaveChanges</c> — вместе со значением и строкой журнала.
    ///
    /// Старым значением сюда идет ФАКТИЧЕСКОЕ значение ячейки до правки, а не слитое «до»
    /// журнала: слияние побуквенных правок — оформление истории, а событие — факт перехода.
    ///
    /// Сбой уведомления не должен отменять сохранение значения, поэтому тело целиком под
    /// <c>try/catch</c>: не записалось — человек просто не получит баннер.
    /// </summary>
    private void TryWriteTaskEvent(int recordId, string taskTitle, int collectionId,
                                   int fieldId, EditedFieldMeta? f, string oldValue, string newValue)
    {
        if (_suppressTaskEvents || f is null) return;

        // Роль = флаг И подходящий тип (правило HasRole): флаг, оставшийся на поле после
        // смены типа, роли не несет — иначе правка обычного текста рождала бы события.
        var isExec   = f.IsTaskExec   && RoleAllowsType(FieldRole.TaskExec,   f.FieldType);
        var isStatus = f.IsTaskStatus && RoleAllowsType(FieldRole.TaskStatus, f.FieldType);
        if (!isExec && !isStatus) return;

        // Значение сняли (исполнителя убрали, стадию очистили) — уведомлять некого и не о чем.
        if (newValue.Length == 0) return;

        // Стадию проставили ВПЕРВЫЕ — это создание задачи, а не смена стадии. Без этой
        // отсечки каждое добавление задачи сразу давало бы баннер «стадия: Запланировано»
        // (AddTaskRecord пишет начальный статус в пустое поле).
        if (isStatus && oldValue.Length == 0) return;

        try
        {
            string recipientName;
            if (isExec)
            {
                recipientName = newValue;
            }
            else
            {
                // Стадия: получатель — текущее значение поля «Назначил» этой коллекции.
                if (TaskRoleFields(collectionId).AssignerId is not int assignerFieldId) return;
                recipientName = db.DbFieldValues.AsNoTracking()
                    .Where(v => v.RecordId == recordId && v.FieldId == assignerFieldId)
                    .Select(v => v.Value).FirstOrDefault() ?? "";
                if (recipientName.Length == 0) return;   // задачу никто не назначал
            }

            var recipientKey = ResolveUserKey(recipientName);

            // Свое назначение не уведомляем. Стадию — уведомляем всегда: назначивший ждет
            // сигнала о движении задачи, даже когда двигает ее сам.
            if (isExec && IsMe(recipientKey, recipientName)) return;

            db.TaskEvents.Add(new TaskEventEntity
            {
                Ts            = DateTime.UtcNow.ToString("o"),
                Kind          = isExec ? TaskEventKinds.Assigned : TaskEventKinds.Status,
                RecipientKey  = recipientKey,
                RecipientName = recipientName,
                ActorKey      = CurrentUser.Key,
                ActorName     = CurrentUser.DisplayName,
                RecordId      = recordId,
                CollectionId  = collectionId,
                FieldId       = fieldId,
                // Задача = запись × ЕЕ поле-исполнитель: одного RecordId для перехода по клику
                // мало. Правили исполнителя — это он и есть, иначе спрашиваем у коллекции.
                ExecFieldId    = isExec ? fieldId : TaskExecFieldId(collectionId) ?? 0,
                TaskTitle      = taskTitle,
                CollectionName = CollectionNameOf(collectionId) ?? "",
                OldValue       = oldValue,
                NewValue       = newValue,
            });
        }
        catch { /* уведомление не критично: правку значения оно отменять не вправе */ }
    }

    // Имя из ячейки → ключ пользователя базы. Сравнение идет В ПАМЯТИ и без учета регистра:
    // db_users — десятки строк, а SQL-сравнение кириллицы без учета регистра SQLite не умеет
    // (тот же довод, что у MeVariants в разделе «Задачи»). Не нашли — ключ пустой, получателя
    // потом опознает по имени сам клиент.
    private string ResolveUserKey(string displayName) =>
        db.DbUsers.AsNoTracking()
          .Select(u => new { u.UserKey, u.DisplayName })
          .AsEnumerable()
          .FirstOrDefault(u => string.Equals(u.DisplayName, displayName,
                                             StringComparison.CurrentCultureIgnoreCase))
          ?.UserKey ?? "";

    private static bool IsMe(string userKey, string displayName) =>
        userKey.Length > 0
            ? string.Equals(userKey, CurrentUser.Key, StringComparison.OrdinalIgnoreCase)
            : string.Equals(displayName, CurrentUser.DisplayName, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>
    /// Отключить события задач на время копирования значений в ДРУГУЮ запись: перенос — не
    /// назначение, исполнитель у задачи не менялся. Вложенность безопасна: восстанавливается
    /// прежнее состояние, а не «выключено».
    /// </summary>
    private IDisposable SuppressTaskEvents()
    {
        var previous = _suppressTaskEvents;
        _suppressTaskEvents = true;
        return new TaskEventSuppression(this, previous);
    }

    private sealed class TaskEventSuppression(ConstructorRepository owner, bool previous) : IDisposable
    {
        public void Dispose() => owner._suppressTaskEvents = previous;
    }

    // ── Чтение очереди (фоновый тик уведомлений) ──────────────────────────────

    /// <summary>Текущий хвост очереди — посев курсора при первом знакомстве с базой.
    /// Обратный скан PK, микросекунды.</summary>
    public long MaxTaskEventIdIsolated()
    {
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyPerformancePragmas();
        return ctx.TaskEvents.Max(e => (long?)e.Id) ?? 0;
    }

    /// <summary>
    /// Мои события после курсора — на СВОЕМ свежем подключении (зовется из фонового тика,
    /// общий контекст однопоточный). Возвращает события и НОВЫЙ курсор.
    ///
    /// Сначала дешевый детектор «хвост вырос?» (MAX по PK): в обычном тике очередь пуста и
    /// второго запроса не будет вовсе — стоимость не зависит от размера базы. Курсор двигаем
    /// на хвост, а не на «последнее мое»: иначе чужие события перечитывались бы вечно.
    /// </summary>
    public (List<TaskEvent> Events, long Cursor) ReadTaskEventsAfterIsolated(
        long cursor, string recipientKey, string recipientName, int limit = 200)
    {
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyPerformancePragmas();

        var max = ctx.TaskEvents.Max(e => (long?)e.Id) ?? 0;
        if (max <= cursor) return ([], Math.Max(cursor, max));

        // Строки без разрешенного ключа берем тоже и отсеиваем по имени уже в памяти:
        // сравнение кириллицы без учета регистра в SQL недоступно.
        var rows = ctx.TaskEvents.AsNoTracking()
            .Where(e => e.Id > cursor && e.Id <= max &&
                        (e.RecipientKey == recipientKey || e.RecipientKey == ""))
            .OrderBy(e => e.Id)
            .Take(limit)
            .ToList();

        var mine = rows
            .Where(e => e.RecipientKey.Length > 0 ||
                        string.Equals(e.RecipientName, recipientName,
                                      StringComparison.CurrentCultureIgnoreCase))
            .Select(e => new TaskEvent
            {
                EventId        = e.Id,
                Kind           = e.Kind,
                Ts             = DateTime.TryParse(e.Ts, null,
                                     System.Globalization.DateTimeStyles.RoundtripKind, out var ts)
                                     ? ts.ToLocalTime() : DateTime.Now,
                ActorName      = e.ActorName,
                RecordId       = e.RecordId,
                CollectionId   = e.CollectionId,
                FieldId        = e.FieldId,
                ExecFieldId    = e.ExecFieldId,
                Title          = e.TaskTitle,
                CollectionName = e.CollectionName,
                OldValue       = e.OldValue,
                NewValue       = e.NewValue,
            })
            .ToList();

        // Взяли неполный хвост (уперлись в лимит) — курсор двигаем только до последней
        // прочитанной строки, остальное приедет следующим тиком.
        var reached = rows.Count == limit ? rows[^1].Id : max;
        return (mine, reached);
    }

    /// <summary>
    /// Автоочистка очереди при монтировании: события старше <see cref="TaskEventsRetentionDays"/>
    /// суток уже не доставить осмысленно. Свежее подключение в фоне, best-effort — как у
    /// автоочистки журнала.
    ///
    /// Возвращает запущенную задачу: ее ДОЛЖЕН дождаться DatabaseManager.Dispose — иначе
    /// чистка переживает закрытие базы и продолжает держать файл (см. подробности
    /// в <see cref="ApplyChangeLogRetention"/>). В отличие от журнальной, эта запускается
    /// всегда, поэтому именно она и держала файл в большинстве случаев.
    /// </summary>
    public Task ApplyTaskEventsRetention()
    {
        var cutoff = DateTime.UtcNow.AddDays(-TaskEventsRetentionDays).ToString("o");
        var path   = db.DbPath;
        return Task.Run(() =>
        {
            try
            {
                using var ctx = new AppDbContext(path);
                ctx.ApplyPerformancePragmas();
                ctx.TaskEvents.Where(e => string.Compare(e.Ts, cutoff) < 0).ExecuteDelete();
            }
            catch { /* не критично: повторится при следующем монтировании */ }
        });
    }
}
