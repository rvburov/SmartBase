# Импорт и экспорт

## Назначение

Перенос данных извне и наружу. На сегодня **реально работает импорт из Excel**
(создание коллекции из листа). Часть путей экспорта/импорта пока заглушки.

## Где в коде

- Excel-импорт: [ExcelImporter.cs](../../Core/Importers/ExcelImporter.cs)
- UI: [ImportExportDialog](../../Controls/Dialogs/ImportExportDialog.axaml.cs)
- Создание коллекции из импорта: `ConstructorRepository.ImportExcelCollection(...)`
- Перенос коллекций/групп между БД: методы `ExportCollection`/`ImportCollection`,
  `ExportGroup`/`ImportGroup`, `MoveCollection`/`MoveGroup` в [ConstructorRepository](../../Database/Repositories/ConstructorRepository.cs)

## Импорт из Excel

`ExcelImporter.Parse(Spec)` читает `.xlsx`/`.xlsm` (через ClosedXML) в структуру
`ExcelImportData`:

- **Spec:** путь к файлу, имя листа (пусто → первый), строка заголовков (≥ 1),
  столбец названия и столбец описания (число «1» или буква «A»).
- Каждый столбец используемого диапазона = свойство: указанный → **название**
  записи, указанный → **описание**, остальные → **текстовые** свойства.
- Имена свойств берутся из строки заголовков; пустые → «Без названия N», повторы
  дедуплицируются.
- Данные читаются со строки сразу после заголовка; всё сохраняется как текст.
- Ошибки бросаются как `ExcelImportException` с дружелюбным текстом и показываются
  в баннере диалога (файл не найден/занят, лист не найден, пустой лист, неверный
  столбец и т.п.).

Результат отдаётся в `ImportExcelCollection(collectionName, groupId, data)` — создаётся
новая коллекция с системными колонками «Название»/«Описание», полями под остальные
столбцы и записями-строками.

## Перенос между БД

Внутри приложения коллекции и группы можно экспортировать в снимок
(`CollectionSnapshot`/`GroupSnapshot`) и импортировать в другую БД, а также
перемещать (`MoveCollection`/`MoveGroup`). Это используется при работе с
несколькими БД — см. [../architecture/multi-database.md](../architecture/multi-database.md).

## Заглушки (в планах)

Файлы [CalendarExporter.cs](../../Core/Exporters/CalendarExporter.cs) и
[CalendarImporter.cs](../../Core/Importers/CalendarImporter.cs) **пустые** — общий
экспорт/импорт календаря (например, CSV/iCal) ещё не реализован. См.
[../ROADMAP.md](../ROADMAP.md) и [../plans/](../plans/).

## Связанные документы

[[constructor-db/overview]], [[../architecture/multi-database]]
