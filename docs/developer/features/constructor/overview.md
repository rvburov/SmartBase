# Конструктор БД — обзор

## Назначение

Конструктор — это «универсальный конструктор данных» (в духе Notion/Airtable):
пользователь создает **коллекции** (таблицы) с произвольными **полями**
(свойствами) и наполняет их **записями** (строками). Коллекции группируются в
**группы**. Календарь — это отдельное представление записей, у которых есть даты.

## Модель данных

Определена в [Constructor.cs](../../../../Core/Models/Constructor.cs):

| Класс | Что это |
|---|---|
| `DbGroup` | группа (папка) коллекций; может быть вложенной (`ParentId`) |
| `DbCollection` | коллекция/таблица: имя, иконка, цвет, группа, позиция |
| `DbField` | поле/свойство: тип (`text/number/date/select/checkbox/url`), `ConfigJson`, `IsCalStart` (= «показывать это поле-дату в календарем»; включается тумблером, может быть у нескольких полей-дат), `IsCalEnd` (не используется), `IsSystem`, `IsRequired` |
| `DbRecord` | запись/строка: `Title`, позиция; значения полей (включая даты и описание) хранятся отдельно в `db_field_values` |

Значения произвольных полей записи хранятся отдельной таблицей «значение по
(recordId, fieldId)» и читаются через `GetFieldValues(recordId)`.

## Где в коде

- Оркестратор: [ConstructorControllerView.axaml.cs](../../../../Views/ConstructorDb/ConstructorControllerView.axaml.cs)
- Доступ к данным: [ConstructorRepository.cs](../../../../Database/Repositories/ConstructorRepository.cs) (≈1500 строк — ядро всего модуля)
- Представления коллекции: [TableView](../../../../Views/ConstructorDb/TableView.axaml.cs) (таблица) и [ConstructorListView](../../../../Views/ConstructorDb/ConstructorListView.axaml.cs) (список-карточки)
- Деталь записи: [RecordPageView](../../../../Views/ConstructorDb/RecordPageView.axaml.cs)

## Как работает

`ConstructorControllerView` держит активный `ConstructorRepository` и
`_currentCollectionId`. В основной области одновременно открыта **одна** коллекция
(мультибаза живет в сайдбаре). `SelectCollection(id)` загружает коллекцию: ставит
заголовок навбара, читает UI-настройки (`GetUiSettings`) и переключает режим
(`SwitchView`), обновляет счетчик в футере.

### Два режима показа коллекции

- **Таблица** (`TableView`) — основной режим, см. [tables.md](table.md).
- **Список** (`ConstructorListView`) — карточки: имя + статус (из первого `select`-поля) + дата (из первого `date`-поля).

Режим хранится в UI-настройках коллекции (`viewMode`) и переключается из навбара.

### Записи

- Открыть → `RecordPageRequested` (MainWindow показывает [RecordPageView](record-page.md) в правой панели).
- Создать → `CreateAndOpen`: `AddRecord` (в конец) или `AddRecordAfter` (после строки из меню). При создании в середине сохраняется вертикальная прокрутка таблицы, чтобы ее «не откидывало».
- Удалить → одиночно или пакетно ([ConfirmDialog](../../../../Controls/Dialogs/ConfirmDialog.axaml.cs) + русское склонение).
- Дублировать → `DuplicateRecord` (копия встает сразу после исходной).

### Свойства (поля)

Управляются из контекстного меню заголовков колонок:

- создать ([PropertyEditorDialog](../../../../Controls/Dialogs/PropertyEditorDialog.axaml.cs)) — в конец или «после колонки» (`MoveColumnAfter`);
- редактировать, дублировать;
- удалить пакетно (обязательные `IsRequired` защищены);
- переименовать системную колонку «Название» (меняется только подпись, физическая колонка не трогается);
- «Отображение» ([DisplayDialog](../../../../Controls/Dialogs/DisplayDialog.axaml.cs)) — видимость/уровни колонок; «Отображение записей» ([RecordDisplayDialog](../../../../Controls/Dialogs/RecordDisplayDialog.axaml.cs)) — видимость/порядок строк.

### Мультибаза

`OpenFrom(repository, collectionId)` перенацеливает модуль на репозиторий другой
БД. `DetachDatabase(fallback)` вызывается, когда БД текущей коллекции
размонтирована. Подробнее — [architecture/multi-database.md](../../architecture/database/multi-database.md).

### События контроллера

`CollectionsChanged` (обновить сайдбар), `RecordPageRequested`, `RecordPageClosed`,
`RecordPanelToggled`.

## Связанные документы

[[table]], [[record-page]], [[../../architecture/data-model]], [[../calendar/overview]]
