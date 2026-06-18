# Таблица коллекции (TableView)

## Назначение

Основной режим коллекции — таблица (спредшит): строки-записи × колонки-поля.
Поддерживает редактирование значений прямо в ячейках, изменение ширины колонок и
высоты строк, переупорядочивание и скрытие колонок/строк, мультивыделение.

## Где в коде

- View + логика: [TableView.axaml.cs](../../../Views/ConstructorDb/TableView.axaml.cs) (≈2300 строк — самый большой контрол проекта)
- Данные/настройки: [ConstructorRepository](../../../Database/Repositories/ConstructorRepository.cs)
- Хозяин: [ConstructorControllerView](../../../Views/ConstructorDb/ConstructorControllerView.axaml.cs) через `TableViewCtrl.Load(repository, collectionId)`

## Как работает

### Колонки

Колонки строятся из полей коллекции (`GetFields`) плюс системные «Название»
(`__title__`) и «Описание» (`__desc__`). Порядок, ширины, видимость и уровни
колонок берутся из UI-настроек коллекции:

- порядок — `GetColumnOrder` / `MoveColumn` / `MoveColumnAfter` / `SaveColumnOrder`;
- ширины — `GetColWidths` / `SaveColWidths`;
- видимость/уровни — `SaveColumnDisplay`;
- подписи системных колонок — `GetSystemFieldNames` / `SetSystemFieldName`.

Готовый список колонок отдаётся наверх через событие `ColumnsBuilt`
(`TableColumnDef`), а `MovableKeysInOrder()` / `MovableColumnsInOrder()` дают
текущий порядок для диалогов.

### Редактирование значений

Значения сохраняются в БД сразу при изменении ячейки (`SetFieldValue`), без кнопки
«сохранить». Тип ввода зависит от `FieldType` поля.

### Ресайз и режимы

- Ширина колонок: автоматическая или ручная (`SetColResizeMode`).
- Высота строк: автоматическая или ручная (`SetRowResizeMode`, `SaveRowHeights`).
  Режимы переключаются из контекстных меню заголовка колонки и строки.

### Сохранение прокрутки

При создании/удалении/дублировании записи в середине больших таблиц контроллер
снимает и восстанавливает вертикальную позицию (`CurrentVerticalOffset` /
`RestoreVerticalOffset`), а при добавлении в конец — прокручивает к новой строке
(`ScrollToRecord`) или к колонке «+» (`ScrollToAddPropertyColumn`), чтобы кнопки
добавления оставались в зоне видимости.

## Взаимодействия (события наверх)

| Событие | Назначение |
|---|---|
| `RecordClicked` | открыть запись в правой панели |
| `RecordCreateRequested (Open, AfterId)` | создать запись (в конце / после строки) |
| `RecordsDeleteRequested` / `RecordDuplicateRequested` | удалить / дублировать записи |
| `PropertyCreateRequested` / `PropertyCreateAfterRequested` | новое свойство (в конец / после колонки) |
| `PropertyEditRequested` / `PropertyDuplicateRequested` / `PropertiesDeleteRequested` | правка/копия/удаление свойств |
| `SystemFieldRenameRequested` | переименовать «Название»/«Описание» |
| `ColumnDisplayRequested` / `RecordDisplayRequested` | окна «Отображение» колонок / записей |

Мультивыделение строк + клавиша Delete → `RecordsDeleteRequested`; то же для
свойств → `PropertiesDeleteRequested` (обязательные поля защищены).

## Связанные документы

[[overview]], [[record-page]], [[../../ui/dialogs]]
