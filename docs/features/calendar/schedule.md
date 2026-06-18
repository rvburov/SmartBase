# Режим «Расписание»

## Назначение

Плоский список событий **одного дня** (повестка дня). Заголовок в навбаре —
дата с цветовой подсказкой: сегодня — красная, прошлое — серое, будущее — обычная.

> В коде режим называется `ListView` (индекс 0). В интерфейсе — «Расписание».

## Где в коде

- View + логика: [ListView.axaml.cs](../../../Views/ConstructorDb/Calendar/ListView.axaml.cs)
- Элемент списка: класс `EventListItem` (там же)
- Данные: `ListViewCtrl.Refresh(records, currentDate)`; диапазон от контроллера — один день

## Как работает

`Refresh(events, currentDate)` оставляет события, у которых
`StartDt.Date == currentDate.Date`, и сортирует: сначала «весь день», затем по
времени начала. Каждое оборачивается в `EventListItem`:

- `Title` = `DisplayTitle` события;
- `DateTimeText` = «Весь день» или `HH:mm – HH:mm`;
- `Description` = текст без markdown-разметки ([MarkdownUtils.StripMarkdown](../../../Utils/MarkdownUtils.cs));
- `ColorBrush` = цвет события ([EventColors.Display](../../../Core/Models/EventColors.cs));
- прошедший день → заголовок серый (`#8E8E93`).

Если событий нет — показывается подсказка `EmptyHint`.

## Взаимодействия

- **Клик** — выделить; **Shift+клик** — добавить/снять (множественное выделение).
- **Двойной клик по событию** → `RecordClicked` (редактирование).
- **Двойной клик по фону** / пункт «Создать» в меню фона → `CreateRequested(currentDate)`.
- **Delete/Backspace** — удалить выделенное (`RecordDeleteRequested` / `RecordsDeleteRequested`).
- **Правый клик** — контекстное меню ([EventContextMenu](../../../Controls/Components/)): по событию — «Редактировать»/«Удалить», по нескольким — «Удалить N», по фону — «Создать».

Подробнее об общих правилах — [interactions.md](interactions.md).

## Связанные документы

[[journal]], [[overview]], [[interactions]]
