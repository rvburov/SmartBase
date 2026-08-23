# Справочник схемы БД

## Назначение

Полный справочник физической схемы: оба SQLite-файла, каждая таблица, каждая колонка,
ключи и индексы. Для архитектурного контекста («почему так») — [data-model.md](../data-model.md);
здесь — «что именно лежит». Актуально после миграции `SchemaCompact` (слияние таблиц
колонок в `column_meta`, `WITHOUT ROWID` у горячих таблиц).

## Где в коде

- Сущности: [Database/Entities/](../../../../Database/Entities/) —
  [CoreEntities.cs](../../../../Database/Entities/CoreEntities.cs) (данные),
  [UiEntities.cs](../../../../Database/Entities/UiEntities.cs) (проекции и UI-настройки),
  [ProfileEntities.cs](../../../../Database/Entities/ProfileEntities.cs) (пользователи/права/журнал),
  [DatabaseRefEntity.cs](../../../../Database/Entities/DatabaseRefEntity.cs) и
  [AppSettingEntity.cs](../../../../Database/Entities/AppSettingEntity.cs) (app.db).
- Маппинг, ключи и индексы: [AppDbContext.cs](../../../../Database/AppDbContext.cs) (`OnModelCreating`),
  [SystemDbContext.cs](../../../../Database/SystemDbContext.cs).
- Миграции: [Database/Migrations/](../../../../Database/Migrations/).

Типы указаны как в SQLite: `INTEGER`, `TEXT`; булевы значения — `INTEGER` 0/1.
«NULL» — колонка допускает пустое значение. Все даты — строки ISO
(лексикографическая сортировка = хронологическая).

---

## Файл 1: app.db (системный, один на приложение)

Реестр баз и настройки приложения. Никогда не показывается в сайдбаре, не удаляется,
изменения схемы — только аддитивно (см. [databases.md](storage-and-migrations.md)).

### `databases` — реестр подключенных баз

| Колонка | Тип | Описание |
|---|---|---|
| `Id` | INTEGER, PK | автоинкремент |
| `Name` | TEXT | отображаемое имя базы («Основная», …) |
| `Path` | TEXT | абсолютный путь к файлу `.db` |
| `Provider` | TEXT | пока всегда `sqlite`; задел под `postgres` |
| `IsDefault` | INTEGER 0/1 | дефолтная база; ее нельзя удалить из реестра |

### `app_settings` — настройки приложения (ключ → значение)

| Колонка | Тип | Описание |
|---|---|---|
| `Key` | TEXT, PK | ключ настройки (`scale`, `recordPanelOpen`, `autoUpdate`, `profile.*`, `ext.*`, `notifyTaskCursors`, …) |
| `Value` | TEXT | значение строкой; формат разбирает `AppSettings` (InvariantCulture) |

Ключи `hiddenViews.<раздел>` — скрытые виды разделов и модули панели навигации, строкой
«key;key» (хранятся именно скрытые: пусто = видно все, см.
[features/settings.md](../../features/settings.md#виды-и-их-видимость)).

Ключи `ext.<id расширения>.<имя настройки>` — значения, объявленные самими расширениями
(см. [features/extensions.md](../../features/extensions.md#настройки-расширения)). Их состав
в коде не фиксирован, а строки расширений, удаленных из сборки, убираются при старте.

Хранятся только отличия от дефолтов (константы в `AppSettings`); сброс = удаление строки.

---

## Файл 2: база конструктора (database.db и каждая подключенная)

Схема одинакова в каждом файле; накатывается миграциями при открытии
(`DatabaseManager.ApplySchemaMigrations`). 17 таблиц в четырех группах.

### Данные

#### `db_groups` — группы (папки) сайдбара

| Колонка | Тип | Описание |
|---|---|---|
| `Id` | INTEGER, PK | |
| `Name` | TEXT | имя группы |
| `ParentId` | INTEGER, NULL, FK→`db_groups.Id` | дерево групп; при удалении родителя — `SET NULL` |
| `Position` | INTEGER | порядок сортировки (шаг 1000) |

#### `db_collections` — коллекции (таблицы данных)

| Колонка | Тип | Описание |
|---|---|---|
| `Id` | INTEGER, PK | |
| `Name` | TEXT | имя коллекции |
| `GroupId` | INTEGER, NULL, FK→`db_groups.Id` | группа; при удалении группы — `SET NULL` (коллекция выживает) |
| `Position` | INTEGER | порядок в сайдбаре |
| `Icon` | TEXT | ключ иконки (дефолт `table`) |
| `Color` | TEXT | цвет `#RRGGBB` |

#### `db_fields` — поля (колонки) коллекций

| Колонка | Тип | Описание |
|---|---|---|
| `Id` | INTEGER, PK | |
| `CollectionId` | INTEGER, FK→`db_collections.Id`, CASCADE | |
| `Name` | TEXT | уникально в коллекции — индекс `(CollectionId, Name)` UNIQUE |
| `FieldType` | TEXT | `text` / `number` / `date` / `select` / `checkbox` / `url` / `user` / `markdown`; **вычисляемые** (read-only, значение из метаданных записи, НЕ из `db_field_values`): `created_at` / `updated_at` / `created_by` / `updated_by` |
| `ConfigJson` | TEXT | JSON-настройки типа (дефолт `{}`) |
| `Position` | INTEGER | внутренний порядок; порядок колонок таблицы — в `column_meta`. Назначается вызывающим кодом (`max + 1000`) и **обязан быть уникален** в коллекции — по нему строится карта полей при дубле/переносе |
| `IsCalStart` | INTEGER 0/1 | поле-дата видно в календаре (может быть у нескольких полей) |
| `IsCalEnd` | INTEGER 0/1 | не используется |
| `IsSystem` | INTEGER 0/1 | системное поле |
| `IsRequired` | INTEGER 0/1 | обязательное |
| `IsTaskExec` | INTEGER 0/1 | user-поле — источник раздела «Задачи» (исполнитель) |
| `IsTaskAssigner` | INTEGER 0/1 | авто-поле «Назначил» |
| `IsTaskStatus` | INTEGER 0/1 | авто-поле «Статус» |
| `IsDescription` | INTEGER 0/1 | поле «Описание»: панель записи рисует его нижним markdown-блоком, календарь/задачи пишут в него описание. Заводится ТОЛЬКО этими двумя путями (`EnsureDescriptionField`), в остальном — обычное поле |

#### `db_records` — записи

| Колонка | Тип | Описание |
|---|---|---|
| `Id` | INTEGER, PK | |
| `CollectionId` | INTEGER, FK→`db_collections.Id`, CASCADE | |
| `Title` | TEXT | системная колонка «Название» — единственный системный атрибут записи (описание переехало в обычное поле с ролью `IsDescription`, миграция `DescriptionAsField`) |
| `CreatedAt` | TEXT | ISO-дата создания (источник типа `created_at`) |
| `UpdatedAt` | TEXT | ISO-дата последней правки данных записи (источник типа `updated_at`); при создании = `CreatedAt`, у старых записей бэкфилл = `CreatedAt` |
| `CreatedBy` | TEXT | снимок имени автора создания (`CurrentUser.DisplayName`; источник `created_by`); пусто у записей, созданных до фичи |
| `UpdatedBy` | TEXT | снимок имени автора последней правки (источник `updated_by`) |
| `Position` | INTEGER | порядок строк; составной индекс `(CollectionId, Position)` — горячий путь открытия/скролла таблицы (keyset-пагинация) |
| `StartDt` | TEXT, NULL | legacy-дата записи: пишется, но календарем **не читается** (см. «Двойная система дат» в [data-model.md](../data-model.md)) |
| `EndDt` | TEXT, NULL | то же |
| `AllDay` | INTEGER 0/1 | то же |
| `Color` | TEXT, NULL | **не используется, всегда NULL.** Был сквозной цвет записи; уровень убран (`RowBackgroundBack`), значения переехали в `record_display.Bg`. Колонка оставлена: `DropColumn` на SQLite — перестройка самой большой таблицы базы. Копируется при дубле и переносе как есть |

#### `db_field_values` — значения полей (EAV, самая большая таблица)

| Колонка | Тип | Описание |
|---|---|---|
| `RecordId` | INTEGER, PK (составной), FK→`db_records.Id`, CASCADE | |
| `FieldId` | INTEGER, PK (составной), FK→`db_fields.Id`, CASCADE | |
| `Value` | TEXT | значение строкой; формат зависит от `FieldType` |

Одна строка = значение одного поля одной записи. Суррогатного Id нет; таблица
`WITHOUT ROWID` — кластеризована по PK `(RecordId, FieldId)`: значения одной записи
лежат физически рядом. Индекс `(FieldId, Value)` — фильтры по значению и `GROUP BY`
уникальных значений; он же покрывает выборку `RecordId` всех значений поля
(пакетные дубль/удаление поля).

Форматы `Value`: `date` — ISO-канон `DateValue.ToStorage`; `number` — сырой ввод
(`10.5`, `1,25`); `checkbox` — `1`/`0`/пусто; `select`/`user` — отображаемое имя строкой.

#### `categories` — категории

| Колонка | Тип | Описание |
|---|---|---|
| `Id` | INTEGER, PK | |
| `Name` | TEXT | уникальное имя |
| `Color` | TEXT | цвет `#RRGGBB` |

### Производные проекции

#### `calendar_index` — проекция календаря (кэш, пересобираемый)

| Колонка | Тип | Описание |
|---|---|---|
| `RecordId` | INTEGER, PK (составной), FK→`db_records.Id`, CASCADE | |
| `FieldId` | INTEGER, PK (составной), FK→`db_fields.Id`, CASCADE | какое поле-дата породило событие |
| `CollectionId` | INTEGER | для фильтра по включенным коллекциям |
| `StartIso` | TEXT | начало события, ISO |
| `EndIso` | TEXT | конец |
| `AllDay` | INTEGER 0/1 | «весь день» |

Строка на каждое непустое значение видимого поля-даты (запись × поле). Содержимое
целиком derived из `db_field_values` + `DbFields.IsCalStart`; поддерживается
инкрементально, при потере пересобирается. Таблица `WITHOUT ROWID`. Индексы:

| Индекс | Зачем |
|---|---|
| `(FieldId)` | пересбор/чистка по полю |
| `(CollectionId, StartIso)` | базовый range-query окна календаря |
| `(CollectionId, AllDay, StartIso, RecordId, FieldId)` | сплит окна по AllDay + полный порядок для «Расписания» (offset/keyset без дублей) |
| `(CollectionId, AllDay, EndIso, StartIso, RecordId, FieldId)` | спилловеры: события, начавшиеся до окна и пересекающие его |

### UI-настройки коллекций

FK-каскадов у этих таблиц нет (исключение — `cell_style`) — при удалении коллекции
строки чистятся явно (`DeleteCollectionUiRows`). Везде действует принцип «нет строки
= дефолт».

#### `column_meta` — все метаданные колонки одной строкой

| Колонка | Тип | Описание |
|---|---|---|
| `CollectionId` | INTEGER, PK (составной) | |
| `Key` | TEXT, PK (составной) | `__title__` или строковый id поля |
| `Position` | INTEGER, NULL | аспект «порядок» (окно «Отображение», перетаскивание); NULL = дефолтный порядок |
| `Visible` | INTEGER 0/1, NULL | аспект «видимость»; NULL = видима |
| `Level` | INTEGER, NULL | пресет-уровень; задается вместе с `Visible` |
| `DisplayName` | TEXT, NULL | аспект «подпись» системных колонок; NULL = имя по умолчанию |
| `Bg` | TEXT, NULL | аспект «фон колонки» `#RRGGBB`; NULL = без фона |
| `Fg` | TEXT, NULL | аспект «цвет текста колонки»; NULL = стандартный |
| `Width` | INTEGER, NULL | аспект «ручная ширина» в DIP; NULL = ширина не задана (стандартная) |

Каждый аспект независим; строка живет, пока задан хоть один (сброс последнего
удаляет строку). Заменила прежние `column_order` + `column_display` +
`system_field_meta` (миграция `SchemaCompact`), а с аспектом `Width` — и
`ui_col_widths` (миграции `ColumnMetaWidth` / `ColumnMetaWidthPg`).

> `Width` обязан участвовать в проверке «строка пуста» (`RemoveColumnMetaIfEmpty`):
> `SaveColumnDisplay` и `WriteColumnOrder` обнуляют свои аспекты у ВСЕХ строк коллекции,
> и без этой проверки строка с одной лишь сохраненной шириной была бы удалена, а ручные
> ширины молча терялись бы после окна «Отображение».

#### `record_display` — метаданные записи в таблице

| Колонка | Тип | Описание |
|---|---|---|
| `CollectionId` | INTEGER, PK (составной) | |
| `RecordId` | INTEGER, PK (составной) | |
| `Visible` | INTEGER 0/1 | false = запись скрыта (фильтр на уровне SQL) |
| `Level` | INTEGER | пресет-уровень (дефолт 1) |
| `Bg` | TEXT, NULL | аспект «фон строки» `#RRGGBB`; NULL = без фона |
| `Fg` | TEXT, NULL | аспект «цвет текста строки»; NULL = стандартный |

Аспекты независимы, как в `column_meta`: строка живет, пока задан хоть один. Поэтому
запись идет точечным upsert'ом (`SetRowStyle`, `SaveRecordDisplay`), а не «снести все
и переписать» — иначе сохранение видимости стирало бы цвет.

Колонка `Bg` пережила два разворота: заведена вместе с цветом записи, убрана как его
дубль (`DropRowBackground`) и возвращена, когда сквозной цвет записи убрали совсем
(`RowBackgroundBack`) — см. [data-model.md → «Цвет»](../data-model.md#цвет-оформление-таблицы).

#### `cell_style` — оформление отдельных ячеек

| Колонка | Тип | Описание |
|---|---|---|
| `CollectionId` | INTEGER, PK (составной) | |
| `RecordId` | INTEGER, PK (составной), FK→`db_records.Id`, CASCADE | |
| `Key` | TEXT, PK (составной) | `__title__` или строковый id поля — как в `column_meta` (числовым `FieldId` системную колонку «Название» покрасить было бы нельзя) |
| `Bg` | TEXT, NULL | фон ячейки `#RRGGBB` |
| `Fg` | TEXT, NULL | цвет текста ячейки |

Разрежена: строка только у покрашенной ячейки. Читается постранично вместе со
значениями (`GetCellStylesBatch` рядом с `GetFieldValuesBatch`) — префикс PK
`(CollectionId, RecordId)` закрывает запрос страницы сиком. Единственная из UI-таблиц
с FK-каскадом: удаление записи чистит ее ячейки само; чистку по УДАЛЕННОМУ ПОЛЮ
(ключ строковый, FK на поле невозможен) делает прикладной код там же, где чистит
`column_meta`.

#### `ui_col_widths` — ширины колонок (ЛЕГАСИ)

| Колонка | Тип | Описание |
|---|---|---|
| `CollectionId` | INTEGER, PK (составной) | |
| `FieldId` | INTEGER, PK (составной) | |
| `Width` | INTEGER | пиксели |

Таблицей конструктора **не используется**: ширины переехали в аспект `column_meta.Width`.
Причина та же, что у `cell_style`, — ключ здесь числовой, а системную колонку «Название»
(`__title__`) числовым `FieldId` не покрыть; пока она оставалась без сохраненной ширины,
она работала «резиновым буфером» и ломала ручной ресайз. Таблица сохранена, чтобы старые
базы читались без ошибок.

#### `ui_row_heights` — высоты строк

| Колонка | Тип | Описание |
|---|---|---|
| `CollectionId` | INTEGER, PK (составной) | |
| `RecordId` | INTEGER, PK (составной) | |
| `Height` | INTEGER | пиксели |

#### `ui_collection_settings` — настройки коллекции

| Колонка | Тип | Описание |
|---|---|---|
| `CollectionId` | INTEGER, PK | |
| `ColResize` | INTEGER 0/1 | разрешен ресайз колонок |
| `RowResize` | INTEGER 0/1 | разрешен ресайз строк |
| `ViewMode` | TEXT | режим отображения (дефолт `table`) |

### Пользователи, права, журнал (модуль «Профиль»)

Живут в файле базы (не в app.db): права и история путешествуют вместе с ним.
Подробно — [users-permissions.md](../users-permissions.md).

#### `db_users` — пользователи, известные базе

| Колонка | Тип | Описание |
|---|---|---|
| `Id` | INTEGER, PK | |
| `UserKey` | TEXT, UNIQUE | «учетка@компьютер», нижний регистр |
| `DisplayName` | TEXT | снапшот ФИО, обновляется при монтировании |
| `IsAdmin` | INTEGER 0/1 | первый открывший базу становится админом |
| `IsApproved` | INTEGER 0/1 | допуск (дает админ или верный пароль базы) |
| `CreatedAt` | TEXT | ISO, когда впервые открыл базу |
| `LastSeenAt` | TEXT | ISO, последнее монтирование базы этим пользователем (в журнал не пишется) |

#### `db_permissions` — права

| Колонка | Тип | Описание |
|---|---|---|
| `Id` | INTEGER, PK | |
| `UserId` | INTEGER, FK→`db_users.Id`, CASCADE | |
| `CollectionId` | INTEGER, NULL, FK→`db_collections.Id`, CASCADE | NULL = права на всю базу; не-NULL — переопределение для коллекции (приоритетнее) |
| `ActionsMask` | INTEGER | битовая маска `PermAction` |

Уникальный индекс `(UserId, CollectionId)` — одна маска на пользователя и область.
У админа строк нет — ему можно все.

#### `db_meta` — ключ-значение базы

| Колонка | Тип | Описание |
|---|---|---|
| `Key` | TEXT, PK | `password.hash` / `password.salt` / `password.iterations` / `changelog.retention.enabled` / `changelog.retention.months`, … |
| `Value` | TEXT | |

#### `change_log` — журнал изменений

| Колонка | Тип | Описание |
|---|---|---|
| `Id` | INTEGER (long), PK | rowid; чтение — keyset по `Id DESC` |
| `Ts` | TEXT | момент, ISO UTC |
| `UserKey` | TEXT | снапшот (не FK — журнал переживает удаление пользователя) |
| `UserName` | TEXT | снапшот ФИО |
| `Action` | TEXT | `create` / `update` / `delete` / `duplicate` / `import` / `move` / `export` |
| `TargetType` | TEXT | `collection` / `group` / `field` / `record` / `records` |
| `TargetName` | TEXT | снапшот имени объекта |
| `CollectionName` | TEXT, NULL | контекст для записей/свойств |
| `Count` | INTEGER, NULL | пакетные операции пишут одну строку с числом |
| `Detail` | TEXT, NULL | готовое «до → после» правки (`Utils/ChangeDetail`): переименования, ячейка/описание, `user.rename`; длинные тексты свернуты до изменившегося фрагмента еще при записи |

Без FK и без доп. индексов — сознательно (журнал переживает удаления, чтение по rowid).

#### `task_events` — очередь уведомлений по задачам

| Колонка | Тип | Описание |
|---|---|---|
| `Id` | INTEGER (long), PK | rowid; он же курсор чтения и хронология |
| `Ts` | TEXT | момент, ISO UTC |
| `Kind` | TEXT | `assigned` (назначили исполнителем) / `status` (сменилась стадия) |
| `RecipientKey` | TEXT | `UserKey` получателя; «» — имя не нашлось в `db_users` |
| `RecipientName` | TEXT | снапшот имени из ячейки (показ и опознание по имени) |
| `ActorKey`, `ActorName` | TEXT | кто сделал правку (снапшоты) |
| `RecordId`, `CollectionId`, `FieldId`, `ExecFieldId` | INTEGER | куда вести по клику; `ExecFieldId` нужен, потому что задача = запись × ее поле-исполнитель |
| `TaskTitle`, `CollectionName` | TEXT | снапшоты для текста баннера |
| `OldValue`, `NewValue` | TEXT | «было → стало» (у назначения «было» — прежний исполнитель) |

Индекс `(RecipientKey, Id)` — под догон после долгого простоя; обычный опрос идет диапазоном по
PK (`Id > курсор`) и чаще всего не находит ничего. Без FK — как у журнала: уведомление должно
доехать, даже если запись успели удалить. Строки пишет `SetFieldValue` (только правка
свойств-ролей «Исполнитель» и «Статус»), читают клиенты получателей по курсору из app.db
(`notifyTaskCursors`); события старше 90 суток удаляются при монтировании базы. Подробнее —
[features/notifications.md](../../features/notifications.md#уведомления-по-задачам).

---

## Сводные принципы

- **Только факты и отличия от дефолтов**: настройки (`app_settings`, `ui_*`,
  `column_meta`, `record_display`) хранят дельты; производное (`calendar_index`)
  пересобирается.
- **Целостность**: каскады на FK работают при `PRAGMA foreign_keys=ON` — включается
  на каждом подключении (`ApplyPerformancePragmas`); таблицы без FK чистятся явно.
- **Горячие таблицы** (`db_field_values`, `calendar_index`) — `WITHOUT ROWID`,
  кластеризация по естественному PK `(RecordId, FieldId)`.
- **Идентичность события календаря** — пара `(RecordId, FieldId)`: одна запись дает
  по событию на каждое видимое поле-дату.

## Связанные документы

[[data-model]], [[database/storage-and-migrations]], [[database/multi-database]], [[users-permissions]], [[performance/overview]]
