using Avalonia.Controls;
using Avalonia.Threading;

namespace MyPlanner.Controls.Components;

// Единая точка сборки контекстных меню календаря (Month/Week/Day/AllDay/List/Journal).
// Стиль (FontSize, фон, отступы) задаётся глобально в Assets/Styles/Controls.axaml
// через селекторы ContextMenu / MenuItem — здесь только состав пунктов.
//
// Каждый пункт оборачивается в Dispatcher.UIThread.Post, потому что нажатие
// «Удалить»/«Изменить» пересобирает сетку и сносит контрол, на котором висит
// закрывающееся меню — без отсрочки это вызывает мелькание.
public static class EventContextMenu
{
    // Меню одиночного события: «Изменить» + «Удалить».
    public static ContextMenu Single(Action onEdit, Action onDelete)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MakeItem("Изменить", onEdit));
        menu.Items.Add(MakeItem("Удалить",       onDelete));
        return menu;
    }

    // Меню множественного выделения: одна команда «Удалить».
    public static ContextMenu Multi(Action onDeleteAll)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MakeItem("Удалить", onDeleteAll));
        return menu;
    }

    // Меню пустой области (день/сетка): «Создать запись».
    public static ContextMenu Background(Action onCreate)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MakeItem("Создать запись", onCreate));
        return menu;
    }

    public static MenuItem MakeItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => Dispatcher.UIThread.Post(onClick);
        return item;
    }
}
