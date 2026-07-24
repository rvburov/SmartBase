# Документация проекта

Календарь-Планировщик на базе универсального конструктора данных (Avalonia + .NET +
EF Core/SQLite). Документация разделена на два раздела:

- **[developer/](developer/)** — как устроено и работает приложение (для разработчиков) —
  основной раздел.
- **[user/](user/)** — инструкция для пользователя (заглушка, в разработке).

Планы развития — [ROADMAP.md](ROADMAP.md).

---

## Для разработчиков ([developer/](developer/))

### Архитектура ([developer/architecture/](developer/architecture/))

- [overview.md](developer/architecture/overview.md) — слои, запуск, MVVM/code-behind.
- [data-model.md](developer/architecture/data-model.md) — доменные модели и хранилища.
- **Типы свойств** ([data-types/](developer/architecture/data-types/)) — сквозной реестр типов
  полей, **файл на тип**: [обзор](developer/architecture/data-types/overview.md) ·
  [text](developer/architecture/data-types/text.md) ·
  [number](developer/architecture/data-types/number.md) ·
  [date](developer/architecture/data-types/date.md) ·
  [select](developer/architecture/data-types/select.md) ·
  [status](developer/architecture/data-types/status.md) ·
  [multiselect](developer/architecture/data-types/multiselect.md) ·
  [checkbox](developer/architecture/data-types/checkbox.md) ·
  [url](developer/architecture/data-types/url.md) ·
  [user](developer/architecture/data-types/user.md) ·
  [computed](developer/architecture/data-types/computed.md).
- **База данных** ([database/](developer/architecture/database/)):
  [schema](developer/architecture/database/schema.md) (справочник всех таблиц/колонок) ·
  [storage-and-migrations](developer/architecture/database/storage-and-migrations.md) (два файла, миграции, пути, WAL) ·
  [multi-database](developer/architecture/database/multi-database.md) (несколько БД одновременно).
- [users-permissions.md](developer/architecture/users-permissions.md) — пользователи, допуск, права, пароль, журнал.
- **Производительность** ([performance/](developer/architecture/performance/)):
  [overview](developer/architecture/performance/overview.md) (принципы, индексы, узкие места) ·
  [hot-paths](developer/architecture/performance/hot-paths.md) (таблица, фильтр, импорт, экспорт) ·
  [calendar](developer/architecture/performance/calendar.md) ·
  [changelog](developer/architecture/performance/changelog.md).

### Фичи ([developer/features/](developer/features/))

- **Календарь** ([calendar/](developer/features/calendar/)): [overview](developer/features/calendar/overview.md) · [month](developer/features/calendar/month.md) · [week](developer/features/calendar/week.md) · [day](developer/features/calendar/day.md) · [year](developer/features/calendar/year.md) · [schedule](developer/features/calendar/schedule.md) · [journal](developer/features/calendar/journal.md) · [interactions](developer/features/calendar/interactions.md) · [diagram](developer/features/calendar/diagram.md).
- **Конструктор** ([constructor/](developer/features/constructor/)): [overview](developer/features/constructor/overview.md) · [table](developer/features/constructor/table.md) · [record-page](developer/features/constructor/record-page.md).
- **Импорт/экспорт** ([import-export/](developer/features/import-export/)): [overview](developer/features/import-export/overview.md) · [import](developer/features/import-export/import.md) · [export](developer/features/import-export/export.md) · [cross-database-move](developer/features/import-export/cross-database-move.md).
- [tasks.md](developer/features/tasks.md) — раздел «Задачи».
- [categories.md](developer/features/categories.md) · [settings.md](developer/features/settings.md) · [notifications.md](developer/features/notifications.md) · [autostart-background.md](developer/features/autostart-background.md) · [profile.md](developer/features/profile.md) · [extensions.md](developer/features/extensions.md) · [logging.md](developer/features/logging.md) · [icons.md](developer/features/icons.md).

### UI ([developer/ui/](developer/ui/))

- [widgets.md](developer/ui/widgets.md) · [dialogs.md](developer/ui/dialogs.md) · [theming.md](developer/ui/theming.md).

### Гайды ([developer/guides/](developer/guides/))

- [build-and-run.md](developer/guides/build-and-run.md) — сборка и запуск: dev-режим, релиз по ОС, версия/дата.
- [developer-guide.md](developer/guides/developer-guide.md) — миграции, соглашения, раскладка вывода.
- [postgresql-setup.md](developer/guides/postgresql-setup.md) — PostgreSQL: сервер, база, подключение.
- [auto-update.md](developer/guides/auto-update.md) — автообновление (сетевая папка + установщик).

### Тестирование ([developer/testing/](developer/testing/))

- [overview.md](developer/testing/overview.md) — точка входа: автотесты (Unit/Integration), бенчмарки, многопроцессные проверки, генератор стенда. Команды запуска — также в корневом [README.md](../README.md#тесты).

### QA-чеклисты ([developer/qa-checklists/](developer/qa-checklists/))

Ручные прогоны фич (что нажать → что должно получиться): [overview.md](developer/qa-checklists/overview.md) · [calendar-diagram](developer/qa-checklists/calendar-diagram.md) · [import](developer/qa-checklists/import.md) · [export](developer/qa-checklists/export.md) · [profile](developer/qa-checklists/profile.md).

---

## Для пользователей ([user/](user/))

Инструкция для конечного пользователя. Пока заглушка — см. [user/README.md](user/README.md) и черновик [user-guide.md](user/user-guide.md).

---

## Как вести документацию

Документы по фичам следуют единому шаблону: **Назначение → Где в коде → Как работает →
Взаимодействия → Связанные документы**. Имена файлов и папок — `lowercase-kebab-case`,
содержимое — на русском. Ссылки на код относительные. Связи между документами помечаются `[[имя]]`.

- **Типы свойств** — сквозная сущность: реестр и разбор в
  [developer/architecture/data-types/](developer/architecture/data-types/), **новый тип = новый файл**
  `data-types/<тип>.md`; режимы (таблица/панель записи) лишь ссылаются, не дублируя.
- **Производительность** — общая картина в
  [performance/overview.md](developer/architecture/performance/overview.md); в фич-документ добавляйте
  подраздел `### Производительность` только при наличии конкретики, со ссылкой на центральный документ.
- Крупные документы дробим по подтемам в одноимённую папку (как `performance/`, `import-export/`),
  оставляя `overview.md` точкой входа.
