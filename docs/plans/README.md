# Планы

Черновики и задумки по фичам. Высокоуровневые этапы — в [ROADMAP.md](../ROADMAP.md);
здесь — детальные планы по конкретным задачам (один файл на задумку).

## Как вести

- Имя файла — `lowercase-kebab-case.md` (например, `calendar-export.md`).
- В начале файла указывай статус:
  - 💡 **идея** — обсуждается, не начато;
  - 🚧 **в работе** — реализуется;
  - ✅ **готово** — сделано (потом перенеси суть в `features/` и удали план).
- Структура плана: **Зачем → Что сделать → Как (подход) → Открытые вопросы**.
- Связывай с фичами и архитектурой через `[[имя]]`.

## Известные кандидаты в планы

Из кода видно незавершённое — хорошие отправные точки:

- **Экспорт/импорт календаря** — [CalendarExporter.cs](../../Core/Exporters/CalendarExporter.cs) и [CalendarImporter.cs](../../Core/Importers/CalendarImporter.cs) пока пустые (например, CSV/iCal). См. [../features/import-export.md](../features/import-export.md).
- **Автозапуск** при входе в систему — [Utils/Autostart.cs](../../Utils/Autostart.cs) пока заглушка. См. [../features/settings.md](../features/settings.md).
- **Мультибаза, дальнейшие этапы** — обработка недоступности БД и статусов (комментарии «Этап 3/5» в коде). См. [../architecture/multi-database.md](../architecture/multi-database.md).

## Связанные документы

[[../README]], [[../architecture/overview]]
