# SmartBase — универсальный конструктор баз данных

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/rvburov/SmartBase/main/docs/assets/banner-dark.png">
    <img src="https://raw.githubusercontent.com/rvburov/SmartBase/main/docs/assets/banner-light.png" alt="SmartBase — календарь, задачи и конструктор данных в одном desktop-приложении. Без сервера и учетных записей — данные в ваших файлах.">
  </picture>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/version-1.2.0-blue" alt="Версия">
  <img src="https://img.shields.io/badge/.NET-10.0+-512BD4?logo=dotnet&logoColor=white" alt=".NET">
  <img src="https://img.shields.io/badge/C%23-14-239120?logo=csharp&logoColor=white" alt="C#">
  <img src="https://img.shields.io/badge/Avalonia_UI-11.2-8B44AC" alt="Avalonia">
  <img src="https://img.shields.io/badge/EF_Core-9.0-512BD4" alt="EF Core">
  <img src="https://img.shields.io/badge/SQLite-003B57?logo=sqlite&logoColor=white" alt="SQLite">
  <img src="https://img.shields.io/badge/PostgreSQL-14+-4169E1?logo=postgresql&logoColor=white" alt="PostgreSQL">
  <img src="https://img.shields.io/badge/Windows-0078D4?logo=data:image/svg%2Bxml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCAyNCAyNCI%2BPHBhdGggZmlsbD0id2hpdGUiIGQ9Ik0wIDMuNDQ5TDkuNzUgMi4xdjkuNDUxSDBtMTAuOTQ5LTkuNkwyNCAwdjExLjRIMTAuOTQ5TTAgMTIuNmg5Ljc1djkuNDUxTDAgMjAuNjk5TTEwLjk0OSAxMi42SDI0VjI0bC0xMi45LTEuODAxIi8%2BPC9zdmc%2B" alt="Windows">
  <img src="https://img.shields.io/badge/macOS-000000?logo=apple&logoColor=white" alt="macOS">
  <img src="https://img.shields.io/badge/Linux-FCC624?logo=linux&logoColor=black" alt="Linux">
</p>

Кросс-платформенное desktop-приложение (Windows / macOS / Linux): прежде всего
**универсальный конструктор баз данных** — коллекции с произвольными полями, как в
Notion/Airtable, — а над ним **календарь**, **задачи**, **библиотека документов**,
**расширения** и **интеграции** со сторонними программами; рядом — **заметки** в файлах
Markdown. Сервер и учетные записи не нужны: данные лежат в файлах SQLite (в том числе общих
в сетевой папке), а когда пишущих становится много — в PostgreSQL.

---

## Что это

Основа — **конструктор**: пользователь создает **коллекции** (таблицы) с произвольными
**полями** и наполняет их **записями**. Календарь, задачи и библиотека — представления тех же
записей: запись с полем-датой видна в **календаре**, с полем-исполнителем — в **задачах**, с
файлом-документом — в **библиотеке документов**. Отдельных сущностей «событие», «задача» и
«документ» в базе нет — это проекции записи. Расширения и интеграции читают и пишут те же
коллекции; **заметки** — самостоятельный раздел для файлов Markdown на диске.

Разделы переключаются иконками слева (Activity bar):

| Раздел                          | Что делает                                                                                                                                                                          |
| ------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Конструктор базы данных** | коллекции, поля, записи: таблица или карточки + панель записи                                                                               |
| **Календарь**          | 7 режимов: Расписание, Журнал, День, Неделя, Месяц, Год, Диаграмма                                                                         |
| **Задачи**                | карточки задач: исполнитель, назначил, статус, срок, остаток времени                                                                 |
| **Заметки**               | папка с файлами Markdown на диске: дерево, редактор, просмотр                                                                     |
| **Библиотека документов** | файлы из записей лентой карточек: миниатюры, поиск внутри документов с подсветкой, фильтр по типу                       |
| **Расширения**        | подключаемые плагины, каждый в своем окне (PDF, составы изделий, документы, трудоемкость, регистрация КД, водяной знак) |
| **Профиль**              | свои данные, допуск пользователей к базам, история изменений                                                                              |
| **Настройки**          | общее, оформление, базы данных, резервное копирование, разделы, интеграции, сочетания клавиш, инструкция, диагностика   |

## Возможности

**Данные**

- **Типы полей** — текст, число, дата, список, статус, мультисписок, чекбокс, ссылка,
  файл, пользователь, связь (ссылки между записями) и вычисляемые (кто и когда создал/изменил).
- **Мультибаза** — несколько баз одновременно, локальных и в сетевых папках; календарь и
  задачи агрегируют записи из всех, коллекции переносятся между базами.
- **SQLite и PostgreSQL** — работают на равных и одновременно, в одном сайдбаре.
- **Импорт/экспорт** — Excel/CSV/JSON → коллекции и обратно (`.xlsx`/`.xlsm`/`.csv`/`.json`),
  потоково и пакетно (все листы книги, папка файлов, несколько коллекций сразу).
- **Миллионы записей** — своя рисованная сетка таблицы (10 млн строк × 1000 колонок) и
  keyset-пагинация вместо выборки целиком.

**Календарь и задачи**

- **Drag & Drop** событий по времени и дням, **resize** длительности мышью.
- **События «весь день»**, в том числе многодневные; поиск по названию на лету.
- **Категории** — именованные цветные метки, задают акцент событий и записей.
- **Уведомления** — нативный баннер ОС в момент события, клик открывает запись; на иконке
  приложения значок с числом непросмотренных.

**Документы и заметки**

- **Библиотека документов** — файлы из записей лентой карточек с миниатюрой первой страницы;
  поиск внутри документов с подсветкой найденного, фильтр по типу, панель чтения.
- **Заметки** — папка с файлами Markdown на диске: дерево, редактор, просмотр. Файлы остаются
  файлами — в базу программа их не переносит.

**Интеграции**

- Обмен со сторонними программами: соединения, запросы, сценарии, потоки событий; прием данных
  через папку-приемник и по HTTP на этом компьютере.
- Наружу программа выходит через одну дверь и только в рамках политики организации: что не
  разрешено явно, запрещено.

**Совместная работа (без сервера)**

- Пользователь определяется автоматически по `учетка@компьютер` — входа и паролей нет.
- **Допуск к базе** дает администратор или пароль самой базы; права и список пользователей
  хранятся внутри файла базы и путешествуют вместе с ним.
- **История изменений** и живая синхронизация: правки соседа по общей папке подхватываются
  без перезапуска.

**Приложение**

- Автозапуск при входе в систему, фоновый режим со значком в трее.
- Автообновление из сетевой папки; та же раздача работает установщиком и деинсталлятором.
- Резервное копирование баз по расписанию: SQLite — `VACUUM INTO`, PostgreSQL — `pg_dump`.
- Темы оформления, зум всего интерфейса, DPI-масштабирование (PerMonitorV2).
- Диагностический лог в отдельный `logs.db` — «Настройки → Диагностика».

## Технологии

| Что                 | Чем                                                                                                                    |
| ---------------------- | ------------------------------------------------------------------------------------------------------------------------- |
| UI                     | **Avalonia UI 11.2** (тема Fluent), .NET 10, своя рисованная сетка таблиц, собственные токены стилей |
| MVVM                   | **CommunityToolkit.Mvvm**; крупные интерактивные модули — намеренно code-behind |
| Данные           | **EF Core 9** + **SQLite**, **Npgsql** для PostgreSQL, миграции EF                           |
| Таблицы         | **ClosedXML** и **DocumentFormat.OpenXml** (потоковая SAX-запись), **ExcelDataReader**  |
| Текст             | **Markdig** — разбор markdown; показ и живое оформление собирает свой рендер; **AvaloniaEdit** — редактор заметок |
| Документы         | **FTS5** (SQLite) — поиск по тексту, **PdfPig** и **DocumentFormat.OpenXml** — текст PDF и DOCX, **Docnet.Core** (PDFium) — миниатюры и страницы PDF |
| Уведомления | **DesktopNotifications** (Windows/Linux), macOS — свой код                                                  |
| Логи               | **Serilog** + приемник SQLite (`logs.db`)                                                                 |
| Расширения   | **PdfPig** — разбор PDF, **Docnet.Core** (PDFium) + **SkiaSharp** — впечатывание водяного знака, **PDFsharp** — сборка результата |

## Как устроено

Слои: `Core/` (модели, валидаторы, импорт/экспорт — без UI и БД) → `Database/` (EF Core:
контексты, репозитории, мультибаза) → `ViewModels/` + `Views/` + `Controls/`. Обмен со
сторонними программами — `Integrations/`, единственная дверь наружу. DI-контейнера нет —
зависимости собираются вручную.

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
> собирается и работает, но нативных уведомлений в нем нет — молча, без ошибки.
> Зачем нужны два таргета —
> [build-and-run.md](docs/developer/guides/build-and-run.md#два-таргета-и-почему-их-два).

`dotnet run` каждый раз проверяет изменения и при необходимости пересобирает проект
(первая сборка в сессии — «холодная» и долгая). Чтобы **не пересобирать при повторных
запусках**, когда код не менялся:

```bash
dotnet run -f net10.0 --no-build      # запустить уже собранное, без пересборки
```

Еще быстрее — запускать собранный бинарь напрямую, вообще без проверки сборки. Папка
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
dotnet test SmartBase.sln       # оба набора ядра
./Tests/run-all.sh              # оба набора ядра и стенд панели чтения (+ --with-bench)

./Tests/run-extensions.sh              # все наборы расширений (Tests/Extensions/*)
./Tests/run-extensions.sh PdfRename    # один набор
./Tests/run-all.sh --with-extensions   # ядро + расширения
```

Тесты расширений — отдельные сборки, по проекту на расширение: удаление расширения уносит
и его тесты, а наборы ядра о расширениях не знают
([extension-tests.md](docs/developer/testing/extension-tests.md)). Общая обвязка (временная база,
временная папка, синхронный прогресс) — в `Tests/ExtensionsShared/`, снаружи `Tests/Extensions/`,
чтобы не попасть в глоб прогона.

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
версия создает отдельную. Это и есть то, что раздается пользователям. Установка .NET
им не нужна (self-contained), размер ~100–150 МБ.

Аргументы скриптов: `Scripts/<ОС>-build.sh [RID] [Config] [Version]`. Номер версии —
**третий аргумент** (напр. `Scripts/windows-build.sh win-x64 Release 1.2.3`): он
**записывается в `<Version>`** в [SmartBase.csproj](SmartBase.csproj) и становится текущим
для дальнейших сборок. Без аргумента берется то, что уже в csproj. Версия и дата
сборки видны в приложении: **Настройки → О программе**. Полное описание —
[docs/developer/guides/build-and-run.md](docs/developer/guides/build-and-run.md).

#### Windows (x64)

```bash
cd SmartBase
Scripts/windows-build.sh                        # win-x64, версия из csproj
Scripts/windows-build.sh win-x64 Release 1.2.3  # с номером версии 1.2.3
```

> Скрипты — bash, на Windows запускаются **из Git Bash**. Если появляется «Нет
> установленных дистрибутивов подсистемы Windows для Linux», `.sh` ушел в WSL-овый
> `bash.exe` — запустите bash из Git по абсолютному пути:
> `/usr/bin/bash Scripts/windows-build.sh` ([подробнее](docs/developer/guides/build-and-run.md#сборка-для-раздачи)).

Результат: `dist/Version <версия>/win-x64/SmartBase.exe` — иконка уже вшита в `.exe`.
Рядом кладется `SmartBase-Setup.exe` (тот же бинарь в режиме установщика: установка в
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
│   ├── Calendar/               # Календарная арифметика (CalendarMath)
│   ├── Documents/              # Библиотека: форматы, текст документов, подсветка, страницы PDF
│   ├── Notes/                  # Заметки: хранилище Markdown, разметка
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
│   ├── SqliteGate.cs           # Все подключения SQLite — под замком своего пула
│   ├── DocIndex/               # Кеш текста документов библиотеки (FTS5)
│   ├── Backup/                 # Резервное копирование (VACUUM INTO, pg_dump)
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
│   ├── ConstructorDb/          # Конструктор базы данных (таблица, карточки, страница записи)
│   │   └── Calendar/           # Расписание, Журнал, День, Неделя, Месяц, Год, Диаграмма
│   ├── Tasks/                  # Раздел «Задачи» (контроллер + список карточек)
│   ├── Notes/                  # Раздел «Заметки» (дерево хранилища, редактор Markdown)
│   ├── Library/                # Раздел «Библиотека документов» (лента, панель чтения)
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
│   ├── DrawnGrid/              # Рисованная сетка таблиц: конструктор и служебные таблицы
│   ├── Markup/                 # Показ и оформление Markdown
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
│   ├── AssemblyCalc/           #   Расчет сборочных единиц
│   ├── DocsCollection/         #   Документы в коллекцию
│   ├── TimeCollector/          #   Сводная трудоемкость (порт макроса «Обновить»)
│   ├── DocRegistry/            #   Регистрация КД, просмотр и подтверждаемые правки по сообщению
│   └── PdfWatermark/           #   Водяной знак на PDF (впечатывание в растр, PT Sans)
│
├── Integrations/               # Обмен со сторонними программами: соединения, запросы, сценарии,
│                               #   потоки событий, прием; единственная дверь наружу (EgressHttp.Open.cs)
│
├── Settings/                   # Реестры разделов: SettingsRegistry, ProfileRegistry
├── Logging/                    # Serilog → logs.db (AppLog) + чтение для «Диагностики»
├── Setup/                      # Установщик/деинсталлятор и самозамена при обновлении
│
├── Utils/                      # Вспомогательные утилиты (без привязки к контролам)
│   ├── AppSettings.cs          # Пути к БД (.localdata / %AppData%) + доступ к настройкам
│   ├── CurrentUser.cs          # Идентичность «учетка@компьютер» + ФИО из профиля
│   ├── Autostart.cs            # Автозапуск при входе в систему
│   ├── BackgroundBehavior.cs   # Фоновый режим и трей
│   ├── SingleInstance.cs       # Один экземпляр приложения
│   ├── Scaling.cs / FontScale.cs # DPI-масштабирование и зум интерфейса
│   └── MarkdownUtils.cs        # Работа с Markdown
│
├── Assets/                     # Ресурсы
│   ├── Branding/               # Иконка: мастера icon-windows/macos/linux.png → icon.ico/.icns, Info.plist, .desktop
│   └── Styles/                 # Colors, Typography, Icons, темы контролов
│
├── Scripts/                    # windows-build.sh, linux-build.sh, macos-build.sh, linux-install.sh, generate-icons.sh,
│                               #   worktree.sh (рабочие папки ИИ-агентов: создать, проверить, влить в main, убрать),
│                               #   cloud-session.sh (облачная сессия Claude Code: автор коммитов — пользователь)
├── Tests/                      # Unit, Integration (xUnit), Extensions/* + ExtensionsShared
│                               #   плюс Benchmarks, DbGenerator, MultiProcess и стенды без экрана
│                               #   (GridProto, RecordPage, Sections, DocReader…, см. Tests/README.md)
├── docs/                       # developer/ (architecture, features, ui, guides, testing, qa-checklists), user/, ROADMAP (см. docs/README.md)
│   └── assets/                 # Картинки документации: баннер README (светлый и темный)
│
│   # ── Файлы в корне проекта ──
├── SmartBase.csproj             # Проект
├── SmartBase.sln                # Solution
├── Directory.Build.props       # Вывод сборки → artifacts\ (а не bin\/obj\); рабочим папкам агентов — общий кэш пакетов
├── nuget.config                # Кэш NuGet-пакетов → packages\ внутри проекта
├── dotnet-tools.json           # Локальные dotnet-инструменты (например, EF CLI)
├── .gitignore                  # Игнор: artifacts, packages, dist, obj, *.db, .localdata, рабочие папки агентов…
├── .gitattributes              # Нормализация переносов строк (LF/CRLF)
├── .claude/settings.json       # Настройки Claude Code: рабочие папки ветвятся от локального main
├── AGENTS.md                   # Указания ИИ-агентам: правила проекта, параллельная работа
├── README.md                   # Этот файл
│
│   # ── Рабочие папки ИИ-агентов (в .gitignore; убирать только Scripts/worktree.sh remove) ──
├── .claude/worktrees/          # Claude Code: копия проекта на ветке worktree-<имя>
├── .agents/worktrees/          # Другие ИИ: копия проекта на ветке <имя>
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
> ее удаление сотрет локальную базу разработки, поэтому при «очистке проекта» ее не трогают.
>
> Папки **`.claude/worktrees/`** и **`.agents/worktrees/`** — рабочие папки ИИ-агентов: у каждого
> чата своя копия проекта на своей ветке, готовая работа вливается в `main` (правила —
> [AGENTS.md](AGENTS.md)). Они тоже в `.gitignore`, но это не временные файлы: внутри может быть
> незакоммиченная или не влитая работа. Проверить — `Scripts/worktree.sh check`, убрать —
> `Scripts/worktree.sh remove <имя>`; удалять руками нельзя.

---

## Куда что устанавливается

Права администратора не нужны ни на одной ОС — все кладется в пользовательские папки
(на Windows это заодно снимает запрос UAC). Пути задаются в
[Setup/InstallService.cs](Setup/InstallService.cs).

### Приложение и установщик

|                                                               | Windows                       | Linux                         | macOS                          |
| ------------------------------------------------------------- | ----------------------------- | ----------------------------- | ------------------------------ |
| Папка установки по умолчанию         | `%LOCALAPPDATA%\SmartBase\` | `~/.local/share/SmartBase/` | `~/Applications/`            |
| Приложение                                          | `SmartBase.exe`             | `SmartBase`                 | `SmartBase.app` (бандл) |
| Установщик — рядом, в той же папке | `SmartBase-Setup.exe`       | `SmartBase-Setup`           | `SmartBase-Setup.app`        |

Полные пути по умолчанию:

- **Windows** — `C:\Users\<Имя>\AppData\Local\SmartBase\SmartBase.exe`
- **Linux** — `/home/<имя>/.local/share/SmartBase/SmartBase`
  (папка — `$XDG_DATA_HOME`, по умолчанию `~/.local/share`)
- **macOS** — `/Users/<имя>/Applications/SmartBase.app`

**Отдельного деинсталлятора нет:** установщик копируется в папку установки и служит
единственной точкой входа для обновления и удаления.

**Своя папка.** Путь установки можно задать вручную. На Windows и Linux к нему
автоматически дописывается подпапка `SmartBase`, если ее там еще нет: удаление чистит
папку установки целиком, и без этого оно снесло бы чужое содержимое общей папки. На macOS
подпапка не нужна — единица установки сама по себе изолирована, это бандл `.app`
(`EnsureDedicatedDir` в [InstallService.cs](Setup/InstallService.cs)).

### Ярлыки и регистрация в системе

|                                                | Windows                                                                                                                                                                                            | Linux                                                                                           | macOS                                                     |
| ---------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------- | --------------------------------------------------------- |
| Меню приложений                  | `%APPDATA%\Microsoft\Windows\Start Menu\Programs\SmartBase\SmartBase.lnk`                                                                                                                        | `~/.local/share/applications/SmartBase.desktop`                                               | Launchpad/Finder (через`lsregister`)               |
| Рабочий стол (по галочке)  | `SmartBase.lnk`                                                                                                                                                                                  | `SmartBase.desktop` (с битом `+x` — этого требует GNOME)                 | —                                                        |
| Удаление                               | `Uninstall SmartBase.lnk` в меню и в папке установки; запись `HKCU\…\CurrentVersion\Uninstall\SmartBase` для «Приложения и возможности» | `SmartBase-uninstall.desktop` в меню + `uninstall.sh` в папке установки | двойной клик по`SmartBase-Setup.app`       |
| Автозапуск (по настройке) | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, значение `SmartBase`                                                                                                             | `~/.config/autostart/smartbase.desktop`                                                       | `~/Library/LaunchAgents/com.romanburov.smartbase.plist` |

На Linux иконка распаковывается в папку установки (`icon.png`) — на нее ссылаются оба
`.desktop`-ярлыка.

### Временные файлы обновления

Новая версия скачивается во временную папку ОС (`%TEMP%`, на Unix — `$TMPDIR`) под именем
`SmartBase.update.exe` или `SmartBase.update.app`. После перезапуска апдейтер ее не убирает —
хвост подчищается при следующем старте приложения (`CleanStaleUpdateArtifacts`).

Механика установки, обновления и удаления целиком —
[auto-update.md](docs/developer/guides/auto-update.md).

---

## База данных

Приложение держит три SQLite-файла (пути задаются в [Utils/AppSettings.cs](Utils/AppSettings.cs)):

- **`database.db`** — пользовательские данные (коллекции, поля, записи, события календаря);
  это база «по умолчанию», к ней добавляются любые другие — см. мультибазу ниже.
- **`app.db`** — системная: реестр подключенных БД + настройки приложения.
- **`logs.db`** — диагностический лог (Serilog), читается в «Настройки → Диагностика».

**В продакшене (Release)** файлы лежат в стандартной папке данных пользователя ОС — `…/SmartBase/`:

- **Windows** (`%APPDATA%`, Roaming):
  - `C:\Users\<Имя>\AppData\Roaming\SmartBase\database.db`
  - `C:\Users\<Имя>\AppData\Roaming\SmartBase\app.db`
  - `C:\Users\<Имя>\AppData\Roaming\SmartBase\logs.db`
- **Linux** (`$XDG_CONFIG_HOME`, по умолчанию `~/.config`):
  - `/home/<имя>/.config/SmartBase/database.db`
  - `/home/<имя>/.config/SmartBase/app.db`
  - `/home/<имя>/.config/SmartBase/logs.db`
- **macOS** (`~/Library/Application Support` — в отличие от Linux, .NET отдает под
  `ApplicationData` нативную папку Apple, а не `~/.config`):
  - `/Users/<имя>/Library/Application Support/SmartBase/database.db`
  - `/Users/<имя>/Library/Application Support/SmartBase/app.db`
  - `/Users/<имя>/Library/Application Support/SmartBase/logs.db`

Во время работы рядом появляются временные файлы SQLite: `*.db-wal`, `*.db-shm`.

### Служебные файлы в папке данных

Кроме трех баз там же лежат служебные файлы. Они **остаются на диске после выхода
из приложения** — это норма, а не мусор:

| Файл          | Кто создает                                                 | Когда появляется                                                                                                                                                                                                                                                                                                                                                                                                                |
| ----------------- | --------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `instance.lock` | [Utils/SingleInstance.cs](Utils/SingleInstance.cs)                     | при каждом запуске; пустой (0 байт) — защита от второго экземпляра держится на эксклюзивном дескрипторе`FileShare.None`, а не на наличии файла                                                                                                                                                                                         |
| `show.signal`   | [Utils/SingleInstance.cs](Utils/SingleInstance.cs)                     | только при запуске второй копии поверх работающей: второй процесс пишет метку времени и выходит, работающий показывает окно                                                                                                                                                                                                           |
| `crash.log`     | [Program.cs](Program.cs)                                               | только при необработанном исключении; синхронный резерв к`logs.db`, время в нем локальное (в `logs.db` — UTC)                                                                                                                                                                                                                                                        |
| `installs.json` | [Setup/InstallService.Discovery.cs](Setup/InstallService.Discovery.cs) | после установки/обновления; список папок, куда поставлено приложение — по нему установщик находит уже установленные копии, где бы они ни лежали. Пишется в системную папку данных даже при Debug-сборке: у установщика свой`DataDir`, без `.localdata` |

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

| Раздел                                                                         | Про что                                                                                                                       |
| ------------------------------------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------- |
| [architecture/overview.md](docs/developer/architecture/overview.md)                   | слои, запуск, MVVM и code-behind                                                                                         |
| [architecture/data-model.md](docs/developer/architecture/data-model.md)               | доменные модели и таблицы                                                                                     |
| [architecture/data-types/](docs/developer/architecture/data-types/)                   | типы полей — файл на тип                                                                                         |
| [architecture/users-permissions.md](docs/developer/architecture/users-permissions.md) | пользователи, допуск, права, журнал                                                                    |
| [features/](docs/developer/features/)                                                 | календарь, конструктор, задачи, библиотека, заметки, импорт/экспорт, уведомления, расширения, интеграции |
| [guides/build-and-run.md](docs/developer/guides/build-and-run.md)                     | сборка и запуск во всех режимах                                                                           |
| [guides/developer-guide.md](docs/developer/guides/developer-guide.md)                 | миграции EF, соглашения, раскладка вывода                                                          |
| [testing/overview.md](docs/developer/testing/overview.md)                             | автотесты, бенчмарки, стенды                                                                                |
| [user/](docs/user/)                                                                   | инструкция пользователя — ее же показывает раздел настроек                       |
| [AGENTS.md](AGENTS.md)                                                                | указания ИИ-агентам: правила проекта, параллельная работа в рабочих папках        |
| [ROADMAP.md](docs/ROADMAP.md)                                                         | планы развития                                                                                                         |
| [ROADMAP-API.md](docs/ROADMAP-API.md)                                                 | план интеграций со сторонними программами                                                                  |
