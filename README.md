# Календарь-Планировщик

Многофункциональное desktop-приложение на Avalonia UI с поддержкой категорий,
шести режимов просмотра и удобным управлением событиями.

![Версия](https://img.shields.io/badge/version-1.0.0-blue)
![.NET](https://img.shields.io/badge/.NET-9.0+-purple)
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

Требуется **.NET SDK 9.0+**. Установить можно любым способом:

#### Windows

**winget** (встроен в Windows 10/11):
```powershell
winget install Microsoft.DotNet.SDK.9
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
sudo apt-get install -y dotnet-sdk-9.0
```

**dnf** (Fedora):
```bash
sudo dnf install dotnet-sdk-9.0
```

**snap:**
```bash
sudo snap install dotnet-sdk --classic --channel=9.0
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

**Сайт (все платформы):** https://dotnet.microsoft.com/download/dotnet/9.0

> Если вы просто хотите запустить готовый `.exe` — ничего устанавливать не нужно,
> файл не требует рантайма (self-contained).

---

### Запуск из исходников (для разработки)

```bash
cd calendar_avalonia
dotnet run
```

### Сборка готового .exe для раздачи

```bash
cd calendar_avalonia
dotnet publish -c Release
```

Готовый файл: `bin\Release\net9.0\win-x64\publish\MyPlanner.exe`

- Не требует установки .NET на компьютере пользователя
- Работает на Windows 10/11 x64
- Размер около 120–150 МБ

---

## Структура проекта

```
calendar_avalonia/
├── Program.cs                  # Точка входа
├── App.axaml                   # Application definition
│
├── Core/                       # Бизнес-логика (без UI и БД)
│   ├── Models/                 # Event, Task, Category
│   ├── Validators/
│   └── Filters/
│
├── Database/                   # Слой данных
│   ├── AppDbContext.cs         # EF Core контекст
│   ├── Migrations/
│   └── Repositories/           # CRUD операции
│
├── ViewModels/                 # MVVM логика
│   ├── Calendar/
│   ├── Dialogs/
│   └── Settings/
│
├── Views/                      # Экраны приложения (.axaml)
│   ├── Calendar/               # Day, Week, Month, Year, List, Journal
│   ├── ConstructorDb/
│   └── Settings/
│
├── Controls/                   # Переиспользуемые UI компоненты
│   ├── Components/             # Navbar, Sidebar, ActivityBar
│   ├── Dialogs/                # EventDialog, CollectionDialog
│   └── Widgets/                # AppButton, AppCheckBox, Selector
│
└── Assets/
    └── Styles/                 # Colors, Controls, Typography
```

---

## База данных

**Расположение:**
- При разработке: `database.db` в корне проекта
- В собранном `.exe`: `%APPDATA%\MyPlanner\database.db`
