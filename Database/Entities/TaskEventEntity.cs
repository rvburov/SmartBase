using System.ComponentModel.DataAnnotations;

namespace SmartBase.Database.Entities;

// ── События задач: очередь уведомлений внутри базы конструктора ────────────────
//
// Строку пишет ТОТ, КТО ПРАВИТ (ConstructorRepository.TryWriteTaskEvent), а адресована
// она конкретному получателю: исполнителю — «вам назначена задача», тому, кто указан
// в свойстве «Назначил», — «задача сменила стадию». Клиент получателя читает свои строки
// после курсора (Notifications/TaskEventReader) и показывает баннеры.
//
// Почему очередь в базе, а не сравнение слепков состояния на клиенте: строка знает
// АВТОРА правки и старое значение, поэтому «я назначил задачу сам себе» отличимо от
// «меня назначили», а стоимость опроса не зависит от размера базы (SELECT MAX(Id)
// обратным сканом PK — тот же прием, что у ChangePoller).
//
// Живет вместе с базой конструктора, как журнал изменений: события относятся к ЕЕ
// задачам и путешествуют с ее файлом. Ретеншен — 90 суток при монтировании
// (ConstructorRepository.ApplyTaskEventsRetention).
public class TaskEventEntity
{
    // PK/rowid: он же курсор чтения и хронология. Диапазон «Id > курсор» и есть
    // весь горячий путь опроса.
    [Key] public long Id { get; set; }

    public string Ts   { get; set; } = "";   // ISO "o" UTC, как у ChangeLogEntity
    public string Kind { get; set; } = "";   // assigned | status (TaskEventKinds)

    // Получатель. Ключ разрешается по db_users в момент записи; не нашли (человек еще
    // не открывал базу, имя вписали руками) — ключ пустой, и клиент матчит по имени.
    // Имя храним всегда: оно же идет в показ и переживает переименование пользователя.
    public string RecipientKey  { get; set; } = "";
    public string RecipientName { get; set; } = "";

    // Автор правки — снапшоты, без FK (событие переживает удаление пользователя).
    public string ActorKey  { get; set; } = "";
    public string ActorName { get; set; } = "";

    // Куда вести по клику. Задача = запись × ЕЕ user-поле с IsTaskExec, поэтому одного
    // RecordId мало: нужен ExecFieldId. FieldId — поле, которое правили (исполнитель
    // или стадия): по нему же идет дедуп повторных правок одной задачи.
    public int RecordId     { get; set; }
    public int CollectionId { get; set; }
    public int FieldId      { get; set; }
    public int ExecFieldId  { get; set; }

    // Снапшоты для текста баннера: запись могли переименовать или удалить, пока
    // получатель был офлайн, а уведомление обязано остаться осмысленным.
    public string TaskTitle      { get; set; } = "";
    public string CollectionName { get; set; } = "";

    public string OldValue { get; set; } = "";
    public string NewValue { get; set; } = "";
}
