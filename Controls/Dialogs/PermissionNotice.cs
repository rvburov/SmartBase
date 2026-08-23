using Avalonia;
using Avalonia.Controls;
using SmartBase.Core.Models;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Единое окно «действие запрещено правами». Запрещенные пункты меню и кнопки остаются
/// доступными и объясняют причину по клику — иначе интерфейс молчит, и человек думает,
/// что программа сломалась (так же ведет себя замок у коллекции без права просмотра).
///
/// Текст — тот же, что у <see cref="PermissionDeniedException"/> (единый источник
/// формулировок), поэтому сообщение одинаково и при клике по пункту меню, и когда
/// операцию отбил репозиторий (право отозвали, пока меню было открыто).
/// </summary>
public static class PermissionNotice
{
    /// <summary>Показать причину отказа для действия (fire-and-forget: окно модальное,
    /// вызывающему ждать нечего).</summary>
    public static void Show(Visual? anchor, PermAction action) =>
        Show(anchor, new PermissionDeniedException(action).Message);

    /// <summary>Показать готовый текст отказа (например, из перехваченного исключения).</summary>
    public static void Show(Visual? anchor, string message)
    {
        if (TopLevel.GetTopLevel(anchor) is Window owner)
            _ = ConfirmDialog.Info(message).ShowDialog(owner);
    }

    /// <summary>
    /// Выполнить действие, если право есть; иначе — окно с причиной. Возвращает,
    /// выполнилось ли действие.
    /// </summary>
    public static bool Run(Visual? anchor, bool allowed, PermAction action, Action run)
    {
        if (!allowed)
        {
            Show(anchor, action);
            return false;
        }
        run();
        return true;
    }
}
