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

### `databases` — реестр подключённых баз

| Колонка | Тип | Описание |
|---|---|---|
| `Id` | INTEGER, PK | автоинкремент |
| `Name` | TEXT | отображаемое имя базы («Основная», …) |
| `Path` | TEXT | абсолютный путь к файлу `.db` |
| `Provider` | TEXT | пока всегда `sqlite`; задел под `postgres` |
| `IsDefault` | INTEGER 0/1 | дефолтная база; её нельзя удалить из реестра |

### `app_settings` — настройки приложения (ключ → значение)

| Колонка | Тип | Описание |
|---|---|---|
| `Key` | TEXT, PK | ключ настройки (`scale`, `recordPanelOpen`, `autoUpdate`, `profile.*`, …) |
| `Value` | TEXT | значение строкой; формат разбирает `AppSettings` (InvariantCulture) |

Хранятся только отличия от дефолтов (константы в `AppSettings`); сброс = удаление строки.

---

## Файл 2: база конструктора (database.db и каждая подключённая)

Схема одинакова в каждом файле; накатывается миграциями при открытии
(`DatabaseManager.ApplySchemaMigrations`). 16 таблиц в четырёх группах.

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

#### `db_records` — записи

| Колонка | Тип | Описание |
|---|---|---|
| `Id` | INTEGER, PK | |
| `CollectionId` | INTEGER, FK→`db_collections.Id`, CASCADE | |
| `Title` | TEXT | системная колонка «Название» |
| `Description` | TEXT, NULL | системная колонка «Описание» |
| `CreatedAt` | TEXT | ISO-дата создания (источник типа `created_at`) |
| `UpdatedAt` | TEXT | ISO-дата последней правки данных записи (источник типа `updated_at`); при создании = `CreatedAt`, у старых записей бэкфилл = `CreatedAt` |
| `CreatedBy` | TEXT | снимок имени автора создания (`CurrentUser.DisplayName`; источник `created_by`); пусто у записей, созданных до фичи |
| `UpdatedBy` | TEXT | снимок имени автора последней правки (источник `updated_by`) |
| `Position` | INTEGER | порядок строк; составной индекс `(CollectionId, Position)` — горячий путь открытия/скролла таблицы (keyset-пагинация) |
| `StartDt` | TEXT, NULL | legacy-дата записи: пишется, но календарём **не читается** (см. «Двойная система дат» в [data-model.md](../data-model.md)) |
| `EndDt` | TEXT, NULL | то же |
| `AllDay` | INTEGER 0/1 | то же |

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
| `CollectionId` | INTEGER | для фильтра по включённым коллекциям |
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

FK-каскадов у этих таблиц нет — при удалении коллекции строки чистятся явно
(`DeleteCollectionUiRows`). Везде действует принцип «нет строки = дефолт».

#### `column_meta` — все метаданные колонки одной строкой

| Колонка | Тип | Описание |
|---|---|---|
| `CollectionId` | INTEGER, PK (составной) | |
| `Key` | TEXT, PK (составной) | `__title__` / `__desc__` или строковый id поля |
| `Position` | INTEGER, NULL | аспект «порядок» (окно «Отображение», перетаскивание); NULL = дефолтный порядок |
| `Visible` | INTEGER 0/1, NULL | аспект «видимость»; NULL = видима |
| `Level` | INTEGER, NULL | пресет-уровень; задаётся вместе с `Visible` |
| `DisplayName` | TEXT, NULL | аспект «подпись» системных колонок; NULL = имя по умолчанию |

Каждый аспект независим; строка живёт, пока задан хоть один (сброс последнего
удаляет строку). Заменила прежние `column_order` + `column_display` +
`system_field_meta` (миграция `SchemaCompact`).

#### `record_display` — видимость записей

| Колонка | Тип | Описание |
|---|---|---|
| `CollectionId` | INTEGER, PK (составной) | |
| `RecordId` | INTEGER, PK (составной) | |
| `Visible` | INTEGER 0/1 | false = запись скрыта (фильтр на уровне SQL) |
| `Level` | INTEGER | пресет-уровень (дефолт 1) |

#### `ui_col_widths` — ширины колонок

| Колонка | Тип | Описание |
|---|---|---|
| `CollectionId` | INTEGER, PK (составной) | |
| `FieldId` | INTEGER, PK (составной) | |
| `Width` | INTEGER | пиксели |

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
| `ColResize` | INTEGER 0/1 | разрешён ресайз колонок |
| `RowResize` | INTEGER 0/1 | разрешён ресайз строк |
| `ViewMode` | TEXT | режим отображения (дефолт `table`) |

### Пользователи, права, журнал (модуль «Профиль»)

Живут в файле базы (не в app.db): права и история путешествуют вместе с ним.
Подробно — [users-permissions.md](../users-permissions.md).

#### `db_users` — пользователи, известные базе

| Колонка | Тип | Описание |
|---|---|---|
| `Id` | INTEGER, PK | |
| `UserKey` | TEXT, UNIQUE | «учётка@компьютер», нижний регистр |
| `DisplayName` | TEXT | снапшот ФИО, обновляется при монтировании |
| `IsAdmin` | INTEGER 0/1 | первый открывший базу становится админом |
| `IsApproved` | INTEGER 0/1 | допуск (даёт админ или верный пароль базы) |
| `CreatedAt` | TEXT | ISO, когда впервые открыл базу |

#### `db_permissions` — права

| Колонка | Тип | Описание |
|---|---|---|
| `Id` | INTEGER, PK | |
| `UserId` | INTEGER, FK→`db_users.Id`, CASCADE | |
| `CollectionId` | INTEGER, NULL, FK→`db_collections.Id`, CASCADE | NULL = права на всю базу; не-NULL — переопределение для коллекции (приоритетнее) |
| `ActionsMask` | INTEGER | битовая маска `PermAction` |

Уникальный индекс `(UserId, CollectionId)` — одна маска на пользователя и область.
У админа строк нет — ему можно всё.

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
| `Detail` | TEXT, NULL | готовое «до → после» правки (`Utils/ChangeDetail`): переименования, ячейка/описание, `user.rename`; длинные тексты свёрнуты до изменившегося фрагмента ещё при записи |

Без FK и без доп. индексов — сознательно (журнал переживает удаления, чтение по rowid).

---

## Сводные принципы

- **Только факты и отличия от дефолтов**: настройки (`app_settings`, `ui_*`,
  `column_meta`, `record_display`) хранят дельты; производное (`calendar_index`)
  пересобирается.
- **Целостность**: каскады на FK работают при `PRAGMA foreign_keys=ON` — включается
  на каждом подключении (`ApplyPerformancePragmas`); таблицы без FK чистятся явно.
- **Горячие таблицы** (`db_field_values`, `calendar_index`) — `WITHOUT ROWID`,
  кластеризация по естественному PK `(RecordId, FieldId)`.
- **Идентичность события календаря** — пара `(RecordId, FieldId)`: одна запись даёт
  по событию на каждое видимое поле-дату.

## Связанные документы

[[data-model]], [[database/storage-and-migrations]], [[database/multi-database]], [[users-permissions]], [[performance/overview]]
