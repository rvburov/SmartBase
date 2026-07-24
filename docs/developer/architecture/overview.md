# Архитектура — обзор

## Назначение

Краткая карта приложения: из каких слоёв оно состоит, как устроен запуск и как
части связаны между собой.

## Технологии

- **Avalonia UI 11** (кросс-платформенный desktop, .NET 10), тема Fluent.
- **Entity Framework Core 9 + SQLite** — хранение.
- **CommunityToolkit.Mvvm** — `ObservableObject`/`RelayCommand` для ViewModel.
- **Markdown.Avalonia / Markdig** — описание записей. **ClosedXML** — импорт/экспорт Excel.

## Слои

| Слой | Папка | Что внутри |
|---|---|---|
| Core | [Core/](../../../Core/) | бизнес-модели и логика без UI и БД: `Models`, `Validators`, `Exporters`, `Importers` |
| Data | [Database/](../../../Database/) | EF Core: контексты, сущности, миграции, репозитории, мультибаза |
| ViewModels | [ViewModels/](../../../ViewModels/) | MVVM-логика |
| Views | [Views/](../../../Views/) | экраны (`.axaml` + code-behind) |
| Controls | [Controls/](../../../Controls/) | переиспользуемые компоненты, диалоги, виджеты |
| Utils | [Utils/](../../../Utils/) | пути/настройки (`AppSettings`), масштаб, автозапуск, markdown |

## Запуск (композиционный корень)

1. [Program.cs](../../../Program.cs) — `AppSettings.EnsureCreated()` (создаёт папку данных) и старт Avalonia.
2. [App.axaml.cs](../../../App.axaml.cs) `OnFrameworkInitializationCompleted`:
   - `DatabaseRegistry.Open(AppSettings.DbPath)` — открыть системную app.db и зарегистрировать дефолтную БД;
   - `AppSettings.AttachStore(registry)` — подключить хранилище настроек (app.db);
   - показать **сплэш-экран** ([SplashWindow](../../../Views/SplashWindow.axaml.cs), стиль установщика:
     название + версия, плавный прогресс) и уйти в фон (`Task.Run`):
     `Workspace.Open(registry, progress)` — смонтировать дефолтную БД (+ остальные из реестра),
     внутри прогоняются миграции и сидинг, сплэш показывает имя открываемой базы. Монтирование
     больших БД (разовая миграция/бэкафилл на миллионах строк) может занять десятки секунд —
     фон + сплэш не дают старту выглядеть зависшим;
   - по готовности — добег полосы до 100%, создать `MainWindow(workspace)`, закрыть сплэш;
     на выходе — `Dispose` рабочего пространства и реестра.

Все индикаторы загрузки в приложении (сплэш, окно импорта/массовых операций `ProgressDialog`,
окно загрузки обновления, установщик, окна расширений) используют один контрол —
[ProgressIndicator](../../../Controls/Components/ProgressIndicator.axaml.cs): фаза + доп. строка +
полоса с процентами; режимы «определённый», «бегущая полоса» и «плавный self-motion» (для работ
без известного объёма). Вид и поведение всех загрузок правятся в одном месте.

DI-контейнера нет — зависимости собираются вручную: `Workspace` → `DatabaseManager`
(контекст + репозитории) → модули UI получают нужный `ConstructorRepository`.

## MVVM и code-behind (важно)

Проект **гибридный**: есть MVVM-слой (`ViewModels/`), но крупные интерактивные
модули — календарь и таблица конструктора — реализованы преимущественно в
**code-behind** вью-контролов, которым контроллеры передают готовые данные через
`Refresh(...)`/`Load(...)`. Например, в окне работает code-behind
`CalendarControllerView`, а параллельный `CalendarControllerViewModel` дублирует
логику дат. При чтении кода ориентируйся на **вью-контролы и контроллеры-вью** как
на основную реализацию.

## Куда смотреть дальше

- Модель данных и таблицы — [data-model.md](data-model.md).
- Хранилище, миграции, пути — [databases.md](database/storage-and-migrations.md).
- Несколько БД одновременно — [multi-database.md](database/multi-database.md).
- Фичи — [../features/](../../features/).

## Связанные документы

[[data-model]], [[database/storage-and-migrations]], [[database/multi-database]], [[../features/calendar/overview]], [[../features/constructor/overview]]
