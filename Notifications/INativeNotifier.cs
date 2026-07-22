namespace SmartBase.Notifications;

/// <summary>
/// Показ нативного баннера-уведомления средствами ОС (как у других программ) с иконкой
/// приложения и обработкой клика. Уведомление посылает САМ процесс приложения (иначе ОС
/// рисует чужую иконку и по клику открывает чужую программу): macOS — свой код через
/// UserNotifications, Windows/Linux — библиотека DesktopNotifications. Реализация выбирается
/// фабрикой <see cref="PlatformNotifications"/>.
///
/// Контракт: <see cref="Show"/> вызывается из UI-потока и НИКОГДА не бросает (сбои ОС
/// проглатываются внутри). Клик по баннеру поднимает <see cref="Activated"/> с тем же
/// <c>tag</c>, что был передан в <see cref="Show"/> (может прийти из фонового потока).
/// </summary>
public interface INativeNotifier
{
    /// <param name="title">Заголовок (наименование события).</param>
    /// <param name="body">Детали (время · коллекция).</param>
    /// <param name="tag">Метка для маршрутизации клика (id записи «db:id:field»); пусто — сводка.</param>
    void Show(string title, string body, string tag);

    /// <summary>Пользователь кликнул по баннеру — аргумент равен <c>tag</c> из <see cref="Show"/>.</summary>
    event Action<string>? Activated;
}
