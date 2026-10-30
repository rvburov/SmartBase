using SmartBase.Settings;
using SmartBase.Utils;

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
