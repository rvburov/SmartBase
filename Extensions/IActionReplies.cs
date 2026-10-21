namespace SmartBase.Extensions;

/// <summary>
/// Сообщение без события: отправить его тем же путем, каким сценарии отвечают на события,
/// которые обрабатывает действие расширения.
///
/// Нужно расширению, которое ведет переписку и пишет человеку первым — например, присылает
/// инструкцию тому, кого добавили в список, — когда входящего сообщения, на которое можно было
/// бы ответить, еще нет. Расширение по-прежнему не знает ни сценариев, ни запросов, ни сторонней
/// программы: оно называет свое действие и поле, в котором действие получает отправителя, а путь
/// находит оболочка — сценарий, который запускает это действие по событию и отвечает запросом.
/// Получатель встает в ту колонку события, из которой шаг берет отправителя, — так его находит и
/// обычный ответ.
/// </summary>
public interface IActionReplies
{
    /// <summary>
    /// Через что пойдет сообщение — имя сценария; <c>null</c> — пути нет, и
    /// <paramref name="problem"/> говорит почему словами для человека.
    /// </summary>
    string? Route(string extensionId, IExtensionAction action, string senderField, out string problem);

    /// <summary>Отправить одному получателю.</summary>
    Task<ReplyResult> SendAsync(string extensionId, IExtensionAction action, string senderField,
                                string recipient, string text, CancellationToken ct = default);
}

/// <summary>Итог отправки: дошло ли до той стороны и, если нет, почему.</summary>
public sealed record ReplyResult(bool Ok, string Explanation);
