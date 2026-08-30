# Tests/

Команды запуска тестов — в корневом [README.md → «Тесты»](../README.md#тесты).
Обзор и принципы — [docs/developer/testing/overview.md](../docs/developer/testing/overview.md).

Кратко (проекты этой папки):

| Проект | Что это | Запуск |
|--------|---------|--------|
| `Unit` | автотесты ядра, быстрый гейт | `dotnet test Tests/Unit` |
| `Integration` | автотесты ядра на SQLite + PostgreSQL | `dotnet test Tests/Integration` |
| `Extensions/*` | тесты расширений, по проекту на расширение ([README](Extensions/README.md)) | `./Tests/run-extensions.sh` |
| `Benchmarks` | перф-замеры | `dotnet run -c Release --project Tests/Benchmarks -- <команда>` |
| `DbGenerator` | генератор большого стенда | `dotnet run -c Release --project Tests/DbGenerator` |
| `MultiProcess` | многопроцессные/ручные проверки | `dotnet run -c Release --project Tests/MultiProcess -- <режим>` |
| `MarkupLayout` | раскладка markdown под настоящим шрифтом | `dotnet run --project Tests/MarkupLayout -f net10.0` |

`MarkupLayout` стоит отдельно от `Unit`, потому что ему нужен `Avalonia.Headless`, а `Unit`
объявлен без внешних зависимостей. Проверяет то, чего не видно без раскладки: что абзац не теряет
перенесенные строки и что просвет между блоками равен пустым строкам исходника. Обе эти ошибки уже
были — прогонять после любой правки `Controls/Markup`.

Прогнать автотесты ядра: `./Tests/run-all.sh`; вместе с расширениями — `./Tests/run-all.sh --with-extensions`.

Тесты расширений живут отдельно намеренно: расширение удаляется вместе со своей папкой тестов, и
тесты ядра о расширениях не знают — см.
[docs/developer/testing/extension-tests.md](../docs/developer/testing/extension-tests.md).
