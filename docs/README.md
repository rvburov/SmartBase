# Документация проекта

Календарь-Планировщик на базе универсального конструктора данных (Avalonia + .NET +
EF Core/SQLite). Здесь описано, **как устроено и как работает** приложение.

## Навигация

### Гайды ([guides/](guides/))
- [user-guide.md](guides/user-guide.md) — руководство пользователя.
- [developer-guide.md](guides/developer-guide.md) — сборка, запуск, миграции, соглашения.

### Архитектура ([architecture/](architecture/))
- [overview.md](architecture/overview.md) — слои, запуск, MVVM/code-behind.
- [data-model.md](architecture/data-model.md) — модели и таблицы.
- [databases.md](architecture/databases.md) — два SQLite-файла, миграции, пути, WAL.
- [multi-database.md](architecture/multi-database.md) — несколько БД одновременно.

### Фичи ([features/](features/))
- **Календарь** ([features/calendar/](features/calendar/)): [overview](features/calendar/overview.md) · [month](features/calendar/month.md) · [week](features/calendar/week.md) · [day](features/calendar/day.md) · [year](features/calendar/year.md) · [schedule](features/calendar/schedule.md) · [journal](features/calendar/journal.md) · [interactions](features/calendar/interactions.md)
- **Конструктор БД** ([features/constructor-db/](features/constructor-db/)): [overview](features/constructor-db/overview.md) · [tables](features/constructor-db/tables.md) · [record-page](features/constructor-db/record-page.md)
- [categories.md](features/categories.md) — категории и цвета.
- [import-export.md](features/import-export.md) — импорт из Excel, перенос между БД.
- [settings.md](features/settings.md) — настройки и управление БД.
- [icons.md](features/icons.md) — иконка приложения на всех ОС.

### UI ([ui/](ui/))
- [widgets.md](ui/widgets.md) — виджеты и компоненты.
- [dialogs.md](ui/dialogs.md) — модальные диалоги.
- [theming.md](ui/theming.md) — цвета, типографика, масштаб.

### Планы
- [ROADMAP.md](ROADMAP.md) — высокоуровневые этапы.
- [plans/](plans/) — черновики и задумки по фичам.

## Как вести документацию

Документы по фичам следуют единому шаблону: **Назначение → Где в коде → Как
работает → Взаимодействия → Связанные документы**. Имена файлов —
`lowercase-kebab-case`, содержимое — на русском. Ссылки на код относительные
(например, из `docs/features/calendar/` к корню — `../../../`). Связи между
документами помечаются `[[имя]]`.
