using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace SmartBase.Controls.Components;

// Единая точка сборки контекстных меню календаря (Month/Week/Day/AllDay/List/Journal).
// Стиль (FontSize, фон, отступы) задается глобально в Assets/Styles/Controls.axaml
// через селекторы ContextMenu / MenuItem — здесь только состав пунктов.
//
// Каждый пункт оборачивается в Dispatcher.UIThread.Post, потому что нажатие
// «Удалить»/«Изменить» пересобирает сетку и сносит контрол, на котором висит
// закрывающееся меню — без отсрочки это вызывает мелькание.
public static class EventContextMenu
{
    /// <summary>
    /// Чьи сочетания показывать подсказками у пунктов: у календаря и задач пункты одни и те
    /// же, а клавиши свои. null в поле — подсказки у этого пункта нет.
    /// </summary>
    public sealed record Keys(string? Open, string? Duplicate, string? Delete, string? Create = null)
    {
        public static readonly Keys Calendar =
            new("calendar.openEvent", "calendar.duplicateSelection", "calendar.deleteSelection", "table.createRecord");
        public static readonly Keys Tasks =
            new("tasks.openTask", "tasks.duplicateSelection", "tasks.deleteSelection");
    }

    // Меню одиночного события: «Изменить» (+ «Дублировать») + «Удалить».
    public static ContextMenu Single(Action onEdit, Action onDelete, Action? onDuplicate = null,
                                     Keys? keys = null, Visual? owner = null)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MakeEditItem(onEdit, keys?.Open, owner));
        if (onDuplicate is not null) menu.Items.Add(MakeDuplicateItem(onDuplicate, keys?.Duplicate, owner));
        menu.Items.Add(MakeDeleteItem(onDelete, keys?.Delete, owner));
        return menu;
    }

    // Меню множественного выделения: («Дублировать») + «Удалить».
    public static ContextMenu Multi(Action onDeleteAll, Action? onDuplicateAll = null,
                                    Keys? keys = null, Visual? owner = null)
    {
        var menu = new ContextMenu();
        if (onDuplicateAll is not null) menu.Items.Add(MakeDuplicateItem(onDuplicateAll, keys?.Duplicate, owner));
        menu.Items.Add(MakeDeleteItem(onDeleteAll, keys?.Delete, owner));
        return menu;
    }

    // Меню пустой области (день/сетка): «Создать запись».
    public static ContextMenu Background(Action onCreate, Keys? keys = null, Visual? owner = null)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MakeCreateItem(onCreate, keys?.Create, owner));
        return menu;
    }

    public static MenuItem MakeItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => Dispatcher.UIThread.Post(onClick);
        return item;
    }

    // Пункт «Создать запись» с иконкой plus.
    public static MenuItem MakeCreateItem(Action onClick, string? actionId = null, Visual? owner = null)
    {
        var item = new MenuItem { Header = MenuIcons.CreateRecordHeader() };
        if (actionId is not null && owner is not null) item.WithGesture(actionId, owner);
        item.Click += (_, _) => Dispatcher.UIThread.Post(onClick);
        return item;
    }

    // Пункт «Дублировать» с иконкой копирования.
    public static MenuItem MakeDuplicateItem(Action onClick, string? actionId = null, Visual? owner = null)
    {
        var item = new MenuItem { Header = MenuIcons.DuplicateHeader() };
        if (actionId is not null && owner is not null) item.WithGesture(actionId, owner);
        item.Click += (_, _) => Dispatcher.UIThread.Post(onClick);
        return item;
    }

    // Пункт «Изменить» с иконкой карандаша.
    public static MenuItem MakeEditItem(Action onClick, string? actionId = null, Visual? owner = null)
    {
        var item = new MenuItem { Header = MenuIcons.EditHeader() };
        if (actionId is not null && owner is not null) item.WithGesture(actionId, owner);
        item.Click += (_, _) => Dispatcher.UIThread.Post(onClick);
        return item;
    }

    // Красный пункт «Удалить» с иконкой корзины.
    public static MenuItem MakeDeleteItem(Action onClick, string? actionId = null, Visual? owner = null)
    {
        var item = new MenuItem { Header = MenuIcons.DeleteHeader() };
        if (actionId is not null && owner is not null) item.WithGesture(actionId, owner);
        item.Click += (_, _) => Dispatcher.UIThread.Post(onClick);
        return item;
    }
}
