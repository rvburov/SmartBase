# Календарь-Планировщик

Многофункциональное desktop-приложение на Avalonia UI с поддержкой категорий,
шести режимов просмотра и удобным управлением событиями.

![Версия](https://img.shields.io/badge/version-1.0.0-blue)
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
dotnet run
```

### Сборка готового бинарника для раздачи

Каждая команда создаёт один самодостаточный файл — установка .NET на компьютере пользователя не нужна. Размер около 100–150 МБ.

#### Windows (x64)

**PowerShell, CMD или Git Bash**
```powershell
cd calendar_avalonia
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Результат: `artifacts/publish/DataBase/release_win-x64/DataBase.exe`

#### macOS (Intel)

```bash
cd calendar_avalonia
dotnet publish -c Release -r osx-x64 --self-contained true -p:PublishSingleFile=true
chmod +x artifacts/publish/DataBase/release_osx-x64/DataBase
```

Результат: `artifacts/publish/DataBase/release_osx-x64/DataBase`

#### macOS (Apple Silicon)

```bash
cd calendar_avalonia
dotnet publish -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true
chmod +x artifacts/publish/DataBase/release_osx-arm64/DataBase
```

Результат: `artifacts/publish/DataBase/release_osx-arm64/DataBase`

#### Linux (x64)

```bash
cd calendar_avalonia
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true
chmod +x artifacts/publish/DataBase/release_linux-x64/DataBase
```

Результат: `artifacts/publish/DataBase/release_linux-x64/DataBase`

> Все платформы можно собирать с одной машины — cross-compilation поддерживается.

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
│   ├── Exporters/              # CalendarExporter — экспорт данных
│   └── Importers/              # CalendarImporter — импорт данных
│
├── Database/                   # Слой данных (EF Core + SQLite)
│   ├── AppDbContext.cs         # EF Core контекст
│   ├── AppDbContextFactory.cs  # Фабрика для design-time миграций
│   ├── Entities/               # Entities.cs — таблицы БД
│   ├── Migrations/             # Миграции EF Core + снапшот модели
│   └── Repositories/           # CategoryRepository, ConstructorRepository
│
├── ViewModels/                 # MVVM логика
│   ├── MainWindowViewModel.cs
│   ├── ViewModelBase.cs
│   ├── Calendar/               # Day, Week, Month, Year, List, Journal + контроллер
│   ├── ConstructorDb/          # Конструктор БД: таблицы, строки, страница записи
│   ├── Dialogs/                # VM диалогов (Collection, Group, Record, PropertyManager)
│   └── Settings/               # SettingsViewModel
│
├── Views/                      # Экраны приложения (.axaml)
│   ├── MainWindow.axaml        # Главное окно
│   ├── ConstructorDb/          # Конструктор БД (список, таблица, страница записи)
│   │   └── Calendar/           # Day, Week, Month, Year, List, Journal + контроллер
│   └── Settings/               # SettingsView
│
├── Controls/                   # Переиспользуемые UI компоненты
│   ├── Components/             # Navbar, Sidebar, ActivityBar, AllDayBar, Footer, ContextMenu
│   ├── Dialogs/                # Record, Collection, Group, Confirm, Property*, CreateItem
│   └── Widgets/                # AppButton, AppCheckBox, AppLabel, Selector, SearchInput, ScrollArea
│
├── Utils/                      # Вспомогательные утилиты
│   ├── AppSettings.cs          # Пути к БД, настройки окна, масштаб
│   ├── Autostart.cs            # Автозапуск приложения
│   ├── BackgroundWorker.cs     # Фоновые задачи
│   ├── Scaling.cs              # DPI-масштабирование
│   └── MarkdownUtils.cs        # Работа с Markdown
│
├── Assets/                     # Ресурсы
│   ├── Icons/                  # Иконки
│   └── Styles/                 # Colors, Controls, Typography
│
├── Scripts/                    # installer.ps1, setup.ps1 — установка/настройка
├── docs/                       # DEVELOPER_GUIDE, USER_GUIDE, ROADMAP
│
├── DataBase.csproj            # Проект
├── DataBase.sln               # Solution
├── Directory.Build.props       # Артефакты сборки → artifacts\ (а не bin\/obj\)
├── nuget.config                # Кэш NuGet-пакетов → packages\ внутри проекта
│
├── artifacts/                  # ⚙ Служебная: вся сборка (bin/obj/publish). В .gitignore
└── packages/                   # ⚙ Служебная: кэш NuGet-пакетов. В .gitignore
```

> **Служебные папки `artifacts/` и `packages/`** держат все временные файлы сборки
> и скачанные пакеты внутри проекта (аналог `venv` в Python) — системный диск не
> засоряется. Обе в `.gitignore` и пересоздаются автоматически при `dotnet build`.
> Полностью очистить проект: удалить `artifacts\` и `packages\`.

---

## База данных

**Расположение** (определяется в [Utils/AppSettings.cs](Utils/AppSettings.cs)):
- При разработке (Debug): папка `Database/` рядом с проектом — `Database/database.db`
- В собранном виде (Release) — в пользовательской папке данных ОС:
  - **Windows:** `%APPDATA%\DataBase\database.db`
  - **macOS:** `~/.config/DataBase/database.db`
  - **Linux:** `~/.config/DataBase/database.db`
