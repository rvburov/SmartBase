# Расширение «Расчет сборочных единиц»

Часть [обзора расширений](overview.md): устройство одного расширения. Общий механизм
(контракт, реестр, настройки, видимость) — там же.

## Назначение

Переносит выбранные столбцы из исходного «Список КД и ТД» (лист «Исходные данные») в
.xlsm-шаблон на тот же лист, раскрашивает строки по уровню вложенности (0–10) и применяет
стиль ГОСТ. Реализация на ClosedXML.

## Где в коде

Папка [Extensions/AssemblyCalc/](../../../../Extensions/AssemblyCalc/):

- [AssemblyCalcExtension.cs](../../../../Extensions/AssemblyCalc/AssemblyCalcExtension.cs) —
  контракт и объявленная настройка `template` (файл-шаблон).
- [AssemblyCalcEngine.cs](../../../../Extensions/AssemblyCalc/AssemblyCalcEngine.cs) —
  логика без UI.
- [AssemblyCalcWindow.axaml](../../../../Extensions/AssemblyCalc/AssemblyCalcWindow.axaml) +
  [.axaml.cs](../../../../Extensions/AssemblyCalc/AssemblyCalcWindow.axaml.cs) — окно.

Книги открывает общий [ExcelBook.cs](../../../../Extensions/ExcelBook.cs) (см.
[«Открытие книг Excel»](overview.md#открытие-книг-excel)).

Тесты: [Tests/Extensions/AssemblyCalc/](../../../../Tests/Extensions/AssemblyCalc/).

## Как работает

### Чтение источника

Лист берется **по имени** — «Исходные данные», а не по номеру. Строки читаются с 6-й и до тех
пор, пока заполнен столбец «Уровень»: первая пустая ячейка уровня и есть конец данных.

Уровень превращается в целое число и в цвет строки. Результат пишется в копию шаблона
(`SaveAs`, макросы сохраняются), прогон отдает `IProgress` и слушает `CancellationToken`.

### Окно

Выбор исходного файла, шаблона и папки результата (`StorageProvider`), индикатор готовности,
прогон в фоне (`Task.Run`), по завершении — предложение открыть папку через
[ConfirmDialog](../../../../Controls/Dialogs/ConfirmDialog.axaml.cs).

### Файл-шаблон

Путь берется из объявленной расширением настройки `template` (устройство механизма —
[«Настройки расширения»](overview.md#настройки-расширения)); доступность файла проверяется
при запуске прогона, а не заранее.

## Тесты

[Tests/Extensions/AssemblyCalc/](../../../../Tests/Extensions/AssemblyCalc/) — вся карта
столбцов (включая 248 → 13 «ВЭ»), границы переноса, раскраска по уровню, область очистки
шаблона и перезапись результата в ту же минуту.

## Связанные документы

[[overview]], [[product-compare]], [[../../testing/extension-tests]]
