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
| `DbField` | поле/свойство: тип (`text/number/date/select/checkbox/url`), `ConfigJson`, `IsCalStart` (= «показывать это поле-дату в календарем»; у нового свойства СНЯТ, включается тумблером, может быть у нескольких полей-дат), `IsCalEnd` (не используется), `IsSystem`, `IsRequired` |
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

### Настройки раздела

«Настройки → Конструктор базы данных»
([SettingsConstructorPanel.cs](../../../../Views/Settings/SettingsConstructorPanel.cs), ключи —
[settings.md](../settings.md#разделы)). Живой сервис
[ConstructorSettings](../../../../Utils/ConstructorSettings.cs) по образцу `TaskSettings`: кэш
значений из app.db, `Set*` сохраняют дельту и поднимают `Changed`, `Stamp` — для догоняющей
перерисовки. Сервис отдает примитивы: списки типов и цветов собирает панель настроек,
применяют сами вью — Utils не тянет за собой Views. Стандарт каждой настройки равен поведению
программы до ее появления, поэтому чистый app.db ничего не меняет.

| Настройка | Что делает | Где действует |
|---|---|---|
| Тип нового свойства (`NewFieldType`) | тип в окне создания свойства; при изменении существующего не участвует | `PropertyEditorDialog`: `_selectedType` в ветке `editFieldId is null` |
| Открывать новую запись (`OpenNewRecord`) | подставляется в `Open` события `RecordCreateRequested` | `TableView`: «+»-строка, меню пустой области, меню строки. «Список» открывает всегда |
| Подтверждение удаления (`ConfirmDelete`) | снимает рутинный вопрос | `DeleteRecordsWithConfirm`, `DeletePropertiesWithConfirm`; при непустых `linkWarn`/`roleWarn` окно показывается всегда. Он же — вопрос перед очисткой больше `ClearConfirmThreshold` (10) ячеек в `TableView.ClearCellsAsync` |
| Цвет новой коллекции (`NewCollectionColor`) | «#RRGGBB», мусор → стандартный синий | `CollectionDialogViewModel` (создание), `SidebarTree` (создание из дерева) |
| Плотность строк (`RowHeight`) | 28/36/44 → ресурс `RowHeightTable` | `TableView` (`RowHeight`, высота шапки, `StandardRowHeight`), `SidebarRow.CellHeight` — строки выровнены и двигаются вместе |
| Ширина столбца (`ColWidth`) | база авто-режима и ширина колонки без ручной | `TableView.MinColWidth` (свойство, не константа); `MinManualColWidth` = 48 не меняется |
| Ручные размеры (`ColResize`, `RowResize`) | значения для коллекции без своей записи `ui_collection_settings` — а ее заводит только ручное переключение режима в меню (`SaveUiSettings`), поэтому настройка действует на все коллекции, которых меню не касалось, а не только на новые | `ConstructorRepository.GetUiSettings` — рядом с `constructorView` |
| Открывать запись двойным кликом (`OpenByDoubleClick`) | одиночный клик по столбцу «№» (`DisplayIndex == 0`) только выделяет строку; на редактор ячейки не влияет | `OnCellPointerPressed`: `ClickCount >= 2` |
| Вид описания (`DescriptionPreview`) | начальное состояние `FooterBar.IsPreview` | `RecordPageView.Show`; переключатель футера работает как прежде |
| Восстановление записи (`RestoreRecord`) | открыть запись прошлого сеанса | `MainWindow.RestoreLastRecordPanel` фоновым приоритетом; молчит, если запись/база пропали |
| Память раскладки (`RememberLayout`) | высота блока свойств и ширина колонки названий на диск | `RecordPageView`: читается при создании, пишется в конце перетаскивания ручки |
| Память отбора (`RememberFilters`) | фильтры и сортировка за коллекцией | `TableView.Load` (восстановление) и `ApplyFilters` (сохранение уже вычищенного отбора; `Save` сверяет с лежащим и молчит при совпадении — иначе каждый тик поллера переписывал бы app.db) |
| Период опроса (`PollSeconds`) | 3/10/30 с, в фоне ×5 | `ChangePoller.ApplyPeriod` — таймер пересаживается на лету, режим окна помнится в `_foreground` |

Строки «Отображение по умолчанию» и «Отображаемые виды» переехали сюда из «Оформления» без
переименования ключей (`constructorView`, `hiddenViews.constructor`); механизм `InitAppearance`
общий. Запомненный отбор лежит в app.db одной JSON-строкой
([ConstructorFilterMemory](../../../../Utils/ConstructorFilterMemory.cs)), а не в схеме
пользовательской базы: это личная раскладка экрана, и колонка в общей базе навязывала бы ее
всем. Покрытие — `ConstructorSettingsTests`, `ConstructorFilterMemoryTests`.

## Связанные документы

[[table]], [[record-page]], [[../../architecture/data-model]], [[../calendar/overview]]
