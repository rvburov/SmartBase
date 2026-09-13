using Avalonia.Interactivity;
using SmartBase.Controls.Components.Security;

namespace SmartBase.Views.Profile;

/// <summary>
/// Контур безопасности и журнал безопасности в модуле «Профиль» (docs/ROADMAP-API.md, шаг 15).
///
/// <b>Почему они не в «Настройках».</b> Оба экрана жили вкладками раздела «Настройки →
/// Интеграции» — там, где обмен настраивают. Читают их другие люди: «что программе разрешено» и
/// «что уходило наружу» спрашивает тот, кто проверяет, а не тот, кто заводит запрос.
///
/// <b>Почему они лежат по-разному.</b> Переезд сперва отправил оба в панель администратора — и
/// это отрезало контур от тех, кому он нужнее всего: панель неадминистратору не показывает
/// ничего, а вопрос «почему у меня не уходит отправка» задает как раз он. Поэтому
/// <b>контур — свой раздел профиля</b>, видимый всем: он только читает, менять с него нечего.
/// <b>Журнал остался в панели администратора</b>: там он и ведется, и читается — вместе с
/// правами и пользователями.
///
/// По той же границе разделены и действия контура. «Перечитать политику» доступно всем: оно
/// ничего не разрешает, а перечитывать файл приходится сразу после того, как администратор его
/// поправил. «Паспорт интеграций» и переход в журнал — только администратору: паспорт уносит из
/// программы сводку по направлениям и объемам, а кнопка, ведущая туда, куда человека не пустят,
/// хуже отсутствующей.
///
/// <b>Чего переезд НЕ принес.</b> Правки политики из программы не появилось и не появится: она
/// остается подписанным файлом, иначе контур перестает быть контуром (раздел 10).
///
/// <b>Что осталось в «Интеграциях».</b> Живые вердикты «Политика: …» на карточках соединений:
/// их читают в момент настройки — «почему этот адрес не подходит», — и гонять человека за
/// ответом в другой раздел значило бы прервать работу ради справки.
/// </summary>
public partial class ProfileView
{
    // Контролы создаются при первом показе и живут дальше: у каждого свое состояние
    // (развернутый список меток, страницы журнала, итог проверки цепочки), и пересоздание
    // молча сбрасывало бы его на каждый уход из раздела.
    private SecurityContourView? _contour;
    private SecurityJournalView? _securityJournal;
    private SecretsView?         _secrets;

    /// <summary>
    /// Модуль просит открыть другой свой раздел (переход «В журнал» из контура). Сайдбар живет
    /// в <c>MainWindow</c>, и выделение пункта — его дело: переключи раздел молча, и сайдбар
    /// показывал бы один раздел, а экран — другой.
    /// </summary>
    public event EventHandler<string>? CategoryRequested;

    // ── Контур ────────────────────────────────────────────────────────────────

    /// <summary>Показать контур: строится при каждом заходе в раздел — политику могли перечитать.</summary>
    private void ShowSecurityContour()
    {
        if (_contour is null)
        {
            _contour = new SecurityContourView();

            // «В журнал» у счетчика блокировок: контур сосчитал их, а показывает журнал —
            // переход делает модуль, у которого оба экрана под рукой.
            _contour.BlockedRequested += (_, _) =>
            {
                CategoryRequested?.Invoke(this, "admin");
                OpenSecurityLog(blocked: true);
            };

            SecurityPanel.Children.Add(_contour);
        }

        // Администраторские действия — по тому же признаку, по которому открывается панель
        // администратора: администратор хотя бы одной открытой базы.
        _contour.Show(_workspace, forAdmin: _workspace?.Mounted.Any(m => m.Permissions.IsAdmin) == true);
    }

    // ── Менеджер паролей ──────────────────────────────────────────────────────

    /// <summary>
    /// Показать опись секретов. Строится при каждом заходе: секреты заводят и отзывают на
    /// соседних экранах, и вернуться сюда можно уже с другим списком.
    /// </summary>
    private void ShowSecrets()
    {
        if (_secrets is null)
        {
            _secrets = new SecretsView();
            SecretsPanel.Children.Add(_secrets);
        }

        _secrets.Show(_workspace);
    }

    // ── Журнал ────────────────────────────────────────────────────────────────

    private void OnOpenSecurityLogPanel(object? sender, RoutedEventArgs e) => OpenSecurityLog();

    private void OpenSecurityLog(bool blocked = false)
    {
        if (_securityJournal is null)
        {
            _securityJournal = new SecurityJournalView();
            SecurityLogHost.Children.Add(_securityJournal);
        }

        if (blocked) _securityJournal.ShowBlocked(_workspace);
        else         _securityJournal.Show(_workspace);

        AdminPanel.IsVisible       = false;
        HeaderBar.IsVisible        = false;
        SecurityLogPanel.IsVisible = true;
    }

    private void OnCloseSecurityLogPanel(object? sender, RoutedEventArgs e) => CloseSecurityPanels();

    // ── Закрытие ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Закрыть журнал и вернуть панель администратора. Вызывается крестиком и сменой раздела в
    /// сайдбаре — так же, как закрывается список пользователей.
    /// </summary>
    private void CloseSecurityPanels()
    {
        if (!SecurityLogPanel.IsVisible) return;

        SecurityLogPanel.IsVisible = false;
        HeaderBar.IsVisible        = true;

        // Возвращаемся в панель администратора, только если раздел еще открыт: при уходе
        // в другой раздел видимость панелей уже расставил ShowCategory.
        AdminPanel.IsVisible = _categoryKey == "admin";
    }
}
