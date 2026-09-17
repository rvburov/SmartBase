# Виджеты и компоненты

## Назначение

Переиспользуемые UI-элементы в [Controls/](../../../Controls/). Делятся на **виджеты**
(мелкие контролы-обертки с единым стилем) и **компоненты** (крупные части экрана:
навбары, сайдбары, полосы).

## Виджеты ([Controls/Widgets/](../../../Controls/Widgets/))

| Виджет | Назначение |
|---|---|
| [AppButton](../../../Controls/Widgets/AppButton.axaml) | кнопка в стиле приложения |
| [AppLabel](../../../Controls/Widgets/AppLabel.axaml) | подпись с типографикой проекта |
| [AppCheckBox](../../../Controls/Widgets/AppCheckBox.axaml) | чекбокс (стиль подключен глобально в [App.axaml](../../../App.axaml)) |
| [SearchInput](../../../Controls/Widgets/SearchInput.axaml) | поле поиска (используется в навбаре календаря) |
| [Selector](../../../Controls/Widgets/Selector.axaml) | выпадающий/сегментный выбор |
| [DateEditor](../../../Controls/Widgets/DateEditor.axaml) | ввод даты; событие `Changed` |
| [TimeField](../../../Controls/Widgets/TimeField.axaml) | ввод времени; событие `TimeChanged` |
| [NoticeBanner](../../../Controls/Widgets/NoticeBanner.axaml) | ШАБЛОН красного уведомления (ошибка, пояснение, тост); методы `Show(msg)` / `Hide()` |
| [CheckListDropdown](../../../Controls/Widgets/CheckListDropdown.axaml) | выпадающий список с галочками (что показывать, а что скрыть); событие `HiddenChanged` |
| [AppMarkdown](../../../Controls/Widgets/AppMarkdown.cs) | показ markdown-текста: разбор Markdig плюс свой рендер (см. [markdown.md](markdown.md)); показывать разметку «руками» нельзя |

`DateEditor` и `TimeField` используются в диалоге события ([RecordDialog](dialogs.md))
для выбора начала/конца.

Виджета-обертки для прокрутки НЕТ и не нужно: прокручиваемые области — обычный
`ScrollViewer`, оформление полосы дает глобальный шаблон `ScrollBar`
(см. [theming.md](theming.md#скроллбар)), а раскладку — стандарт
[scrolling.md](scrolling.md) с готовыми кусками разметки и сторожем-тестом.

Обертка тут проиграла бы: почти каждый `ScrollViewer` в приложении несет что-то свое —
события (`DoubleTapped`/`ContextRequested` в списках календаря), фон (блок свойств, деталь
профиля), доступ к `Offset` снаружи (инструкция, календарь), требование «`ItemsControl` —
прямой ребенок» ради виртуализации (задачи). Все это пришлось бы пробрасывать сквозь
обертку, и ее начали бы обходить.

### NoticeBanner — шаблон красных уведомлений

`NoticeBanner` — ЕДИНЫЙ вид «красной плашки» на все приложение: иконка-восклицание в
круге, красная рамка (`BrushRed` + токен `BorderLine`), светло-красная подложка
(`BrushRedLight`), красный текст. Своей верстки для таких уведомлений не заводим — любая
новая плашка делается этим виджетом, иначе вид расходится от окна к окну (так и было:
разметка дублировалась в каждом диалоге, а пояснения в диалоге свойства шли отдельным
`Border` без иконки и рамки).

Где применяется:

- ошибки валидации в диалогах (`RecordDialog`, `CreateItemDialog`, `GroupDialog`,
  `CollectionDialog`, `PropertyEditorDialog`, `ImportExportDialog`, `PgConnectDialog`,
  `PasswordDialog`, `CreateDatabaseDialog`, `TaskDialog`);
- постоянные пояснения в диалоге свойства: «базовое свойство записи» и «привязано к
  разделу» (см. [dialogs.md](dialogs.md));
- всплывашки [`Toast.Error`](../../../Controls/Widgets/Toast.cs) — тем же баннером внутри
  флайаута;
- внутри виджетов `DateEditor` и `FileEditor`.

Использование:

```xml
<w:NoticeBanner x:Name="Note" Margin="0,0,0,12" />
```

```csharp
Note.Show("Текст уведомления");   // показывает и подставляет текст
Note.Hide();                       // прячет
```

- скрыт, пока нет текста (`Show`/`Hide` = запись в `Message`);
- иконка — своя колонка слева, по центру всей плашки; текст переносится в своей колонке,
  под иконку не заходит;
- длинный текст переносится по словам, высота идет по содержимому;
- внешний отступ (`Margin`) и `DockPanel.Dock` задаются на месте использования;
- размер текста стандартный (`FontSizeBase`) и одинаковый во всех уведомлениях — вариантов
  размера у баннера нет.

Цвета берем только из палитры ([Colors.axaml](../../../Assets/Styles/Colors.axaml)),
размер текста — только токенами ([Typography.axaml](../../../Assets/Styles/Typography.axaml)),
сырых значений в баннере нет.

### EmptyListHint — пустое состояние своих списков

[EmptyListHint](../../../Controls/Components/EmptyListHint.cs) кладет список и надпись-заглушку
в общий `Panel`: заглушка видна, пока в списке нет строк — «Список пуст», а при непустом
поиске «Ничего не найдено». Применяется в чеклистах «Мультисписка» и «Связи» и в панелях
фильтра (таблица конструктора и общее окно служебных таблиц `ColumnFilterPanel`). У нативных `ComboBox` то же делает копия темы
(см. [theming.md](theming.md#выпадающие-списки-стандарт)).

## Компоненты ([Controls/Components/](../../../Controls/Components/))

| Компонент | Назначение |
|---|---|
| [ActivityBar](../../../Controls/Components/ActivityBar.axaml) | левая узкая панель режимов приложения |
| [Sidebar](../../../Controls/Components/Sidebar/SidebarTree.axaml) | боковая панель: дерево групп/коллекций, чекбоксы активных коллекций |
| [SettingsSidebar](../../../Controls/Components/Sidebar/SidebarList.axaml) | боковая навигация в настройках |
| [Navbar](../../../Controls/Components/Navbar/NavbarCalendar.axaml) | верхний навбар календаря: ← Сегодня →, переключатель режимов, поиск, «+» |
| [ConstructorNavbar](../../../Controls/Components/Navbar/NavbarConstructor.axaml) | навбар конструктора: заголовок-коллекция (редактируемый), переключатель вида, toggle панели записи |
| [ConstructorFooter](../../../Controls/Components/Footer/FooterConstructor.axaml) | нижняя панель: навигация по строкам таблицы («‹‹ ‹ 10 – 50 из 125 437 › ››», см. [../features/constructor/table.md](../features/constructor/table.md#навигация-нижней-панели)) и размер выделения ячеек «Выделено: 3 × 2» (`SetSelection`; при одной выделенной ячейке и в виде «Список» надписи нет). Больше в панели ничего нет: кнопки «Фильтр»/«Сортировка» удалены (их события никто не слушал, живые вызовы — из меню заголовка колонки), счетчик «Всего: N» — как дубль числа после «из» |
| [AllDayBar](../../../Controls/Components/AllDayBar.axaml) | полоса событий «весь день» в режимах «День»/«Неделя» |
| [DateFilterTreeBox](../../../Controls/Components/DateFilterTreeBox.cs) | чеклист дат в окне фильтра колонки: дерево «год → месяц → день» с каскадом галочек; общий для таблицы конструктора, «Диагностики», «Истории изменений» и таблицы пользователей (логика — [DateFilterTree](../../../Core/Models/DateFilterTree.cs)) |
| [ColumnFilterPanel](../../../Controls/Components/ColumnFilterPanel.cs) | окно фильтра и сортировки колонки служебных таблиц — «Диагностика», «История изменений», «Пользователи»: поведение таблицы конструктора (каскад фильтров, автоотметка найденного, Enter, отобранные первыми) на общей модели [FilterSelection](../../../Core/Models/FilterSelection.cs). Таблица отдает ему `ColumnFilterSpec` (подписи, загрузку значений с фильтрами остальных колонок) и получает `ColumnFilterResult`; каскад в памяти — [ColumnCascade](../../../Core/Models/ColumnCascade.cs) |
| [EventContextMenu](../../../Controls/Components/EventContextMenu.cs) | фабрика контекстных меню событий: `Single` / `Multi` / `Background` |

`Navbar` шлет наверх `TodayClicked/PrevClicked/NextClicked/AddClicked/ViewChanged/
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
| [ScrollBarLink](../../../Controls/Behaviors/ScrollBarLink.cs) | `ScrollBar` (`Source`) | связывает вынесенную наружу полосу прокрутки со `ScrollViewer` — полоса начинается на уровне закрепленной шапки |
| [TitleEditing](../../../Controls/Behaviors/TitleEditing.cs) | пара `TextBlock` показа + `TextBox` правки | вход в правку названия: каретка встает в точку клика, а не `SelectAll` |

Подробности по каждому — в [theming.md](theming.md) (линии, зум, скроллбар).

## Пара «показ + правка» — по стандарту

Виджет, где одно значение ПОКАЗЫВАЕТ `TextBlock`, а ПРАВИТ `TextBox` (название в навбаре,
ячейка таблицы, значение карточки), собирается строго по чек-листу из
[theming.md → «Текст не двигается при смене состояния»](theming.md#текст-не-двигается-при-смене-состояния-стандарт):
общий целый `LineHeight` из токена, один боковой отступ на оба состояния, `MinHeight=0`,
каретка в точку клика, одна рамка на оба состояния, прозрачный фон редактора. Иначе текст
дергается при клике — на Retina это заметно, на обычном экране нет. Проверяет стенд
[Tests/EditGeometry](../../../Tests/EditGeometry/Program.cs).

## Ловушка: `TextBox.TextChanged` отложенный

`TextBox` в Avalonia 11.2 поднимает `TextChanged` не в момент присвоения `Text`, а отложенно,
через диспетчер (`RaiseTextChangeEvents`). Отсюда два следствия, на которых уже ловились:

- **флаг подавления не работает.** `_suppress = true; box.Text = x; _suppress = false;` — событие
  придет после снятия флага и будет принято за правку пользователя;
- **самое первое присвоение тоже дает событие.** `new TextBox { Text = value }`, затем
  `box.TextChanged += …` — обработчик получит событие с этим самым `value`, хотя подписался позже.

Правило для обработчиков `TextChanged`, которые что-то пишут (в базу, в настройки, наверх
событием): **сверять текст со значением и молчать, если оно не изменилось** — так сделано в
инлайн-полях панели записи, ее нижнем редакторе и в имени записи (`NavbarPage._knownTitle`).
Поиск и фильтры сверки не требуют: лишний перерасчет по тому же тексту безвреден. Цена ошибки:
панель записи писала значения обратно в базу и дергала таблицу на каждое построение карточек, а
число с форматом показа уходило в базу отформатированной строкой — см.
[features/constructor/record-page.md](../features/constructor/record-page.md), «Отложенный
TextChanged».

## Связанные документы

[[dialogs]], [[theming]], [[scrolling]], [[markdown]], [[../features/calendar/interactions]]
