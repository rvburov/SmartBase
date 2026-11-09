using SmartBase.Controls.Components.Security;

namespace SmartBase.Views.Profile;

/// <summary>
/// «Менеджер паролей» в модуле «Профиль» (docs/ROADMAP-API.md, шаг 16): опись секретов машины
/// собирает <see cref="SecretsView"/>, здесь — только хозяйство раздела: создать опись, перестроить
/// ее при смене открытых баз и показать «Добавить пароль» в навбаре тому, кому есть что заводить.
/// </summary>
public partial class ProfileView
{
    private SecretsView? _secrets;

    /// <summary>
    /// База переименована из менеджера паролей: шестеренка у пароля сервера PostgreSQL открывает то
    /// же окно настроек базы, что и «Управление базой данных». Имя уже записано в реестр;
    /// переименовать смонтированную базу и перечитать сайдбары — дело <c>MainWindow</c>, и делает он
    /// это тем же обработчиком, что и для «Настроек»: вторая копия разошлась бы с первой.
    /// </summary>
    public event EventHandler<(int Id, string Name)>? DatabaseRenamed;

    /// <summary>
    /// Показать опись секретов. Строится при каждом заходе: секреты заводят и отзывают на
    /// соседних экранах, и вернуться сюда можно уже с другим списком.
    /// </summary>
    private void ShowSecrets()
    {
        if (_secrets is null)
        {
            _secrets = new SecretsView();
            _secrets.DatabaseRenamed += (_, e) => DatabaseRenamed?.Invoke(this, e);
            // Список перестраивается после каждого действия и при смене открытых баз — и каждый
            // раз заново сверяется, кому показывать «Добавить пароль».
            _secrets.Built += (_, _) => SyncSecretsActions();
            SecretsPanel.Children.Add(_secrets);
        }

        _secrets.Show(_workspace);
    }

    /// <summary>
    /// «Добавить пароль» в навбаре — только в этом разделе и только тому, кому есть что им
    /// заводить: оба пункта меню (соединение, токен) спрашивают право на настройку обмена.
    /// Кнопка, ведущая туда, куда человека не пустят, хуже отсутствующей.
    /// </summary>
    private void SyncSecretsActions() =>
        HeaderBar.ShowSecretsActions =
            _categoryKey == "secrets" && _secrets?.ForIntegrations == true;

    /// <summary>
    /// Перестроить опись, если она на экране: база открылась или закрылась, админство выдали или
    /// отозвали — строки и кнопка «Добавить пароль» обязаны следовать за этим сразу. Модуль
    /// «Профиль» скрыт — строить незачем: при возврате в него ShowCategory соберет список заново.
    /// </summary>
    private void RefreshSecrets()
    {
        if (_categoryKey == "secrets" && IsVisible) _secrets?.Show(_workspace);
    }
}
