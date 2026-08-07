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
| [DateEditor](../../../Controls/Widgets/DateEditor.axaml) | ввод даты; событие `Changed` |
| [TimeField](../../../Controls/Widgets/TimeField.axaml) | ввод времени; событие `TimeChanged` |
| [ErrorBanner](../../../Controls/Widgets/ErrorBanner.axaml) | красный баннер ошибки валидации; методы `Show(msg)` / `Hide()` |

`DateEditor` и `TimeField` используются в диалоге события ([RecordDialog](dialogs.md))
для выбора начала/конца.

Виджета-обёртки для прокрутки НЕТ и не нужно: прокручиваемые области — обычный
`ScrollViewer`, оформление полосы даёт глобальный шаблон `ScrollBar`
(см. [theming.md](theming.md#скроллбар)). Бывшая обёртка `ScrollArea` (наследие
Python-версии) удалена — её не использовал ни один экран.

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

## Поведения ([Controls/Behaviors/](../../../Controls/Behaviors/))

Не контролы: ничего не рисуют и не имеют шаблона. Это статические классы с
attached-свойствами, которые навешивают поведение на ЧУЖИЕ контролы — обычно глобальным
стилем в `Controls.axaml` или одним атрибутом в разметке вида.

| Поведение | Навешивается на | Назначение |
|---|---|---|
| [LineMetrics](../../../Controls/Behaviors/LineMetrics.cs) | `Window` (`IsManaged`, `FollowsAppZoom`), `Visual` (`Aliased`) | единая толщина линий ~1px при любом зуме и DPI — токены в `Resources` окна |
| [WindowZoom](../../../Controls/Behaviors/WindowZoom.cs) | `Window` (`Enabled`) | зум приложения для отдельных окон: диалоги, окна расширений, сплэш |
| [ScrollBarLink](../../../Controls/Behaviors/ScrollBarLink.cs) | `ScrollBar` (`Source`) | связывает вынесенную наружу полосу прокрутки со `ScrollViewer` — полоса начинается на уровне закреплённой шапки |

Подробности по каждому — в [theming.md](theming.md) (линии, зум, скроллбар).

## Связанные документы

[[dialogs]], [[theming]], [[../features/calendar/interactions]]
