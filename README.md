# Календарь-Планировщик

Многофункциональное desktop-приложение на Avalonia UI с поддержкой категорий,
шести режимов просмотра и удобным управлением событиями.

![Версия](https://img.shields.io/badge/version-1.0.3-blue)
![.NET](https://img.shields.io/badge/.NET-10.0+-purple)
![Avalonia](https://img.shields.io/badge/Avalonia-11.x-blue)

---

## Возможности

- **6 режимов просмотра** — Расписание, Журнал, День, Неделя, Месяц, Год
- **Drag & Drop** — перетаскивание событий по времени и дням
- **Resize** — изменение длительности событий мышью
- **Категории** — создание, редактирование, удаление с цветовой маркировкой
- **All-day события** — однодневные и многодневные события «весь день»
- **Поиск** — фильтрация событий по названию в реальном времени
- **SQLite** — локальное хранение через Entity Framework Core
- **DPI масштабирование** — поддержка PerMonitorV2

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

```bash
cd calendar_avalonia
dotnet run                 # собрать при необходимости и запустить
```

`dotnet run` каждый раз проверяет изменения и при необходимости пересобирает проект
(первая сборка в сессии — «холодная» и долгая). Чтобы **не пересобирать при повторных
запусках**, когда код не менялся:

```bash
dotnet run --no-build      # запустить уже собранное, без пересборки
```

Ещё быстрее — запускать собранный `.exe` напрямую, вообще без проверки сборки:

```bash
artifacts/bin/DataBase/debug/DataBase.exe
```

> ⚠️ `--no-build` и прямой запуск `.exe` стартуют **последнюю собранную** версию.
> Если ты менял код — сначала собери (`dotnet run` либо `dotnet build`), иначе
> запустится старый бинарь.

**Правило:** менял код → `dotnet run` (нужна сборка); код не менялся, просто
перезапустить → `dotnet run --no-build` или прямой `.exe`. Для активной разработки
с авто-пересборкой и перезапуском при каждом сохранении — `dotnet watch run`.

### Сборка готового бинарника для раздачи

Под каждую ОС — **один короткий скрипт** в `Scripts/` (имя = `<ОС>-build`).
Он прячет длинную команду `dotnet publish … --self-contained … -p:PublishSingleFile=true …`
и все особенности сборки этой платформы. Готовые файлы раскладываются по версиям:
**`dist/Version <версия>/<rid>/`** — та же версия перезаписывает свою папку, новая
версия создаёт отдельную. Это и есть то, что раздаётся пользователям. Установка .NET
им не нужна (self-contained), размер ~100–150 МБ.

Аргументы скриптов: `Scripts/<ОС>-build.sh [RID] [Config] [Version]`. Номер версии —
**третий аргумент** (напр. `Scripts/windows-build.sh win-x64 Release 1.2.3`): он
**записывается в `<Version>`** в [DataBase.csproj](DataBase.csproj) и становится текущим
для дальнейших сборок. Без аргумента берётся то, что уже в csproj. Версия и дата
сборки видны в приложении: **Настройки → О программе**. Полное описание —
[docs/guides/build.md](docs/guides/build.md).

#### Windows (x64)

```bash
cd calendar_avalonia
Scripts/windows-build.sh                        # win-x64, версия из csproj
Scripts/windows-build.sh win-x64 Release 1.2.3  # с номером версии 1.2.3
```

Результат: `dist/Version <версия>/win-x64/DataBase.exe` — иконка уже вшита в `.exe`.
Рядом кладётся `DataBase-Setup.exe` (тот же бинарь в режиме установщика: установка в
`%LOCALAPPDATA%`, ярлыки, обновление, удаление) — см. [docs/guides/auto-update.md](docs/guides/auto-update.md).

#### Linux (x64)

```bash
cd calendar_avalonia
Scripts/linux-build.sh                          # linux-x64, версия из csproj
Scripts/linux-build.sh linux-x64 Release 1.2.3  # с номером версии 1.2.3
```

Результат: `dist/Version <версия>/linux-x64/DataBase` (бит запуска уже выставлен). Чтобы
в меню приложений появился пункт с иконкой, на целевой машине выполните `Scripts/linux-install.sh`.

#### macOS (Apple Silicon / Intel)

Скрипт сам публикует бинарь **и** упаковывает его в `.app` с иконкой (на маке без бандла иконки в Dock не будет):

```bash
cd calendar_avalonia

# версия из csproj:
Scripts/macos-build.sh                          # Apple Silicon (osx-arm64)
Scripts/macos-build.sh osx-x64                  # Intel        (osx-x64)

# с номером версии 1.2.3 (одна и та же версия для обоих таргетов):
Scripts/macos-build.sh osx-arm64 Release 1.2.3  # Apple Silicon
Scripts/macos-build.sh osx-x64   Release 1.2.3  # Intel
```

Результат: `dist/Version <версия>/osx-arm64/DataBase.app` (или `…/osx-x64/…` для Intel) —
готовое приложение с иконкой (`open "dist/Version <версия>/osx-arm64/DataBase.app"`).
Подробности — [docs/features/icons.md](docs/features/icons.md).

> Все платформы собираются с одной машины (cross-compilation). Папка `dist/`
> в `.gitignore` — это артефакты раздачи, пересоздаются.

---

## Структура проекта

```
calendar_avalonia/
├── Program.cs                  # Точка входа
├── App.axaml / App.axaml.cs    # Application definition
├── app.manifest                # Манифест (DPI-awareness для Windows)
│
├── Core/                       # Бизнес-логика (без UI и БД)
│   ├── Models/                 # CalendarRecord, Category, Task, Constructor, EventColors
│   ├── Validators/             # CalendarRecordValidator
│   ├── Importers/              # Импорт таблиц (Excel/CSV/JSON): типы, ImportPlanner,
│   │   └── TableSources/       #   потоковые источники форматов (двухпроходные)
│   └── Exporters/              # Экспорт коллекций (.xlsx/.xlsm/.csv/.json): ExportPlanner,
│       └── TableWriters/       #   потоковые писатели (OpenXml SAX / StreamWriter / Utf8Json)
│
├── Database/                   # Слой данных (EF Core + SQLite)
│   ├── AppDbContext.cs         # EF Core контекст пользовательской БД (database.db)
│   ├── AppDbContextFactory.cs  # Фабрика для design-time миграций
│   ├── SystemDbContext.cs      # Контекст системной app.db (реестр БД + настройки)
│   ├── DatabaseManager.cs      # Открытие и миграции одной БД
│   ├── DatabaseRegistry.cs     # Реестр БД + хранилище настроек поверх app.db
│   ├── Workspace.cs            # Набор смонтированных БД (мультибаза)
│   ├── EntityRef.cs            # Изоляция id (DbId, Id) между БД
│   ├── Entities/               # Entities.cs, DatabaseRefEntity, AppSettingEntity
│   ├── Migrations/             # Миграции EF Core + снапшот модели
│   └── Repositories/           # CategoryRepository, ConstructorRepository
│
├── ViewModels/                 # MVVM логика
│   ├── MainWindowViewModel.cs
│   ├── ViewModelBase.cs
│   ├── Calendar/               # Day, Week, Month, Year, List, Journal + контроллер
│   ├── ConstructorDb/          # Конструктор БД: таблицы, строки, страница записи
│   ├── Dialogs/                # VM диалогов (Collection, Group, Record)
│   └── Settings/               # SettingsViewModel
│
├── Views/                      # Экраны приложения (.axaml)
│   ├── MainWindow.axaml        # Главное окно
│   ├── ConstructorDb/          # Конструктор БД (список, таблица, страница записи)
│   │   └── Calendar/           # Day, Week, Month, Year, Diagram, List, Journal + контроллер
│   └── Settings/               # SettingsView
│
├── Controls/                   # Переиспользуемые UI компоненты
│   ├── Components/             # Navbar, Sidebar, ActivityBar, AllDayBar, Footer, ContextMenu
│   ├── Dialogs/                # Record, Collection, Group, Confirm, Property*, CreateItem
│   └── Widgets/                # AppButton, AppCheckBox, AppLabel, Selector, SearchInput, ScrollArea
│
├── Utils/                      # Вспомогательные утилиты
│   ├── AppSettings.cs          # Пути к БД (.localdata / %AppData%) + доступ к настройкам
│   ├── IAppSettingsStore.cs    # Интерфейс хранилища настроек (реализован поверх app.db)
│   ├── Autostart.cs            # Автозапуск приложения
│   ├── BackgroundWorker.cs     # Фоновые задачи
│   ├── Scaling.cs              # DPI-масштабирование
│   └── MarkdownUtils.cs        # Работа с Markdown
│
├── Assets/                     # Ресурсы
│   ├── Branding/               # Иконка приложения (icon.png/.ico/.icns, Info.plist, .desktop)
│   └── Styles/                 # Colors, Controls, Typography
│
├── Scripts/                    # windows-build.sh, linux-build.sh, macos-build.sh, linux-install.sh, generate-icons.sh
├── docs/                       # guides/, architecture/, features/, ui/, plans/, ROADMAP (см. docs/README.md)
│
│   # ── Файлы в корне проекта ──
├── DataBase.csproj             # Проект
├── DataBase.sln                # Solution
├── Directory.Build.props       # Вывод сборки → artifacts\ (а не bin\/obj\)
├── nuget.config                # Кэш NuGet-пакетов → packages\ внутри проекта
├── dotnet-tools.json           # Локальные dotnet-инструменты (например, EF CLI)
├── .gitignore                  # Игнор: artifacts, packages, dist, obj, *.db, .localdata…
├── .gitattributes              # Нормализация переносов строк (LF/CRLF)
├── README.md                   # Этот файл
│
│   # ── Служебные папки (в .gitignore, пересоздаются автоматически) ──
├── .localdata/                 # ⚙ Dev-данные (Debug): database.db, app.db
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

Приложение работает с двумя SQLite-БД (пути задаются в [Utils/AppSettings.cs](Utils/AppSettings.cs)):

- **`database.db`** — пользовательские данные (коллекции, поля, записи, события календаря).
- **`app.db`** — системная: реестр подключённых БД + настройки приложения.

**В продакшене (Release)** файлы лежат в стандартной папке данных пользователя ОС — `…/DataBase/`:

- **Windows** (`%APPDATA%`, Roaming):
  - `C:\Users\<Имя>\AppData\Roaming\DataBase\database.db`
  - `C:\Users\<Имя>\AppData\Roaming\DataBase\app.db`
- **Linux** (`~/.config`):
  - `/home/<имя>/.config/DataBase/database.db`
  - `/home/<имя>/.config/DataBase/app.db`
- **macOS** (`~/.config` — в .NET `ApplicationData` указывает на `~/.config`, а не на `~/Library/Application Support`):
  - `/Users/<имя>/.config/DataBase/database.db`
  - `/Users/<имя>/.config/DataBase/app.db`

Во время работы рядом появляются временные файлы SQLite: `*.db-wal`, `*.db-shm`.

**При разработке (Debug)** — папка `.localdata/` в корне проекта (находится по маркеру
`DataBase.csproj`, поэтому не зависит от раскладки вывода):
`calendar_avalonia/.localdata/database.db` и `.localdata/app.db` (в `.gitignore`).

> Дополнительные `.db`-файлы (в т.ч. общие в сетевых папках) подключаются через
> «Настройки → Управление базой данных»; их пути хранятся в `app.db`.
