# Tests/

Документация по тестам вынесена в **[docs/testing/](../docs/testing/)** —
обзор и запуск: [docs/testing/README.md](../docs/testing/README.md).

Кратко (проекты этой папки):

| Проект | Что это | Запуск |
|--------|---------|--------|
| `Unit` | автотесты, быстрый гейт | `dotnet test Tests/Unit` |
| `Integration` | автотесты на SQLite + PostgreSQL | `dotnet test Tests/Integration` |
| `Benchmarks` | перф-замеры | `dotnet run -c Release --project Tests/Benchmarks -- <команда>` |
| `DbGenerator` | генератор большого стенда | `dotnet run -c Release --project Tests/DbGenerator` |
| `MultiProcess` | многопроцессные/ручные проверки | `dotnet run -c Release --project Tests/MultiProcess -- <режим>` |

Прогнать все автотесты: `./Tests/run-all.sh`.
