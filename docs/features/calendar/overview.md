# Календарь — общий обзор

## Назначение

Модуль календаря показывает события (`CalendarRecord`) в шести режимах:
**Расписание**, **Журнал**, **День**, **Неделя**, **Месяц**, **Год**. Режимы
переключаются на лету, навигация (← Сегодня →) работает в контексте текущего режима.

## Где в коде

- Оркестратор (то, что реально работает в окне): [CalendarControllerView.axaml.cs](../../../Views/ConstructorDb/Calendar/CalendarControllerView.axaml.cs)
- Вью-контролы режимов: [Views/ConstructorDb/Calendar/](../../../Views/ConstructorDb/Calendar/) — `ListView`, `JournalView`, `DayView`, `WeekView`, `MonthView`, `YearView`
- Параллельный MVVM-слой: [ViewModels/Calendar/](../../../ViewModels/Calendar/) — `CalendarControllerViewModel` + по одному `*ViewModel` на режим, общая база `CalendarViewModelBase`
- Модель события: [CalendarRecord.cs](../../../Core/Models/CalendarRecord.cs)

## Как работает

В главном окне ([MainWindow.axaml](../../../Views/MainWindow.axaml)) стоит
`CalendarControllerView`. Это **code-behind-оркестратор**, а не MVVM: он сам
держит состояние и кормит вью-контролы готовыми данными. Параллельно существует
MVVM-слой (`CalendarControllerViewModel` и `*ViewModel`) с такой же логикой
дат/навигации — он содержит ту же бизнес-логику диапазонов, но рендеринг идёт
через вью-контролы.

### Состояние контроллера

- `_currentViewIndex` — текущий режим (0-Расписание, 1-Журнал, 2-День, 3-Неделя, 4-Месяц, 5-Год; по умолчанию **4 — Месяц**).
- `_currentDate` — опорная дата.
- `_dbs` — список **всех смонтированных БД** (мультибаза): события агрегируются из всех, CRUD маршрутизируется по `DbId`.
- `_activeByDb` — активные коллекции по каждой БД (фильтр сайдбара).
- `_searchQuery` — строка поиска.

### Цикл обновления `RefreshCurrent()`

1. `GetDateRange()` считает диапазон дат под текущий режим (день / неделя / 6 недель для месяца / год).
2. События собираются **из всех смонтированных БД**, каждое помечается своим `DbId`, и фильтруются по активным коллекциям этой БД.
3. Применяется фильтр поиска (`Title.Contains`, без учёта регистра).
4. `UpdateTitle()` обновляет заголовок в навбаре (для прошедших дат — серый, для сегодня — красный).
5. Данные передаются в видимый вью-контрол через его `Refresh(records, …)`.

### Переключение режимов `SwitchView(idx)`

Все шесть контролов всегда в дереве; переключение — это смена `IsVisible` у нужного
и `RefreshCurrent()`. Навигация (`NavigatePrev`/`NavigateNext`) сдвигает `_currentDate`
с шагом под режим (день / 7 дней / месяц / год). Для Журнала навигация особая —
прокрутка к соседней группе внутри самого контрола.

### Создание / редактирование / удаление

Контроллер ловит события вью-контролов и открывает диалоги:

- создание → `OpenAddDialog` / `OpenAddAllDayDialog` ([RecordDialog](../../../Controls/Dialogs/RecordDialog.axaml.cs));
- редактирование → `OpenEditDialog`;
- удаление нескольких → `DeleteRecordsWithConfirm` ([ConfirmDialog](../../../Controls/Dialogs/ConfirmDialog.axaml.cs)) с русским склонением «запись/записи/записей»;
- дублирование → `DuplicateCalendarRecord`.

При нескольких смонтированных БД новое событие требует выбора целевой БД
(`ChooseTargetRepoAsync` → [SelectDatabaseDialog](../../../Controls/Dialogs/SelectDatabaseDialog.axaml.cs)).
После любой правки поднимается `CollectionsMayHaveChanged` (обновить сайдбар) и `RefreshCurrent()`.

## Режимы

| Режим | Индекс | Диапазон данных | Документ |
|---|---|---|---|
| Расписание | 0 | один день, плоский список | [schedule.md](schedule.md) |
| Журнал | 1 | лента по дням | [journal.md](journal.md) |
| День | 2 | один день, сетка часов | [day.md](day.md) |
| Неделя | 3 | 7 дней, сетка часов | [week.md](week.md) |
| Месяц | 4 | сетка 6×7 | [month.md](month.md) |
| Год | 5 | 12 мини-месяцев | [year.md](year.md) |

Общие для всех режимов жесты (выделение, контекстное меню, удаление, drag & drop)
вынесены в [interactions.md](interactions.md).

## Связанные документы

[[month]], [[interactions]], [[categories]], [[../../architecture/multi-database]]
