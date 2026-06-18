# Страница записи (RecordPageView)

## Назначение

Правая панель с деталью записи в стиле Obsidian: сверху — свойства (типизированные
поля), снизу — текстовое описание в Markdown с переключением «редактор ↔ просмотр».
Все изменения сохраняются в БД сразу (по потере фокуса).

## Где в коде

- View: [RecordPageView.axaml.cs](../../../Views/ConstructorDb/RecordPageView.axaml.cs)
- ViewModel: [RecordPageViewModel](../../../ViewModels/ConstructorDb/) (`Show(repository, recordId, collectionId)`)
- Markdown: [MarkdownUtils](../../../Utils/MarkdownUtils.cs) + `Markdown.Avalonia`

## Как работает

`Show(repository, recordId, collectionId)` создаёт `RecordPageViewModel`, заполняет
заголовок, текст описания и список свойств (`BuildProperties`).

### Заголовок

Кликабельный `TextBlock` превращается в `TextBox` для редактирования; по Enter/Escape
или потере фокуса возвращается обратно. Пустой заголовок показывается серым «Без
названия».

### Свойства

Для каждого поля (`PropertyRowVm`) `MakeInputForField` строит контрол под тип:

| `FieldType` | Контрол |
|---|---|
| `text` / `url` | `TextBox` (у url — watermark `https://`) |
| `number` | `TextBox` |
| `date` | `CalendarDatePicker` (формат `dd.MM.yyyy`) |
| `select` | `ComboBox` с вариантами из `ConfigJson` → `options` |
| `checkbox` | `CheckBox` (хранится как `1`/`0`) |

Каждый ввод сохраняется по потере фокуса/изменению через `row.Persist()`
(→ `SetFieldValue` в репозитории).

### Описание (Markdown)

`MarkdownEditor` — исходный текст; `PreviewToggle` переключает на динамический
просмотр (`MarkdownPreview.Markdown`). Сохранение текста и заголовка — по потере
фокуса (`WireAutoSave`, «save on blur», чтобы не было шторма записей при печати),
после чего поднимается `RecordChanged`.

### Кнопка панели коллекции

`CollectionPanelBtn` шлёт `CollectionPanelToggled` — MainWindow показывает/прячет
блок коллекции рядом со страницей записи.

## Взаимодействия (события наверх)

- `RecordChanged` — запись изменена (обновить таблицу/счётчики).
- `CollectionPanelToggled` — переключить видимость панели коллекции.

## Связанные документы

[[overview]], [[tables]]
