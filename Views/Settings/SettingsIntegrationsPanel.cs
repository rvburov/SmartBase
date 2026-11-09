using SmartBase.Settings;
using SmartBase.Utils;
using SmartBase.Controls.Dialogs;

namespace SmartBase.Views.Settings;

/// <summary>
/// Раздел «Настройки → Интеграции» (partial SettingsView, без ViewModel — как «Календарь» и
/// «Задачи»). Раздел один, а внутри вкладки; переключатель между ними — выпадающий список в
/// навбаре, как виды календаря.
///
/// Переключение вкладок живет здесь; списки соединений и запросов — в
/// <c>SettingsIntegrationsLists</c>, потоки событий — в <c>SettingsIntegrationsStreams</c>,
/// обмен по кнопке — в <c>SettingsIntegrationsRun</c>,
/// папка-приемник — в <c>SettingsIntegrationsInbox</c>, сценарии — в
/// <c>SettingsIntegrationsScenarios</c>, журнал запусков — в
/// <c>SettingsIntegrationsJournal</c>.
/// </summary>
public partial class SettingsView
{
    // Какая вкладка открыта. Пустая строка — раздел еще не открывали в этот заход.
    private string _integrationsTab = "";

    // ── Права на обмен (docs/ROADMAP-API.md, 10.8) ────────────────────────────

    /// <summary>
    /// Разрешено ли действие обмена текущему пользователю.
    ///
    /// Спрашиваются ВСЕ открытые базы разом: настройки обмена общие на машину, а ответ ложится в
    /// ту базу, которую выберут при запуске; спроси одну — и человек, которому соседняя база
    /// настройку запретила, обошел бы запрет, открыв ту, где она разрешена.
    ///
    /// Ни одной базы не открыто — ответ «нельзя»: спрашивать некого, а обмен, которому некуда
    /// класть ответ, настраивать незачем (то же говорит и сам запуск).
    ///
    /// Права уровня базы, поэтому коллекция не передается: у настройки обмена ее и нет.
    /// </summary>
    private bool CanIntegrations(Core.Models.PermAction action)
    {
        var bases = _databases?.Invoke() ?? [];
        return bases.Count > 0 && bases.All(d => d.Permissions.Can(action));
    }

    /// <summary>
    /// Пустить действие или объяснить отказ окном — как и везде в программе: запрещенная кнопка
    /// остается нажимаемой и называет причину, иначе интерфейс молчит и выглядит сломанным
    /// (docs/developer/architecture/users-permissions.md).
    /// </summary>
    private bool AllowIntegrations(Core.Models.PermAction action)
    {
        if (CanIntegrations(action)) return true;

        PermissionNotice.Show(this, action);
        return false;
    }

    // ── Вход в раздел и переключение вкладок ──────────────────────────────────

    private void BuildIntegrationsPanel()
    {
        var tab = IntegrationTabs.Resolve(AppSettings.LoadIntegrationsTab()).Key;

        HeaderBar.SetIntegrationTabs(tab);
        ShowIntegrationTab(tab);
    }

    /// <summary>
    /// Показать вкладку. Выбор запоминается: обмен настраивают в несколько заходов, и
    /// возвращаться каждый раз к первой вкладке утомительно.
    /// </summary>
    private void ShowIntegrationTab(string key)
    {
        _integrationsTab = IntegrationTabs.Resolve(key).Key;
        AppSettings.SaveIntegrationsTab(_integrationsTab);

        IntegrationsConnectionsTab.IsVisible = _integrationsTab == "connections";
        IntegrationsRequestsTab.IsVisible    = _integrationsTab == "requests";
        IntegrationsStreamsTab.IsVisible     = _integrationsTab == "streams";
        IntegrationsScenariosTab.IsVisible   = _integrationsTab == "scenarios";
        IntegrationsInboxTab.IsVisible       = _integrationsTab == "inbox";
        IntegrationsJournalTab.IsVisible     = _integrationsTab == "journal";

        // Список строится при каждом показе: карточки правятся в окнах, и вернуться на вкладку
        // можно уже с другим их составом.
        if (_integrationsTab == "connections") BuildIntegrationsConnections();
        if (_integrationsTab == "requests")    BuildIntegrationsRequests();
        if (_integrationsTab == "streams")     BuildIntegrationsStreams();
        if (_integrationsTab == "scenarios")   BuildIntegrationsScenarios();
        if (_integrationsTab == "inbox")       BuildIntegrationsInbox();
        if (_integrationsTab == "journal")     BuildIntegrationsJournal();
    }
}
