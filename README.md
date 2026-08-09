# SmartBase — календарь-планировщик на конструкторе данных

Кросс-платформенное desktop-приложение (Windows / macOS / Linux): **универсальный
конструктор данных** — коллекции с произвольными полями, как в Notion/Airtable — и
построенные поверх него **календарь**, **задачи** и **расширения**. Сервер и учётные
записи не нужны: данные лежат в файлах SQLite (в том числе общих в сетевой папке),
а когда пишущих становится много — в PostgreSQL.

![Версия](https://img.shields.io/badge/version-1.0.4-blue) ![.NET](https://img.shields.io/badge/.NET-10.0+-512BD4?logo=dotnet&logoColor=white) ![C#](https://img.shields.io/badge/C%23-14-239120?logo=csharp&logoColor=white) ![Avalonia](https://img.shields.io/badge/Avalonia_UI-11.2-8B44AC) ![EF Core](https://img.shields.io/badge/EF_Core-9.0-512BD4) ![SQLite](https://img.shields.io/badge/SQLite-003B57?logo=sqlite&logoColor=white) ![PostgreSQL](https://img.shields.io/badge/PostgreSQL-14+-4169E1?logo=postgresql&logoColor=white) ![Windows](https://img.shields.io/badge/Windows-0078D4?logo=data:image/svg%2Bxml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCAyNCAyNCI%2BPHBhdGggZmlsbD0id2hpdGUiIGQ9Ik0wIDMuNDQ5TDkuNzUgMi4xdjkuNDUxSDBtMTAuOTQ5LTkuNkwyNCAwdjExLjRIMTAuOTQ5TTAgMTIuNmg5Ljc1djkuNDUxTDAgMjAuNjk5TTEwLjk0OSAxMi42SDI0VjI0bC0xMi45LTEuODAxIi8%2BPC9zdmc%2B) ![macOS](https://img.shields.io/badge/macOS-000000?logo=apple&logoColor=white) ![Linux](https://img.shields.io/badge/Linux-FCC624?logo=linux&logoColor=black)

---

## Что это

Основа — **конструктор**: пользователь создаёт **коллекции** (таблицы) с произвольными
**полями** и наполняет их **записями**. Всё остальное — представления тех же записей:
запись с полем-датой видна в **календаре**, запись с полем-исполнителем — в **задачах**.
Отдельных сущностей «событие» и «задача» в базе нет — это проекции записи.

Разделы переключаются иконками слева (Activity bar):

| Раздел | Что делает |
|---|---|
| **Конструктор БД** | коллекции, поля, записи: таблица или карточки + панель записи |
| **Календарь** | 7 режимов: Расписание, Журнал, День, Неделя, Месяц, Год, Диаграмма |
| **Задачи** | карточки задач: исполнитель, назначил, статус, срок, остаток времени |
| **Расширения** | подключаемые плагины, каждый в своём окне (PDF, составы изделий, документы) |
| **Профиль** | свои данные, допуск пользователей к базам, история изменений |
| **Настройки** | общее, оформление, управление базами, диагностика, о программе |

## Возможности

**Данные**

- **Типы полей** — текст, число, дата, список, статус, мультисписок, чекбокс, ссылка,
  файл, пользователь, связь (ссылки между записями) и вычисляемые (кто и когда создал/изменил).
- **Мультибаза** — несколько баз одновременно, локальных и в сетевых папках; календарь и
  задачи агрегируют записи из всех, коллекции переносятся между базами.
- **SQLite и PostgreSQL** — работают на равных и одновременно, в одном сайдбаре.
- **Импорт/экспорт** — Excel/CSV/JSON → коллекции и обратно (`.xlsx`/`.xlsm`/`.csv`/`.json`),
  потоково и пакетно (все листы книги, папка файлов, несколько коллекций сразу).
- **Миллионы записей** — виртуализированная таблица и keyset-пагинация вместо выборки целиком.

**Календарь и задачи**

- **Drag & Drop** событий по времени и дням, **resize** длительности мышью.
- **События «весь день»**, в том числе многодневные; поиск по названию на лету.
- **Категории** — именованные цветные метки, задают акцент событий и записей.
- **Уведомления** — нативный баннер ОС в момент события, клик открывает запись; на иконке
  приложения значок с числом непросмотренных.

**Совместная работа (без сервера)**

- Пользователь определяется автоматически по `учётка@компьютер` — входа и паролей нет.
- **Допуск к базе** даёт администратор или пароль самой базы; права и список пользователей
  хранятся внутри файла базы и путешествуют вместе с ним.
- **История изменений** и живая синхронизация: правки соседа по общей папке подхватываются
  без перезапуска.

**Приложение**

- Автозапуск при входе в систему, фоновый режим со значком в трее.
- Автообновление из сетевой папки; та же раздача работает установщиком и деинсталлятором.
- Темы оформления, зум всего интерфейса, DPI-масштабирование (PerMonitorV2).
- Диагностический лог в отдельный `logs.db` — «Настройки → Диагностика».

## Технологии

| Что | Чем |
|---|---|
| UI | **Avalonia UI 11.2** (тема Fluent, DataGrid), .NET 10, собственные токены стилей |
| MVVM | **CommunityToolkit.Mvvm**; крупные интерактивные модули — намеренно code-behind |
| Данные | **EF Core 9** + **SQLite**, **Npgsql** для PostgreSQL, миграции EF |
| Таблицы | **ClosedXML** и **DocumentFormat.OpenXml** (потоковая SAX-запись), **ExcelDataReader** |
| Текст | **Markdig** + **Markdown.Avalonia** — описания записей |
| Уведомления | **DesktopNotifications** (Windows/Linux), macOS — свой код |
| Логи | **Serilog** + приёмник SQLite (`logs.db`) |
| Расширения | **PdfPig** — разбор PDF |

## Как устроено

Слои: `Core/` (модели, валидаторы, импорт/экспорт — без UI и БД) → `Database/` (EF Core:
контексты, репозитории, мультибаза) → `ViewModels/` + `Views/` + `Controls/`.
DI-контейнера нет — зависимости собираются вручную.

Запуск: [Program.cs](Program.cs) (логгер, пути, Avalonia) → [App.axaml.cs](App.axaml.cs)
открывает системную `app.db` и показывает сплэш, пока в фоне монтируется база данных
(миграции, сидинг) → главное окно. Остальные базы реестра доезжают фоновой очередью,
поэтому старт не зависит от их количества и размера.

Подробнее — [docs/developer/architecture/overview.md](docs/developer/architecture/overview.md);
вся документация — [docs/README.md](docs/README.md).

---

## Быстрый старт

### Что нужно установить

Требуется **.NET SDK 10.0+**. Установить можно любым способом:

#### Windows

**winget** (встроен в Windows 10/11):

```powershell
winget install Microsoft.DotNet.SDK.10
```

**Chocolatey:**

```powershell
choco install dotnet-sdk
```

**Scoop:**

```powershell
scoop install dotnet-sdk
```

#### Linux

**apt** (Ubuntu / Debian):

```bash
sudo apt-get install -y dotnet-sdk-10.0
```

**dnf** (Fedora):

```bash
sudo dnf install dotnet-sdk-10.0
```

**snap:**

```bash
sudo snap install dotnet-sdk --classic --channel=10.0
```

#### macOS

**Homebrew:**

```bash
brew install --cask dotnet-sdk
```

**MacPorts:**

```bash
sudo port install dotnet-sdk
```

---

**Сайт (все платформы):** https://dotnet.microsoft.com/download/dotnet/10.0

> Если вы просто хотите запустить готовый `.exe` — ничего устанавливать не нужно,
> файл не требует рантайма (self-contained).

---

### Запуск из исходников (для разработки)

Сначала клонируем репозиторий:

```bash
git clone https://github.com/rvburov/SmartBase.git
```

Проект многотаргетный, поэтому `dotnet run` требует явно указать таргет флагом `-f`
(без него — ошибка «Проект предназначен для нескольких платформ»):

```bash
cd SmartBase
dotnet run -f net10.0-windows10.0.17763   # Windows
dotnet run -f net10.0                     # macOS, Linux
```

> ⚠️ **На Windows запускайте только windows-таргет.** Под `net10.0` приложение
> собирается и работает, но нативных уведомлений в нём нет — молча, без ошибки.
> Зачем нужны два таргета —
> [build-and-run.md](docs/developer/guides/build-and-run.md#два-таргета-и-почему-их-два).

`dotnet run` каждый раз проверяет изменения и при необходимости пересобирает проект
(первая сборка в сессии — «холодная» и долгая). Чтобы **не пересобирать при повторных
запусках**, когда код не менялся:

```bash
dotnet run -f net10.0 --no-build      # запустить уже собранное, без пересборки
```

Ещё быстрее — запускать собранный бинарь напрямую, вообще без проверки сборки. Папка
вывода содержит имя таргета (`debug_<TFM>`), а расширение зависит от ОС:

```bash
artifacts/bin/SmartBase/debug_net10.0-windows10.0.17763/SmartBase.exe   # Windows
artifacts/bin/SmartBase/debug_net10.0/SmartBase                         # macOS, Linux
```

> ⚠️ `--no-build` и прямой запуск бинаря стартуют **последнюю собранную** версию.
> Если ты менял код — сначала собери (`dotnet run` либо `dotnet build`), иначе
> запустится старый бинарь.

**Правило:** менял код → `dotnet run -f …` (нужна сборка); код не менялся, просто
перезапустить → `--no-build` или прямой бинарь. Для активной разработки
с авто-пересборкой и перезапуском при каждом сохранении — `dotnet watch run -f net10.0`
(на Windows — с `-f net10.0-windows10.0.17763`).

Полное описание режимов запуска — [docs/developer/guides/build-and-run.md](docs/developer/guides/build-and-run.md).

### Тесты

```bash
dotnet test Tests/Unit          # быстрый гейт (мс), без внешних зависимостей
dotnet test Tests/Integration   # репозитории на SQLite + PostgreSQL (если поднят стенд sb-pg)
dotnet test SmartBase.sln       # оба набора
./Tests/run-all.sh              # оба набора (+ --with-bench для быстрых бенчмарков)
```

Инструменты (консольные, не автотесты):

```bash
dotnet run -c Release --project Tests/Benchmarks  -- <команда>   # перф-замеры
dotnet run -c Release --project Tests/DbGenerator                # генератор большого стенда
dotnet run -c Release --project Tests/MultiProcess -- <режим>    # многопроцессные проверки
```

Обзор и принципы разделения тестов — [docs/developer/testing/overview.md](docs/developer/testing/overview.md).

### Сборка готового бинарника для раздачи

Под каждую ОС — **один короткий скрипт** в `Scripts/` (имя = `<ОС>-build`).
Он прячет длинную команду `dotnet publish … --self-contained … -p:PublishSingleFile=true …`
и все особенности сборки этой платформы. Готовые файлы раскладываются по версиям:
**`dist/Version <версия>/<rid>/`** — та же версия перезаписывает свою папку, новая
версия создаёт отдельную. Это и есть то, что раздаётся пользователям. Установка .NET
им не нужна (self-contained), размер ~100–150 МБ.

Аргументы скриптов: `Scripts/<ОС>-build.sh [RID] [Config] [Version]`. Номер версии —
**третий аргумент** (напр. `Scripts/windows-build.sh win-x64 Release 1.2.3`): он
**записывается в `<Version>`** в [SmartBase.csproj](SmartBase.csproj) и становится текущим
для дальнейших сборок. Без аргумента берётся то, что уже в csproj. Версия и дата
сборки видны в приложении: **Настройки → О программе**. Полное описание —
[docs/developer/guides/build-and-run.md](docs/developer/guides/build-and-run.md).

#### Windows (x64)

```bash
cd SmartBase
Scripts/windows-build.sh                        # win-x64, версия из csproj
Scripts/windows-build.sh win-x64 Release 1.2.3  # с номером версии 1.2.3
```

> Скрипты — bash, на Windows запускаются **из Git Bash**. Если появляется «Нет
> установленных дистрибутивов подсистемы Windows для Linux», `.sh` ушёл в WSL-овый
> `bash.exe` — запустите bash из Git по абсолютному пути:
> `/usr/bin/bash Scripts/windows-build.sh` ([подробнее](docs/developer/guides/build-and-run.md#сборка-для-раздачи)).

Результат: `dist/Version <версия>/win-x64/SmartBase.exe` — иконка уже вшита в `.exe`.
Рядом кладётся `SmartBase-Setup.exe` (тот же бинарь в режиме установщика: установка в
`%LOCALAPPDATA%`, ярлыки, обновление, удаление) — см. [docs/developer/guides/auto-update.md](docs/developer/guides/auto-update.md).

#### Linux (x64)

```bash
cd SmartBase
Scripts/linux-build.sh                          # linux-x64, версия из csproj
Scripts/linux-build.sh linux-x64 Release 1.2.3  # с номером версии 1.2.3
```

Результат: `dist/Version <версия>/linux-x64/SmartBase` (бит запуска уже выставлен). Чтобы
в меню приложений появился пункт с иконкой, на целевой машине выполните `Scripts/linux-install.sh`.

#### macOS (Apple Silicon / Intel)

Скрипт сам публикует бинарь **и** упаковывает его в `.app` с иконкой (на маке без бандла иконки в Dock не будет):

```bash
cd SmartBase

# версия из csproj:
Scripts/macos-build.sh                          # Apple Silicon (osx-arm64)
Scripts/macos-build.sh osx-x64                  # Intel        (osx-x64)

# с номером версии 1.2.3 (одна и та же версия для обоих таргетов):
Scripts/macos-build.sh osx-arm64 Release 1.2.3  # Apple Silicon
Scripts/macos-build.sh osx-x64   Release 1.2.3  # Intel
```

Результат: `dist/Version <версия>/osx-arm64/SmartBase.app` (или `…/osx-x64/…` для Intel) —
готовое приложение с иконкой (`open "dist/Version <версия>/osx-arm64/SmartBase.app"`).
Подробности — [docs/developer/features/icons.md](docs/developer/features/icons.md).

> Все платформы собираются с одной машины (cross-compilation). Папка `dist/`
> в `.gitignore` — это артефакты раздачи, пересоздаются.

---

## Структура проекта

```
SmartBase/
├── Program.cs                  # Точка входа
├── App.axaml / App.axaml.cs    # Application definition
├── app.manifest                # Манифест (DPI-awareness для Windows)
│
├── Core/                       # Бизнес-логика (без UI и БД)
│   ├── Models/                 # Constructor, CalendarRecord, TaskRecord, FieldTypeInfo,
│   │                           #   Permissions, RelationConfig, Category, EventColors
│   ├── Validators/             # CalendarRecordValidator
│   ├── Importers/              # Импорт таблиц (Excel/CSV/JSON): типы, ImportPlanner,
│   │   └── TableSources/       #   потоковые источники форматов (двухпроходные)
│   └── Exporters/              # Экспорт коллекций (.xlsx/.xlsm/.csv/.json): ExportPlanner,
│       └── TableWriters/       #   потоковые писатели (OpenXml SAX / StreamWriter / Utf8Json)
│
├── Database/                   # Слой данных (EF Core: SQLite + PostgreSQL)
│   ├── AppDbContext.cs         # Контекст пользовательской БД (database.db)
│   ├── PgAppDbContext.cs       # Тот же домен на PostgreSQL (+ свои миграции)
│   ├── SystemDbContext.cs      # Контекст системной app.db (реестр БД + настройки)
│   ├── DatabaseManager.cs      # Открытие и миграции одной БД
│   ├── DatabaseRegistry.cs     # Реестр БД + хранилище настроек поверх app.db
│   ├── Workspace.cs            # Набор смонтированных БД (мультибаза)
│   ├── MountQueue.cs           # Фоновая очередь монтирования (по одной, живые статусы)
│   ├── ChangePoller.cs         # Опрос журнала: правки соседей без перезапуска
│   ├── EntityRef.cs            # Изоляция id (DbId, Id) между БД
│   ├── Entities/               # Core, Ui, Profile, DatabaseRef, AppSetting
│   ├── Migrations/             # Миграции EF Core (SQLite и Pg) + снапшоты модели
│   └── Repositories/           # ConstructorRepository (+ Tasks, Relations, CrossMove),
│                               #   CategoryRepository, PermissionService (права и журнал)
│
├── ViewModels/                 # MVVM логика
│   ├── ViewModelBase.cs
│   ├── ConstructorDb/          # Таблица, строки, страница записи, виртуализация списка
│   ├── Dialogs/                # VM диалогов (Collection, Group, Record)
│   └── Settings/               # SettingsViewModel
│
├── Views/                      # Экраны приложения (.axaml)
│   ├── MainWindow.axaml        # Главное окно (оболочка всех разделов)
│   ├── SplashWindow.axaml      # Сплэш: монтирование БД на старте
│   ├── ConstructorDb/          # Конструктор БД (таблица, карточки, страница записи)
│   │   └── Calendar/           # Расписание, Журнал, День, Неделя, Месяц, Год, Диаграмма
│   ├── Tasks/                  # Раздел «Задачи» (контроллер + список карточек)
│   ├── Profile/                # Профиль: общие данные, админ-панель, история изменений
│   ├── Extensions/             # Раздел «Расширения» (список + запуск)
│   └── Settings/               # SettingsView
│
├── Controls/                   # Переиспользуемые UI компоненты
│   ├── Behaviors/              # Поведения на attached-свойствах (навешиваются на чужие контролы, сами ничего не рисуют)
│   │   ├── LineMetrics.cs      # Единая толщина линий при любом зуме и DPI
│   │   ├── WindowZoom.cs       # Зум приложения для отдельных окон (диалоги, расширения)
│   │   └── ScrollBarLink.cs    # Полоса прокрутки, вынесенная из ScrollViewer наружу (начинается на уровне шапки)
│   ├── Components/             # Navbar, Sidebar, ActivityBar, AllDayBar, Footer, ContextMenu
│   ├── Dialogs/                # Record, Collection, Group, Confirm, Property*, CreateItem
│   └── Widgets/                # AppButton, AppCheckBox, AppLabel, Selector, SearchInput, DateEditor, TimeField, ErrorBanner
│
├── Notifications/              # Напоминания о событиях
│   ├── NotificationScheduler.cs # Ядро расписания (без БД и платформы, тестируемое)
│   ├── NotificationService.cs  # Таймер, сбор событий, значок на иконке
│   └── Platforms/              # Бэкенды ОС: macOS — свой код, Windows/Linux — библиотека
│
├── Extensions/                 # Плагины: IExtension + реестр, каждый в своей папке
│   ├── PdfRename/              #   Переименование PDF
│   ├── PdfAnalyzer/            #   Анализ PDF-файлов
│   ├── ProductCompare/         #   Сравнение составов изделий
│   ├── AssemblyCalc/           #   Расчёт сборочных единиц
│   └── DocsCollection/         #   Документы в коллекцию
│
├── Settings/                   # Реестры разделов: SettingsRegistry, ProfileRegistry
├── Logging/                    # Serilog → logs.db (AppLog) + чтение для «Диагностики»
├── Setup/                      # Установщик/деинсталлятор и самозамена при обновлении
│
├── Utils/                      # Вспомогательные утилиты (без привязки к контролам)
│   ├── AppSettings.cs          # Пути к БД (.localdata / %AppData%) + доступ к настройкам
│   ├── CurrentUser.cs          # Идентичность «учётка@компьютер» + ФИО из профиля
│   ├── Autostart.cs            # Автозапуск при входе в систему
│   ├── BackgroundBehavior.cs   # Фоновый режим и трей
│   ├── SingleInstance.cs       # Один экземпляр приложения
│   ├── Scaling.cs / FontScale.cs # DPI-масштабирование и зум интерфейса
│   └── MarkdownUtils.cs        # Работа с Markdown
│
├── Assets/                     # Ресурсы
│   ├── Branding/               # Иконка приложения (icon.png/.ico/.icns, Info.plist, .desktop)
│   └── Styles/                 # Colors, Typography, Icons, темы контролов
│
├── Scripts/                    # windows-build.sh, linux-build.sh, macos-build.sh, linux-install.sh, generate-icons.sh
├── Tests/                      # Unit, Integration (xUnit) + Benchmarks, DbGenerator, MultiProcess
├── docs/                       # developer/ (architecture, features, ui, guides, testing, qa-checklists), user/, ROADMAP (см. docs/README.md)
│
│   # ── Файлы в корне проекта ──
├── SmartBase.csproj             # Проект
├── SmartBase.sln                # Solution
├── Directory.Build.props       # Вывод сборки → artifacts\ (а не bin\/obj\)
├── nuget.config                # Кэш NuGet-пакетов → packages\ внутри проекта
├── dotnet-tools.json           # Локальные dotnet-инструменты (например, EF CLI)
├── .gitignore                  # Игнор: artifacts, packages, dist, obj, *.db, .localdata…
├── .gitattributes              # Нормализация переносов строк (LF/CRLF)
├── README.md                   # Этот файл
│
│   # ── Служебные папки (в .gitignore, пересоздаются автоматически) ──
├── .localdata/                 # ⚙ Dev-данные (Debug): database.db, app.db, logs.db + instance.lock, show.signal, crash.log
├── artifacts/                  # ⚙ Вся сборка: bin / obj / publish
├── packages/                   # ⚙ Кэш NuGet-пакетов
└── obj/                        # ⚙ Промежуточные файлы restore
```

> **Служебные папки `artifacts/`, `packages/`, `obj/`** держат временные файлы сборки
> и скачанные пакеты внутри проекта (аналог `venv` в Python) — системный диск не
> засоряется. Все в `.gitignore` и пересоздаются автоматически при `dotnet build`.
> Очистить только сборку: удалить `artifacts\` и `packages\`.
>
> Папка **`.localdata/`** тоже в `.gitignore`, но это **dev-данные** (БД при Debug) —
> её удаление сотрёт локальную базу разработки, поэтому при «очистке проекта» её не трогают.

---

## База данных

Приложение держит три SQLite-файла (пути задаются в [Utils/AppSettings.cs](Utils/AppSettings.cs)):

- **`database.db`** — пользовательские данные (коллекции, поля, записи, события календаря);
  это база «по умолчанию», к ней добавляются любые другие — см. мультибазу ниже.
- **`app.db`** — системная: реестр подключённых БД + настройки приложения.
- **`logs.db`** — диагностический лог (Serilog), читается в «Настройки → Диагностика».

**В продакшене (Release)** файлы лежат в стандартной папке данных пользователя ОС — `…/SmartBase/`:

- **Windows** (`%APPDATA%`, Roaming):
  - `C:\Users\<Имя>\AppData\Roaming\SmartBase\database.db`
  - `C:\Users\<Имя>\AppData\Roaming\SmartBase\app.db`
  - `C:\Users\<Имя>\AppData\Roaming\SmartBase\logs.db`
- **Linux** (`~/.config`):
  - `/home/<имя>/.config/SmartBase/database.db`
  - `/home/<имя>/.config/SmartBase/app.db`
  - `/home/<имя>/.config/SmartBase/logs.db`
- **macOS** (`~/.config` — в .NET `ApplicationData` указывает на `~/.config`, а не на `~/Library/Application Support`):
  - `/Users/<имя>/.config/SmartBase/database.db`
  - `/Users/<имя>/.config/SmartBase/app.db`
  - `/Users/<имя>/.config/SmartBase/logs.db`

Во время работы рядом появляются временные файлы SQLite: `*.db-wal`, `*.db-shm`.

### Служебные файлы в папке данных

Кроме трёх баз там же лежат три служебных файла. Они **остаются на диске после выхода
из приложения** — это норма, а не мусор:

| Файл | Кто создаёт | Когда появляется |
|---|---|---|
| `instance.lock` | [Utils/SingleInstance.cs](Utils/SingleInstance.cs) | при каждом запуске; пустой (0 байт) — защита от второго экземпляра держится на эксклюзивном дескрипторе `FileShare.None`, а не на наличии файла |
| `show.signal` | [Utils/SingleInstance.cs](Utils/SingleInstance.cs) | только при запуске второй копии поверх работающей: второй процесс пишет метку времени и выходит, работающий показывает окно |
| `crash.log` | [Program.cs](Program.cs) | только при необработанном исключении; синхронный резерв к `logs.db`, время в нём локальное (в `logs.db` — UTC) |

Подробнее — [storage-and-migrations.md](docs/developer/architecture/database/storage-and-migrations.md).

**При разработке (Debug)** — папка `.localdata/` в корне проекта (находится по маркеру
`SmartBase.csproj`, поэтому не зависит от раскладки вывода): `.localdata/database.db`,
`.localdata/app.db`, `.localdata/logs.db` и те же служебные файлы (вся папка в `.gitignore`).

### Мультибаза и PostgreSQL

Дополнительные базы подключаются в «Настройки → Управление базой данных», их пути
хранятся в `app.db`. Это либо **`.db`-файлы** (в том числе общие в сетевой папке или
облаке), либо **базы PostgreSQL** — оба вида работают одновременно и на равных: общий
сайдбар, общий календарь, перенос коллекций между ними в любую сторону. Схема
накатывается миграциями при открытии каждой базы, id уникальны внутри файла и
адресуются парой `(DbId, Id)`.

- Как устроено — [multi-database.md](docs/developer/architecture/database/multi-database.md)
  и [storage-and-migrations.md](docs/developer/architecture/database/storage-and-migrations.md).
- Поднять сервер PG и подключить базу — [postgresql-setup.md](docs/developer/guides/postgresql-setup.md).

---

## Документация

Полная карта — [docs/README.md](docs/README.md). Точки входа:

| Раздел | Про что |
|---|---|
| [architecture/overview.md](docs/developer/architecture/overview.md) | слои, запуск, MVVM и code-behind |
| [architecture/data-model.md](docs/developer/architecture/data-model.md) | доменные модели и таблицы |
| [architecture/data-types/](docs/developer/architecture/data-types/) | типы полей — файл на тип |
| [architecture/users-permissions.md](docs/developer/architecture/users-permissions.md) | пользователи, допуск, права, журнал |
| [features/](docs/developer/features/) | календарь, конструктор, задачи, импорт/экспорт, уведомления, расширения |
| [guides/build-and-run.md](docs/developer/guides/build-and-run.md) | сборка и запуск во всех режимах |
| [guides/developer-guide.md](docs/developer/guides/developer-guide.md) | миграции EF, соглашения, раскладка вывода |
| [testing/overview.md](docs/developer/testing/overview.md) | автотесты, бенчмарки, стенды |
| [ROADMAP.md](docs/ROADMAP.md) | планы развития |
