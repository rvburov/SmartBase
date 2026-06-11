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

Результат: `artifacts/publish/MyPlanner/release_win-x64/MyPlanner.exe`

#### macOS (Intel)

```bash
cd calendar_avalonia
dotnet publish -c Release -r osx-x64 --self-contained true -p:PublishSingleFile=true
chmod +x artifacts/publish/MyPlanner/release_osx-x64/MyPlanner
```

Результат: `artifacts/publish/MyPlanner/release_osx-x64/MyPlanner`

#### macOS (Apple Silicon)

```bash
cd calendar_avalonia
dotnet publish -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true
chmod +x artifacts/publish/MyPlanner/release_osx-arm64/MyPlanner
```

Результат: `artifacts/publish/MyPlanner/release_osx-arm64/MyPlanner`

#### Linux (x64)

```bash
cd calendar_avalonia
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true
chmod +x artifacts/publish/MyPlanner/release_linux-x64/MyPlanner
```

Результат: `artifacts/publish/MyPlanner/release_linux-x64/MyPlanner`

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
├── MyPlanner.csproj            # Проект
├── MyPlanner.sln               # Solution
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

## Как работает режим «Месяц»

Режим «Месяц» реализован в [MonthView.axaml.cs](Views/ConstructorDb/Calendar/MonthView.axaml.cs). Это `UserControl`, который вручную рисует сетку 6 × 7 (6 недель × 7 дней) поверх двух слоёв: `DayGrid` с ячейками дней и `SpanCanvas` — оверлей-Canvas для многодневных событий. Вся раскладка пересчитывается с нуля при каждом изменении данных или размера контейнера.

### Построение сетки

Точка входа — `Refresh(events, currentDate)`, которая сохраняет события и текущую дату и вызывает `_BuildAll()`. Этот же метод вызывается при `SizeChanged` контейнера, поэтому раскладка всегда соответствует актуальному размеру окна.

`_BuildAll()`:

1. Определяет первый день месяца и сдвигает начало сетки к ближайшему понедельнику (`startOffset` — неделя начинается с Пн).
2. Считает ширину/высоту ячейки как `W / 7` и `H / 6`.
3. Проходит по 6 строкам недель. Для каждой недели сначала раскладывает **многодневные бары** на `SpanCanvas`, затем строит **7 ячеек дней**.

### Раскладка многодневных событий

`_ComputeWeekLayout(weekStart)` — жадный алгоритм распределения многодневных и all-day событий по строкам внутри недели (порт `_compute_week_layout` из PyQt-версии):

- События, пересекающие неделю, сортируются по дате начала, затем по убыванию длительности.
- Для каждого события ищется первая строка, где оно не пересекается с уже размещённым (`sc > rowEnds[r]`); если такой нет — добавляется новая строка.
- Результат — `Dictionary<eventId, LayoutEntry(RowIdx, Sc, Ec)>`, где `Sc`/`Ec` — индексы колонок начала и конца в пределах недели (0–6).

Бары рисуются методом `_AddSpanBar`. Учитываются «капы» (скруглённые торцы) только на реальных краях события: `leftCap`, если событие начинается в этой неделе, `rightCap` — если заканчивается. Под цветной полупрозрачный фон подкладывается белый слой, чтобы бар перекрывал линии сетки. События с одинаковым `Id`, разрезанные на несколько недель, хранятся списками в `_spanBgBorders` / `_spanTextBlocks`, поэтому ховер и выделение подсвечивают **все** сегменты одного события сразу.

### Ячейки дней и однодневные события

`_MakeCell` строит одну ячейку:

- **Число дня** в кружке справа-сверху. Сегодняшний день — белый текст на красном кружке; выходные (Сб/Вс) — красный текст; дни соседних месяцев — приглушённый серый, фон ячейки чуть темнее.
- **Однодневные события** (`_MakeChip`) укладываются в `StackPanel`, смещённый вниз на `topOff`, чтобы не перекрывать многодневные бары. `topOff` зависит от максимальной занятой строки spanning-событий в этой ячейке.
- Сколько чипов влезает (`maxVis`) рассчитывается из доступной высоты; всё, что не помещается (вместе со скрытыми барами), сворачивается в подпись **«+N ещё»** внизу ячейки.

Константы макета: высота строки числа `DateH=34`, высота бара `BarH=18`, шаг между барами `BarStep=20`, высота «+N ещё» `MoreH=16`.

### Взаимодействие

- **Ховер по ячейке** — подсветка фона (`_UpdateCellHover`), вычисляется по позиции курсора через `_DayAtPos`.
- **Ховер по кружку с числом** — число заменяется иконкой «+» (`_UpdateCircleHover` + `_CircleDayAtPos` проверяет попадание в круг радиусом 12px).
- **Клик по «+»** → `DayAddClicked` (создать событие в этот день).
- **Двойной клик по ячейке/числу** → `DaySelected` (перейти в режим «День»).
- **Клик по событию** → выделение (заливка сплошным цветом, белый текст). **Двойной клик по событию** → `RecordClicked` (открыть на редактирование).
- Клик мимо событий снимает выделение — обработчик вешается на окно в `OnAttachedToVisualTree`.

### Drag & Drop переноса событий

Перетаскивание реализовано вручную через захват указателя:

1. `_BeginDragTrack` запоминает событие, исходный день и позицию нажатия, захватывает указатель. Drag активируется только после смещения курсора более чем на 6px (`_OnDragMoved`), чтобы не путать с кликом.
2. Во время перетаскивания исходный чип/бар становится полупрозрачным, а на целевом дне рисуется **превью** (`_UpdateDragPreview`): существующие однодневные события дня сдвигаются вниз на `BarStep`, освобождая место; если места нет — превью не показывается.
3. При отпускании (`_OnDragReleased`), если целевой день отличается от исходного, у события на дельту дней сдвигаются `StartDt`/`EndDt` и вызывается `RecordMoved` для сохранения. `_CancelDrag` восстанавливает прозрачность и убирает превью.

### События (event'ы) контрола

| Событие | Когда возникает |
|---|---|
| `DaySelected` | Двойной клик по дню — переход в режим «День» |
| `DayAddClicked` | Клик по «+» в кружке дня — создать событие |
| `RecordClicked` | Двойной клик по событию — открыть на редактирование |
| `RecordMoved` | Событие перетащено на другой день |

---

## База данных

**Расположение** (определяется в [Utils/AppSettings.cs](Utils/AppSettings.cs)):
- При разработке (Debug): папка `Database/` рядом с проектом — `Database/database.db`
- В собранном виде (Release) — в пользовательской папке данных ОС:
  - **Windows:** `%APPDATA%\MyPlanner\database.db`
  - **macOS:** `~/.config/MyPlanner/database.db`
  - **Linux:** `~/.config/MyPlanner/database.db`
