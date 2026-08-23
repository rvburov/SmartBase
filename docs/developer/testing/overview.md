# Тестирование

Единая точка входа по тестам и тестовым инструментам проекта. Тесты разделены по
**характеру** (скорость/зависимости), инструменты — по **роли**.

## Документы раздела

- [automated-tests.md](automated-tests.md) — автотесты xUnit: `Unit` (быстрый гейт) и
  `Integration` (temp SQLite + PostgreSQL), структура, принципы обвязки, запуск.
- [extension-tests.md](extension-tests.md) — тесты расширений: отдельное дерево
  `Tests/Extensions/*`, по проекту на расширение, изолированы от тестов ядра.
- [benchmarks.md](benchmarks.md) — замеры производительности (`Tests/Benchmarks`).
- [multi-process.md](multi-process.md) — многопроцессные/интерактивные проверки (`Tests/MultiProcess`).
- [test-database.md](test-database.md) — генератор большого стенда (`Tests/DbGenerator`).
- Ручные QA-чеклисты фич — в [../checklists/](../qa-checklists/).
- Стенд PostgreSQL (установка сервера) — в [../guides/postgresql-setup.md](../guides/postgresql-setup.md).

## Карта проектов Tests/

| Проект | Тип | Назначение | Запуск |
|--------|-----|-----------|--------|
| **Unit** | xUnit (быстрый гейт) | Чистая логика ядра без БД/IO: даты, валидаторы, планировщики, права | `dotnet test Tests/Unit` |
| **Integration** | xUnit (медленнее) | Репозитории на temp SQLite + PostgreSQL: CRUD, keyset, календарь, cross-move, импорт/экспорт, гонки | `dotnet test Tests/Integration` |
| **Extensions/&lt;Имя&gt;** | xUnit (по проекту на расширение) | Логика и сквозные прогоны расширений; отдельные сборки, от тестов ядра не зависят | `./Tests/run-extensions.sh` |
| **Benchmarks** | консоль (замеры) | Производительность: сайдбар, импорт/экспорт больших объемов, нагрузка PG | `dotnet run -c Release --project Tests/Benchmarks -- <команда>` |
| **DbGenerator** | консоль (инструмент) | Генерация большого стенда (по умолчанию 20 млн записей) | `dotnet run -c Release --project Tests/DbGenerator` |
| **MultiProcess** | консоль (многопроцесс) | Ручные многопроцессные сценарии (два процесса, сетевые прагмы, живая синхронизация) | `dotnet run -c Release --project Tests/MultiProcess -- <режим>` |

## Быстрый старт

```bash
dotnet test Tests/Unit                 # быстрый гейт (мс), без внешних зависимостей
dotnet test Tests/Integration          # SQLite + PostgreSQL (если поднят стенд sb-pg)
dotnet test SmartBase.sln              # оба набора ядра
./Tests/run-all.sh                     # оба набора ядра
./Tests/run-extensions.sh              # все наборы расширений
./Tests/run-extensions.sh PdfRename    # один набор расширения
./Tests/run-all.sh --with-extensions   # ядро + расширения
./Tests/run-all.sh --with-bench        # + быстрый прогон бенчмарков
```

## Почему тесты разделены на Unit и Integration

Граница проходит по вопросу «нужна ли тесту БД?»:
- **Unit** — миллисекунды, ноль внешних зависимостей → обязательный гейт на каждый коммит.
- **Integration** — миграции, файлы БД, стенд PostgreSQL → полная/ночная стадия CI.

Это самая устойчивая граница при росте: быстрый сигнал не платит за медленные интеграционные тесты.

Оба набора гоняются ПОСЛЕДОВАТЕЛЬНО (`DisableTestParallelization` в `AssemblyInfo.cs` каждого):
Integration — из-за процесс-глобального `SqliteConnection.ClearAllPools()`, Unit — из-за статики
`AppSettings.AttachStore`. Причины расписаны в самих файлах.

## Почему тесты расширений вынесены отдельно

Расширение — отделяемый блок: удаляется вместе со своей папкой, ядро не трогается. Тесты внутри
проектов ядра ломали это свойство — `using SmartBase.Extensions.<Имя>` в `Tests/Unit` означал, что
удаление расширения роняет сборку тестов программы.

Поэтому у расширений свое дерево `Tests/Extensions/<Имя>/` с проектом на расширение: свой процесс
тест-хоста, свой прогон, ноль связей с наборами ядра (сторожится шагом CI). Подробности —
[extension-tests.md](extension-tests.md).
