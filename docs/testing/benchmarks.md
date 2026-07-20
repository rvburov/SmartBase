# Бенчмарки (Tests/Benchmarks)

Замеры производительности — **не автотесты**: работают на больших объёмах (гигабайты,
минуты), запускаются точечно и вручную. Один консольный проект с диспетчером команд;
общие мелочи (парсинг аргументов, форматирование) — в `BenchUtil`.

Запуск из корня репозитория:

```bash
dotnet run -c Release --project Tests/Benchmarks -- sidebar [--db PATH] [--target PATH] [--light]
dotnet run -c Release --project Tests/Benchmarks -- import  [--rows N] [--dir PATH]
dotnet run -c Release --project Tests/Benchmarks -- export  [--rows N] [--dir PATH]
dotnet run -c Release --project Tests/Benchmarks -- pgload  [клиентов] [секунд] [--db ИМЯ]
```

| Команда | Что мерит | Требования |
|---------|-----------|-----------|
| **sidebar** | операции сайдбара на большом стенде (чтения дерева, календарь, дубль/перенос/удаление коллекций и групп) | заранее сгенерированный стенд — см. [test-database.md](test-database.md) |
| **import** | импорт большого CSV (Analyze + чтение/запись, строк/с) | — |
| **export** | экспорт коллекции во все форматы (CSV/JSON/XLSX/XLSM) | — |
| **pgload** | нагрузка PostgreSQL: N клиентов со своими подключениями, сверка целостности | стенд `sb-pg` (порт 5455) — см. [../guides/postgresql-setup.md](../guides/postgresql-setup.md) |

Функциональная корректность импорта/экспорта/PG проверяется автотестами (см.
[automated-tests.md](automated-tests.md)); здесь только скорость.
