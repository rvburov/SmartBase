using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace SmartBase.Controls.Behaviors;

/// <summary>
/// Учет открытых всплывающих панелей, которые обязаны исчезнуть, когда окно теряет фокус.
///
/// <b>Зачем.</b> Флайаут рендерится отдельным окном поверх всех остальных. На macOS оно
/// остается висеть над чужой программой, если его не закрыть (Avalonia issue) — человек
/// переключился в браузер, а поверх него стоит наш фильтр колонки.
///
/// <b>Почему реестр, а не обход дерева.</b> Раньше окно знало ровно про один вид панелей:
/// <c>MainWindow.CloseOpenPopups</c> искал в дереве <c>TableView</c> и звал его собственный
/// метод закрытия. Фильтр журнала, пикер коллекций и «Тип документа» библиотеки про это правило
/// не знали и висели дальше — каждая новая панель молча заводила ту же дыру. Здесь панель
/// отмечается ОДНОЙ строкой в месте показа, а окно закрывает все разом и ничего не ищет.
///
/// Всплывашки-уведомления (<c>Toast</c>) сюда не попадают намеренно: они не забирают фокус,
/// живут секунды и гаснут по своему таймеру, а закрытие «на деактивации» съедало бы сообщение
/// при открытии собственного модального окна — на macOS оно тоже приходит как потеря фокуса.
///
/// Все вызовы — с UI-потока, синхронизация не нужна.
/// </summary>
public static class TransientPopups
{
    private static readonly List<FlyoutBase> Open = [];

    /// <summary>
    /// Отметить показанную панель. Снимать с учета не нужно — реестр сам забывает ее по
    /// закрытию, чьим бы оно ни было (клик мимо, «Готово», Esc, деактивация окна).
    /// </summary>
    public static void Track(FlyoutBase flyout)
    {
        if (Open.Contains(flyout)) return;
        Open.Add(flyout);
        flyout.Closed += Forget;
    }

    private static void Forget(object? sender, EventArgs e)
    {
        if (sender is not FlyoutBase flyout) return;
        flyout.Closed -= Forget;
        Open.Remove(flyout);
    }

    /// <summary>Закрыть все открытые панели. Зовет окно при потере фокуса.</summary>
    public static void CloseAll()
    {
        // Идем по копии: Hide() поднимает Closed, а тот правит сам список.
        foreach (var flyout in Open.ToList()) flyout.Hide();
        // Подчищаем на случай, если чей-то Closed не пришел: реестр не должен копить
        // ссылки на панели, которых уже нет.
        Open.Clear();
    }

    /// <summary>Сколько панелей сейчас на учете. Для тестов.</summary>
    public static int Count => Open.Count;
}
