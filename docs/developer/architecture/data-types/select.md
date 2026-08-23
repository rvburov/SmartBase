# Свойство: «Список» (`select`)

Часть [реестра типов свойств](overview.md). Единый источник классификации — [`FieldTypeInfo`](../../../../Core/Models/FieldTypeInfo.cs).


Хранит строку-значение выбранного варианта — **снимок текста**, а не ссылку на опцию. Набор опций —
в `ConfigJson` поля (ключ `options`), **формируется пользователем** в диалоге «Создать/Изменить свойство»
(блок «Пункты списка»). Редактор — `ComboBox`. Нюанс: переименование опции не мигрирует уже сохраненные
значения (в записях остается старый текст).

## Связанные документы

[overview](overview.md) · [features/constructor/table](../../features/constructor/table.md) · [features/constructor/record-page](../../features/constructor/record-page.md)
