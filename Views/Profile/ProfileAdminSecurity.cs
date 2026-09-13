using Avalonia.Interactivity;
using SmartBase.Controls.Components.Security;

namespace SmartBase.Views.Profile;

/// <summary>
/// Контур безопасности и его журнал в панели администратора (docs/ROADMAP-API.md, шаг 15).
///
/// <b>Почему они здесь.</b> Оба экрана жили вкладками раздела «Настройки → Интеграции» — там,
/// где обмен настраивают. Читают их другие люди: «что программе разрешено» и «что уходило
/// наружу» спрашивает служба безопасности и администратор, а не тот, кто заводит запрос. Все,
/// чем управляет администратор, собирается в одном месте, и показ контура — часть этого «всего».
///
/// <b>Чего переезд НЕ принес.</b> Правки политики из программы не появилось и не появится: она
/// остается подписанным файлом, иначе контур перестает быть контуром (раздел 10). Экран
/// администратора отличается от прежней вкладки только тем, где он лежит и кто его видит.
///
/// <b>Что осталось в «Интеграциях».</b> Живые вердикты «Политика: …» на карточках соединений:
/// их читают в момент настройки — «почему этот адрес не подходит», — и гонять человека за
/// ответом в другой раздел значило бы прервать работу ради справки.
///
/// Обе панели открываются ВМЕСТО панели администратора и занимают ее место целиком — тем же
/// приемом, что список пользователей (<see cref="ProfileView.OnOpenUsersPanel"/>): своя шапка
/// встает на место навбара модуля. Сами экраны — контролы из
/// <c>Controls/Components/Security</c>: панель администратора им зритель, а не хозяин, и
/// показать их может кто угодно еще.
/// </summary>
public partial class ProfileView
{
    // Контролы создаются при первом открытии и живут дальше: у каждого свое состояние
    // (развернутый список меток, страницы журнала, итог проверки цепочки), и пересоздание
    // молча сбрасывало бы его на каждое закрытие панели.
    private SecurityContourView? _contour;
    private SecurityJournalView? _securityJournal;

    // ── Контур ────────────────────────────────────────────────────────────────

    private void OnOpenSecurityPanel(object? sender, RoutedEventArgs e) => OpenSecurity();

    private void OpenSecurity()
    {
        if (_contour is null)
        {
            _contour = new SecurityContourView();
            // «В журнал» у счетчика блокировок: контур сосчитал их, а показывает журнал —
            // переход делает тот, кто показывает оба экрана.
            _contour.BlockedRequested += (_, _) => OpenSecurityLog(blocked: true);
            SecurityHost.Children.Add(_contour);
        }

        _contour.Show(_workspace);

        AdminPanel.IsVisible       = false;
        HeaderBar.IsVisible        = false;
        SecurityLogPanel.IsVisible = false;
        SecurityPanel.IsVisible    = true;
    }

    private void OnCloseSecurityPanel(object? sender, RoutedEventArgs e) => CloseSecurityPanels();

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
        SecurityPanel.IsVisible    = false;
        SecurityLogPanel.IsVisible = true;
    }

    private void OnCloseSecurityLogPanel(object? sender, RoutedEventArgs e) => CloseSecurityPanels();

    // ── Закрытие ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Закрыть обе панели и вернуть панель администратора. Вызывается крестиком и сменой
    /// раздела в сайдбаре — так же, как закрывается список пользователей.
    /// </summary>
    private void CloseSecurityPanels()
    {
        if (!SecurityPanel.IsVisible && !SecurityLogPanel.IsVisible) return;

        SecurityPanel.IsVisible    = false;
        SecurityLogPanel.IsVisible = false;
        HeaderBar.IsVisible        = true;

        // Возвращаемся в панель администратора, только если раздел еще открыт: при уходе
        // в «Общие данные» или «Историю» видимость панелей уже расставил ShowCategory.
        AdminPanel.IsVisible = _categoryKey == "admin";
    }
}
