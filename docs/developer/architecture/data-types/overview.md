# Типы полей (свойств) — реестр

Тип свойства — **сквозная** сущность: одни и те же поля видны в **таблице**, в **панели
записи**, питают **календарь** (поля-даты) и **задачи** (user-поля). Классификация, подписи и
формат — единый источник [`FieldTypeInfo`](../../../../Core/Models/FieldTypeInfo.cs) (`Label` /
`IsComputed` / `IsDateLike` / `ComputedValue` / `FormatComputedDisplay`); за рендер (какой
редактор, read-only) отвечает каждый режим отдельно — таблица
([tables.md](../../features/constructor/table.md)), панель
([record-page.md](../../features/constructor/record-page.md)).

**Базовые** — значение правит пользователь, хранится строкой в `db_field_values`:

| Тип | Хранение / семантика |
|---|---|
| `text` | строка как есть |
| `number` | сырой ввод цифр; десятичный разделитель канонизируется под настройку поля (запятая/точка). Формат показа (обычное/разряды/деньги/проценты) — в `ConfigJson` (`NumberFormat`); типизация `WriterHelpers.TryNumber` (запятая → точка) |
| `date` | ISO-канон `DateValue.ToStorage`; показ `DateValue.ToDisplay`; при `IsCalStart` проецируется в календарь (см. «Где календарь») |
| `select` | строка-значение из опций (`ConfigJson`); опции задает пользователь в диалоге свойства |
| `status` | как `select` (строка из опций `ConfigJson`); новое поле сразу с тремя статусами по умолчанию; тип авто-поля «Статус» в разделе задач |
| `multiselect` | **несколько** опций из `ConfigJson`; значение — JSON-массив (`MultiSelectValue`); показ — текст через выбранный разделитель (`separator` в `ConfigJson`), редактор — виртуализированный выпадающий чеклист с поиском |
| `checkbox` | `1`/`0` (+легаси `true`); показ `✓`/пусто (`WriterHelpers.IsChecked`) |
| `url` | строка |
| `file` | абсолютный путь к файлу строкой (ничего не копируется); показ — имя файла синей ссылкой (клик — открыть через ОС) / «файл не найден» / «Выбрать файл…»; правка — ручной ввод пути + иконка-скрепка (системный выбор), редактор `FileEditor` |
| `user` | строка-имя (снимок, как `select`); опции — `GetUserOptions` (допущенные пользователи), новая запись = текущий пользователь; флаги задач `IsTaskExec/IsTaskAssigner/IsTaskStatus` (см. [tasks/overview.md](../../features/tasks.md)) |

**Связь** (`relation`) — значение НЕ в `db_field_values`, а в таблице ребер `db_record_links`
([`DbRecordLinkEntity`](../../../../Database/Entities/CoreEntities.cs)); ячейка read-only, правится
выбором в списке. Настройка (целевая коллекция + one/many + разделитель) —
[`RelationConfig`](../../../../Core/Models/RelationConfig.cs) в ConfigJson. Показ — в стиле
«Мультисписка»: названия связанных записей текстом через разделитель, справа иконка-стрелка
открывает чеклист. Подробности — [relation.md](relation.md).

**Роль «Описание»** — не отдельный тип, а флаг `IsDescription` на обычном `text`-поле: панель
записи открывает на нем нижний markdown-блок, календарь и задачи пишут в него описание. Заводят
поле только эти два пути; удаляется и меняет тип оно как любое свойство —
[description.md](description.md).

**Вычисляемые** — read-only, значение НЕ в `db_field_values`, а из аудит-метаданных `db_records`
(автор — снимок `CurrentUser.DisplayName`, стамп при создании/правке; подробности —
[tables.md](../../features/constructor/table.md#вычисляемые-типы-авто-метаданные-записи)):

| Тип | Источник (`db_records`) |
|---|---|
| `created_at` | `CreatedAt` |
| `updated_at` | `UpdatedAt` |
| `created_by` | `CreatedBy` |
| `updated_by` | `UpdatedBy` |

## Разбор по типам

Каждый тип — отдельным файлом:

- [`text` — «Текст»](text.md)
- [`number` — «Число»](number.md)
- [`date` — «Дата»](date.md)
- [`select` — «Список»](select.md)
- [`status` — «Статус»](status.md)
- [`multiselect` — «Мультисписок»](multiselect.md)
- [`checkbox` — «Чекбокс»](checkbox.md)
- [`url` — «Ссылка»](url.md)
- [`file` — «Файл»](file.md)
- [`relation` — «Связь»](relation.md)
- [`user` — «Пользователь»](user.md)
- [Роль «Описание» — поле календаря и задач](description.md)
- [Вычисляемые — `created_*`/`updated_*`](computed.md)

Смена типа между «дата» и остальными приводит значения без потери (`ConvertValueForType`).
Покрытие — `FieldTypeMatrixTests` (реестр типов) и `ConstructorRepositoryCrudTests` (метаданные).

## Связанные документы

[data-model](../data-model.md) · [features/constructor/table](../../features/constructor/table.md) · [features/constructor/record-page](../../features/constructor/record-page.md)
