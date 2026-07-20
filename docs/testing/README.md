# Тестирование

Единая точка входа по тестам и тестовым инструментам проекта. Тесты разделены по
**характеру** (скорость/зависимости), инструменты — по **роли**.

## Документы раздела

- [automated-tests.md](automated-tests.md) — автотесты xUnit: `Unit` (быстрый гейт) и
  `Integration` (temp SQLite + PostgreSQL), структура, принципы обвязки, запуск.
- [benchmarks.md](benchmarks.md) — замеры производительности (`Tests/Benchmarks`).
- [multi-process.md](multi-process.md) — многопроцессные/интерактивные проверки (`Tests/MultiProcess`).
- [test-database.md](test-database.md) — генератор большого стенда (`Tests/DbGenerator`).
- Ручные QA-чеклисты фич — в [../checklists/](../checklists/).
- Стенд PostgreSQL (установка сервера) — в [../guides/postgresql-setup.md](../guides/postgresql-setup.md).

## Карта проектов Tests/

| Проект | Тип | Назначение | Запуск |
|--------|-----|-----------|--------|
| **Unit** | xUnit (быстрый гейт) | Чистая логика без БД/IO: даты, валидаторы, планировщики, права | `dotnet test Tests/Unit` |
| **Integration** | xUnit (медленнее) | Репозитории на temp SQLite + PostgreSQL: CRUD, keyset, календарь, cross-move, импорт/экспорт, гонки | `dotnet test Tests/Integration` |
| **Benchmarks** | консоль (замеры) | Производительность: сайдбар, импорт/экспорт больших объёмов, нагрузка PG | `dotnet run -c Release --project Tests/Benchmarks -- <команда>` |
| **DbGenerator** | консоль (инструмент) | Генерация большого стенда (по умолчанию 20 млн записей) | `dotnet run -c Release --project Tests/DbGenerator` |
| **MultiProcess** | консоль (многопроцесс) | Ручные многопроцессные сценарии (два процесса, сетевые прагмы, живая синхронизация) | `dotnet run -c Release --project Tests/MultiProcess -- <режим>` |

## Быстрый старт

```bash
dotnet test Tests/Unit             # быстрый гейт (мс), без внешних зависимостей
dotnet test Tests/Integration      # SQLite + PostgreSQL (если поднят стенд sb-pg)
dotnet test SmartBase.sln          # оба набора
./Tests/run-all.sh                 # оба набора
./Tests/run-all.sh --with-bench    # + быстрый прогон бенчмарков
```

## Почему тесты разделены на Unit и Integration

Граница проходит по вопросу «нужна ли тесту БД?»:
- **Unit** — миллисекунды, ноль внешних зависимостей, параллельные → обязательный гейт на каждый коммит.
- **Integration** — миграции, файлы БД, стенд PostgreSQL, прогон последовательный → полная/ночная стадия CI.

Это самая устойчивая граница при росте: быстрый сигнал не платит за медленные интеграционные тесты.
