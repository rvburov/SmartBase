# Документация проекта

Универсальный конструктор баз данных, над которым надстроены календарь, задачи, библиотека
документов и другие разделы (Avalonia + .NET + EF Core/SQLite). Документация разделена на два
раздела:

- **[developer/](developer/)** — как устроено и работает приложение (для разработчиков) —
  основной раздел.
- **[user/](user/)** — инструкция для пользователя. Эти же файлы показывает сама
  программа: «Настройки → Инструкция пользователя».

Планы развития — [ROADMAP.md](ROADMAP.md); отдельной дорожной картой идут интеграции со сторонними
программами — [ROADMAP-API.md](ROADMAP-API.md).

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
  [file](developer/architecture/data-types/file.md) ·
  [user](developer/architecture/data-types/user.md) ·
  [computed](developer/architecture/data-types/computed.md) ·
  [роль «Описание»](developer/architecture/data-types/description.md).
- **База данных** ([database/](developer/architecture/database/)):
  [schema](developer/architecture/database/schema.md) (справочник всех таблиц/колонок) ·
  [storage-and-migrations](developer/architecture/database/storage-and-migrations.md) (два файла, миграции, пути, WAL) ·
  [multi-database](developer/architecture/database/multi-database.md) (несколько БД одновременно; чужие правки поллером и свои — шиной `ChangeBus`, по которой разделы обновляются в моменте) ·
  [network-shares](developer/architecture/database/network-shares.md) (общая база SQLite в сетевой папке: определение пути, режим DELETE и признак в файле, монтирование, потеря и возврат, зависшая папка, файлы рядом с базой, проверка на боевой машине).
- [users-permissions.md](developer/architecture/users-permissions.md) — пользователи, допуск, права, пароль, журнал.
- **Производительность** ([performance/](developer/architecture/performance/)):
  [overview](developer/architecture/performance/overview.md) (принципы, индексы, узкие места) ·
  [hot-paths](developer/architecture/performance/hot-paths.md) (таблица, фильтр, импорт, экспорт) ·
  [calendar](developer/architecture/performance/calendar.md) ·
  [changelog](developer/architecture/performance/changelog.md) ·
  [library-calibration](developer/architecture/performance/library-calibration.md) (калибровка чтения документов).

### Фичи ([developer/features/](developer/features/))

- **Календарь** ([calendar/](developer/features/calendar/)): [overview](developer/features/calendar/overview.md) · [month](developer/features/calendar/month.md) · [week](developer/features/calendar/week.md) · [day](developer/features/calendar/day.md) · [year](developer/features/calendar/year.md) · [schedule](developer/features/calendar/schedule.md) · [journal](developer/features/calendar/journal.md) · [interactions](developer/features/calendar/interactions.md) · [diagram](developer/features/calendar/diagram.md).
- **Конструктор** ([constructor/](developer/features/constructor/)): [overview](developer/features/constructor/overview.md) · [table](developer/features/constructor/table.md) · [record-page](developer/features/constructor/record-page.md).
- **Импорт/экспорт** ([import-export/](developer/features/import-export/)): [overview](developer/features/import-export/overview.md) · [import](developer/features/import-export/import.md) · [export](developer/features/import-export/export.md) · [cross-database-move](developer/features/import-export/cross-database-move.md).
- [tasks.md](developer/features/tasks.md) — раздел «Задачи».
- [notes.md](developer/features/notes.md) — раздел «Заметки»: папка с Markdown на диске, дерево файлов, редактор и просмотр.
- [library.md](developer/features/library.md) — раздел «Библиотека документов»: лента файлов, фильтр по типу, поиск внутри документов (кеш FTS5), миниатюры PDF.
- **Расширения** ([extensions/](developer/features/extensions/)): [overview](developer/features/extensions/overview.md) (механизм: контракт, реестр, настройки, видимость, файлы данных) — дальше **файл на расширение**, имя файла равно его `Id`: [pdf-rename](developer/features/extensions/pdf-rename.md) · [product-compare](developer/features/extensions/product-compare.md) · [assembly-calc](developer/features/extensions/assembly-calc.md) · [pdf-analyzer](developer/features/extensions/pdf-analyzer.md) · [docs-collection](developer/features/extensions/docs-collection.md) · [time-collector](developer/features/extensions/time-collector.md) · [doc-registry](developer/features/extensions/doc-registry.md) · [pdf-watermark](developer/features/extensions/pdf-watermark.md).
- **Интеграции** ([integrations/](developer/features/integrations/)): [overview](developer/features/integrations/overview.md) — обмен со сторонними программами: единственная дверь наружу, политика организации и проверка адресов · [inbox](developer/features/integrations/inbox.md) — протокол папки-приемника, контракт для отправителя. План целиком — [ROADMAP-API.md](ROADMAP-API.md).
- [categories.md](developer/features/categories.md) · [settings.md](developer/features/settings.md) · [backup.md](developer/features/backup.md) · [user-guide-section.md](developer/features/user-guide-section.md) · [notifications.md](developer/features/notifications.md) · [autostart-background.md](developer/features/autostart-background.md) · [profile.md](developer/features/profile.md) · [logging.md](developer/features/logging.md) · [icons.md](developer/features/icons.md).

### UI ([developer/ui/](developer/ui/))

- [widgets.md](developer/ui/widgets.md) · [dialogs.md](developer/ui/dialogs.md) · [theming.md](developer/ui/theming.md) · [scrolling.md](developer/ui/scrolling.md) · [markdown.md](developer/ui/markdown.md).

### Гайды ([developer/guides/](developer/guides/))

- [build-and-run.md](developer/guides/build-and-run.md) — сборка и запуск: dev-режим, релиз по ОС, версия/дата.
- [developer-guide.md](developer/guides/developer-guide.md) — миграции, соглашения, раскладка вывода.
- [AGENTS.md](../AGENTS.md) — указания ИИ-агентам: правила проекта, параллельная работа в рабочих
  папках (git worktree, `Scripts/worktree.sh`) и облачных копиях, отправка в `main` и PR.
- [postgresql-setup.md](developer/guides/postgresql-setup.md) — PostgreSQL: сервер, база, подключение.
- [auto-update.md](developer/guides/auto-update.md) — автообновление (сетевая папка + установщик).
- [ci.md](developer/guides/ci.md) — что и где гоняет CI: матрица ОС, стенд PostgreSQL, guard-проверки.
- [agent-tests.md](developer/guides/agent-tests.md) — проверка веток ИИ-агентов и PR на GitHub: Unit и расширения на Linux.
- [release.md](developer/guides/release.md) — сборка релиза: артефакты по ОС, подпись, публикация.

### Тестирование ([developer/testing/](developer/testing/))

- [overview.md](developer/testing/overview.md) — точка входа: автотесты (Unit/Integration), бенчмарки, многопроцессные проверки, генератор стенда. Команды запуска — также в корневом [README.md](../README.md#тесты).
- [extension-tests.md](developer/testing/extension-tests.md) — тесты расширений: отдельное дерево `Tests/Extensions/*`, по проекту на расширение, изолированы от тестов ядра.

### QA-чеклисты ([developer/qa-checklists/](developer/qa-checklists/))

Ручные прогоны фич (что нажать → что должно получиться): [overview.md](developer/qa-checklists/overview.md) · [database-file](developer/qa-checklists/database-file.md) · [backup](developer/qa-checklists/backup.md) · [calendar-diagram](developer/qa-checklists/calendar-diagram.md) · [calendar-settings](developer/qa-checklists/calendar-settings.md) · [import](developer/qa-checklists/import.md) · [export](developer/qa-checklists/export.md) · [profile](developer/qa-checklists/profile.md) · [postgresql](developer/qa-checklists/postgresql.md) · [tasks](developer/qa-checklists/tasks.md) · [cross-section-sync](developer/qa-checklists/cross-section-sync.md) · [relation](developer/qa-checklists/relation.md) · [table-columns](developer/qa-checklists/table-columns.md) · [table-navigation](developer/qa-checklists/table-navigation.md) · [filter-date-tree](developer/qa-checklists/filter-date-tree.md) · [diagnostics](developer/qa-checklists/diagnostics.md) · [shortcuts](developer/qa-checklists/shortcuts.md) · [notes](developer/qa-checklists/notes.md) · [library](developer/qa-checklists/library.md).

---

## Для пользователей ([user/](user/))

Инструкция для конечного пользователя; точка входа — [user/README.md](user/README.md).
Порядок документов в программе задает [GuideRegistry](../Settings/GuideRegistry.cs),
устройство раздела — [features/user-guide-section.md](developer/features/user-guide-section.md).

- Начало: [overview](user/overview.md) · [data-structure](user/data-structure.md)
- Работа с данными: [table](user/table.md) · [record-page](user/record-page.md) · [properties](user/properties.md) · [filters-sorting](user/filters-sorting.md)
- Планирование: [calendar](user/calendar.md) · [tasks](user/tasks.md) · [notes](user/notes.md) · [library](user/library.md)
- Обмен и инструменты: [import-export](user/import-export.md) · [extensions](user/extensions.md)
- Настройка: [settings](user/settings.md) · [profile-access](user/profile-access.md) · [postgresql](user/postgresql.md) · [postgresql-server](user/postgresql-server.md) · [install-update](user/install-update.md)
- Справочное: [shortcuts](user/shortcuts.md)

> **Файлы попадают в сборку.** `docs/user/**/*.md` объявлены `AvaloniaResource`
> в [SmartBase.csproj](../SmartBase.csproj) — правка markdown меняет и то, что видит
> пользователь в программе. Новый документ = файл здесь + строка в `GuideRegistry`.

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
- **Расширения** — то же правило, что у типов свойств: механизм в
  [extensions/overview.md](developer/features/extensions/overview.md), **новое расширение = новый файл**
  `extensions/<Id>.md` (имя файла равно `Id` расширения). Пользовательский текст про конкретное
  расширение сюда не попадает вовсе: он живет в `MarkdownDescription` самого расширения, а
  [user/extensions.md](user/extensions.md) описывает только общий механизм и ни одного расширения
  по имени не называет.
- Крупные документы дробим по подтемам в одноименную папку (как `performance/`, `import-export/`),
  оставляя `overview.md` точкой входа.
