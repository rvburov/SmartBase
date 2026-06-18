# Режим «День»

## Назначение

Один день в виде вертикальной сетки часов (00:00–23:00) с событиями,
позиционированными по времени, и отдельной полосой событий «весь день» сверху.
Показывает красную линию текущего времени.

## Где в коде

- View + логика: [DayView.axaml.cs](../../../Views/ConstructorDb/Calendar/DayView.axaml.cs)
- Полоса all-day — общий виджет [AllDayBar](../../../Controls/Components/) (`AllDayBarCtrl`), он же используется в режиме «Неделя»
- Данные: `DayViewCtrl.Refresh(records, DateOnly date, scrollToNow)` от [CalendarControllerView](../../../Views/ConstructorDb/Calendar/CalendarControllerView.axaml.cs)

## Как работает

### Сетка часов

- Метки часов — `HourLabels` (01:00…23:00, `Enumerable.Range(1, 23)`).
- Событие с временем позиционируется на canvas по минутам от начала суток: `y ≈ минуты`, высота пропорциональна длительности. Текст внутри (`lineH`, `margins`, `timeH`) рассчитывается так, чтобы влезли заголовок и время.
- Пересекающиеся по времени события раскладываются по колонкам (`LayoutEntries(width)`): ширина каждого делится между параллельными событиями, чтобы они не перекрывались.

### События «весь день»

Все all-day и многодневные события уходят в `AllDayBarCtrl` — общий виджет полосы
сверху. У полосы есть **грип изменения высоты** (`AllDayResizeGrip`): тянешь —
полоса расширяется, показывая больше all-day событий. Виджет сам шлёт наверх свои
события (клик, перенос, удаление, дублирование, создание), `DayView` их
ретранслирует как собственные.

### Линия текущего времени

`_timeLineTimer` (раз в минуту) вызывает `UpdateTimeLine()` — двигает красную линию
«сейчас». При открытии режима сетка прокручивается к текущему времени
(`TimeScroll.Offset`, `scrollToNow`).

### Выделение и ghost-элементы

Выделенные события хранятся в словаре `_selections` (поддержка множественного
выделения). Во время перетаскивания/ресайза рисуется полупрозрачный
**ghost-элемент** с обновляемым текстом времени (`UpdateDragGhostText`,
`UpdateResizeGhostText`).

## Взаимодействия

- **Клик по пустому слоту** → `TimeSlotCreationRequested(date + время)` — создать событие на это время.
- **Клик по пустой полосе all-day** → `AllDayCreationRequested(date)`.
- **Перетаскивание события по вертикали** → перенос времени → `RecordMoved`.
- **Ресайз события** (за край) → изменение длительности → `RecordMoved`.
- **Двойной клик по событию** → `RecordClicked` (редактирование).
- Выделение, удаление (Delete), контекстное меню — по общим правилам ([interactions.md](interactions.md)).

### События контрола

`RecordClicked`, `RecordMoved`, `RecordDeleteRequested`, `RecordsDeleteRequested`,
`RecordDuplicateRequested`, `AllDayCreationRequested`, `TimeSlotCreationRequested`.

## Связанные документы

[[week]], [[overview]], [[interactions]]
