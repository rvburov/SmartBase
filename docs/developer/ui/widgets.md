# Виджеты и компоненты

## Назначение

Переиспользуемые UI-элементы в [Controls/](../../../Controls/). Делятся на **виджеты**
(мелкие контролы-обёртки с единым стилем) и **компоненты** (крупные части экрана:
навбары, сайдбары, полосы).

## Виджеты ([Controls/Widgets/](../../../Controls/Widgets/))

| Виджет | Назначение |
|---|---|
| [AppButton](../../../Controls/Widgets/AppButton.axaml) | кнопка в стиле приложения |
| [AppLabel](../../../Controls/Widgets/AppLabel.axaml) | подпись с типографикой проекта |
| [AppCheckBox](../../../Controls/Widgets/AppCheckBox.axaml) | чекбокс (стиль подключён глобально в [App.axaml](../../../App.axaml)) |
| [SearchInput](../../../Controls/Widgets/SearchInput.axaml) | поле поиска (используется в навбаре календаря) |
| [Selector](../../../Controls/Widgets/Selector.axaml) | выпадающий/сегментный выбор |
| [ScrollArea](../../../Controls/Widgets/ScrollArea.axaml) | прокручиваемая область с оформлением проекта |
| [DateEditor](../../../Controls/Widgets/DateEditor.axaml) | ввод даты; событие `Changed` |
| [TimeField](../../../Controls/Widgets/TimeField.axaml) | ввод времени; событие `TimeChanged` |
| [ErrorBanner](../../../Controls/Widgets/ErrorBanner.axaml) | красный баннер ошибки валидации; методы `Show(msg)` / `Hide()` |

`DateEditor` и `TimeField` используются в диалоге события ([RecordDialog](dialogs.md))
для выбора начала/конца.

`ErrorBanner` — единый контрол для уведомлений об ошибках валидации (раньше вёрстка
дублировалась в каждом диалоге). Скрыт, пока нет текста; `Show(msg)` показывает и
подставляет текст, `Hide()` прячет. Длинный текст переносится по словам, высота
подстраивается под содержимое. Внешний отступ (`Margin`) и `DockPanel.Dock` задаются
на месте использования. Применяется в диалогах с валидацией и внутри `DateEditor`.

## Компоненты ([Controls/Components/](../../../Controls/Components/))

| Компонент | Назначение |
|---|---|
| [ActivityBar](../../../Controls/Components/ActivityBar.axaml) | левая узкая панель режимов приложения |
| [Sidebar](../../../Controls/Components/Sidebar/SidebarTree.axaml) | боковая панель: дерево групп/коллекций, чекбоксы активных коллекций |
| [SettingsSidebar](../../../Controls/Components/Sidebar/SidebarList.axaml) | боковая навигация в настройках |
| [Navbar](../../../Controls/Components/Navbar/NavbarCalendar.axaml) | верхний навбар календаря: ← Сегодня →, переключатель режимов, поиск, «+» |
| [ConstructorNavbar](../../../Controls/Components/Navbar/NavbarConstructor.axaml) | навбар конструктора: заголовок-коллекция (редактируемый), переключатель вида, toggle панели записи |
| [ConstructorFooter](../../../Controls/Components/Footer/FooterConstructor.axaml) | нижняя панель: счётчик записей, страницы |
| [AllDayBar](../../../Controls/Components/AllDayBar.axaml) | полоса событий «весь день» в режимах «День»/«Неделя» |
| [EventContextMenu](../../../Controls/Components/EventContextMenu.cs) | фабрика контекстных меню событий: `Single` / `Multi` / `Background` |

`Navbar` шлёт наверх `TodayClicked/PrevClicked/NextClicked/AddClicked/ViewChanged/
SearchChanged`, которые ловит [CalendarControllerView](../../../Views/ConstructorDb/Calendar/CalendarControllerView.axaml.cs).
`EventContextMenu` собирает пункты «Создать/Редактировать/Дублировать/Удалить» —
им пользуются все режимы календаря (см. [../features/calendar/interactions.md](../features/calendar/interactions.md)).

## Связанные документы

[[dialogs]], [[theming]], [[../features/calendar/interactions]]
