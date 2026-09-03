# Расширение «Документы в коллекцию»

Часть [обзора расширений](overview.md): устройство одного расширения. Общий механизм
(контракт, реестр, настройки, видимость) — там же.

## Назначение

По выбранной папке создает **коллекцию конструктора**, где каждая запись — документ.

Первое расширение, которое не выгружает файл, а **пишет в базу**, — образец того, как
расширение работает с данными приложения, оставаясь отделимым. Тем же путем позже пошли
[[time-collector]], [[doc-registry]] и [[pdf-watermark]].

## Где в коде

Папка [Extensions/DocsCollection/](../../../../Extensions/DocsCollection/):

- [DocsCollectionExtension.cs](../../../../Extensions/DocsCollection/DocsCollectionExtension.cs) —
  контракт; настроек не объявляет.
- [DocsCollectionBuilder.cs](../../../../Extensions/DocsCollection/DocsCollectionBuilder.cs) —
  логика без UI: обход папки и создание коллекции.
- [DocsCollectionWindow.axaml](../../../../Extensions/DocsCollection/DocsCollectionWindow.axaml) +
  [.axaml.cs](../../../../Extensions/DocsCollection/DocsCollectionWindow.axaml.cs) — окно.

Тесты: [Tests/Extensions/DocsCollection/](../../../../Tests/Extensions/DocsCollection/).

## Как работает

### Схема создаваемой коллекции

Порядок колонок задается `SaveColumnOrder`:

| Колонка | Чем является | Что записывается |
|---|---|---|
| Наименование | системная `Title` (переименована через `SetSystemFieldName`) | имя файла без расширения |
| Название папки | поле типа `text` | имя папки документа |
| Формат файла | поле типа `text` | расширение без точки, нижний регистр |
| Файл | поле типа `file` | абсолютный путь (см. [FileValue](../../../../Core/Models/FileValue.cs)) |

### Три шага сборки

Шаги в [DocsCollectionBuilder.cs](../../../../Extensions/DocsCollection/DocsCollectionBuilder.cs)
разнесены намеренно:

- `Scan` — обход диска: `EnumerationOptions` со `IgnoreInaccessible`, скрытые и системные файлы
  пропускаются;
- `CreateShell` — коллекция и колонки на **общем** репозитории; там же срабатывает право
  `CreateCollection`;
- `FillRecords` — записи на **отдельном** подключении (`new AppDbContext(repo.DbPath)`): общий
  контекст EF принадлежит UI-потоку. Тот же прием, что у `AddRecordIsolated` и
  `DeleteCollectionIsolated`.

### Окно

Папка (`StorageProvider`), чекбокс рекурсии, строка расширений-фильтра, выбор базы, прогресс с
троттлингом (сообщение раз в 25 записей — поток постов в UI тормозит сильнее самой вставки) и
«Остановить». Отмена или ошибка — недособранная коллекция удаляется целиком
(`DeleteCollectionIsolated`).

### Доступ к базам — без правок приложения

Репозиторий расширение берет через
[RelationDbResolver](../../../../Database/RelationDbResolver.cs) (`Databases()` / `RepoFor(dbId)`),
а список коллекций в сайдбаре обновляет, найдя `SidebarTree` в визуальном дереве окна-владельца.
Событие «коллекции изменились» живет внутри UI и расширению недоступно; поиск по дереву обернут
в try/catch — не нашли, значит коллекция появится при следующем обновлении списка.

### Осознанные ограничения

⚠️ Поле «Файл» создается **без** правила авто-привязки
([FileAutoRule](../../../../Core/Models/FileAutoRule.cs)): правило умеет ровно одну папку (без
подпапок) и один формат, чего не хватает при рекурсивном обходе и смешанных расширениях. Пути
проставляются напрямую, поле остается редактируемым.

⚠️ Записи создаются публичным API (`AddRecord` + `SetFieldValue`), а не сырым пакетным INSERT,
как импорт: это дает запись `Description` (ее `ImportStreamedCollection` не пишет), но стоит
около трех коммитов и трех строк журнала изменений на документ. На типовых папках
(сотни-тысячи файлов) это секунды; для десятков тысяч — минуты, прогон отменяем.

## Тесты

[Tests/Extensions/DocsCollection/](../../../../Tests/Extensions/DocsCollection/) — обход папки и
фильтр форматов, каркас коллекции (поля, порядок колонок, уникализация имени, право «Создавать
коллекции») и наполнение записями на отдельном подключении.

## Связанные документы

[[overview]], [[time-collector]], [[../constructor/overview]], [[../../testing/extension-tests]]
