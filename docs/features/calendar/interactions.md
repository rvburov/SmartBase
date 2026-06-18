# Календарь — общие взаимодействия

Жесты выделения, удаления, контекстного меню, drag & drop работают одинаково во
всех режимах. Каждый вью-контрол лишь поднимает события наверх, а
[CalendarControllerView](../../../Views/ConstructorDb/Calendar/CalendarControllerView.axaml.cs)
открывает диалоги и пишет в нужную БД.

## Выделение

- **Одиночный клик** по событию — выделить только его (остальные снимаются).
- **Shift+клик** — аддитивно: добавить или снять событие из набора (множественное выделение).
- **Клик по пустому месту** — снять всё выделение. Реализовано глобальным обработчиком `PointerPressed` (tunnel) в `OnAttachedToVisualTree`. Исключения: **правый клик** и **Shift+клик** выделение не сбрасывают.
- **Правый клик по невыделенному событию** — сначала выделяет его одно, чтобы меню действовало на видимо выделенное.

## Удаление

- Клавиши **Delete / Backspace** удаляют выделенное, пока режим виден (`IsVisible`) и фокус не в текстовом поле (`TextBox`/`AutoCompleteBox`).
- Одно событие → `RecordDeleteRequested`; несколько → `RecordsDeleteRequested` (контроллер показывает [ConfirmDialog](../../../Controls/Dialogs/ConfirmDialog.axaml.cs) с русским склонением «запись/записи/записей»).

## Контекстное меню

Фабрика [EventContextMenu](../../../Controls/Components/) даёт три варианта:

- `Single` — по одному событию: «Редактировать» (`RecordClicked`) / «Удалить» (`RecordDeleteRequested`);
- `Multi` — по нескольким выделенным: «Удалить N»;
- `Background` — по пустому месту: «Создать» (`CreateRequested` с датой под курсором).

В «Дне»/«Неделе»/«Месяце» меню также содержит дублирование (`RecordDuplicateRequested`).

## Создание

- **Двойной клик по фону** — создать событие на соответствующую дату/время.
- **Месяц**: клик по «+» в кружке дня → создать в этот день.
- **День/Неделя**: клик по слоту времени → `TimeSlotCreationRequested` (дата + время); клик по полосе all-day → `AllDayCreationRequested`.

## Drag & Drop и ресайз

- **Месяц** — перетаскивание чипа/бара на другой день (порог 6px, превью на целевом дне) → `RecordMoved`.
- **День/Неделя** — перетаскивание по времени (и между днями в «Неделе») и ресайз за край для изменения длительности → `RecordMoved`. Во время операции рисуется полупрозрачный ghost с обновляемым временем.

## Что делает контроллер с этими событиями

| Событие вью | Действие контроллера |
|---|---|
| `RecordClicked` | `OpenEditDialog` → [RecordDialog](../../../Controls/Dialogs/RecordDialog.axaml.cs) |
| `CreateRequested` / `TimeSlotCreationRequested` / `AllDayCreationRequested` | `OpenAddDialog` (+ выбор БД при мультибазе) |
| `RecordMoved` | `UpdateCalendarRecord` в БД события |
| `RecordDeleteRequested` / `RecordsDeleteRequested` | удаление (с подтверждением для нескольких) |
| `RecordDuplicateRequested` | копия события в той же БД |

После любой правки контроллер поднимает `CollectionsMayHaveChanged` (обновить
сайдбар) и вызывает `RefreshCurrent()`.

## Связанные документы

[[overview]], [[month]], [[day]], [[../../architecture/multi-database]]
