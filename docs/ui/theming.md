# Оформление (темы, цвета, типографика)

## Назначение

Где задаются цвета, шрифты и стили контролов, и как они подключаются.

## Где в коде

- Ресурсы и стили: [Assets/Styles/](../../Assets/Styles/) — `Colors.axaml`, `Typography.axaml`, `Controls.axaml`
- Подключение: [App.axaml](../../App.axaml)
- Масштабирование интерфейса: [Utils/Scaling.cs](../../Utils/Scaling.cs)

## Как подключено

В [App.axaml](../../App.axaml):

- `Application.Resources` подключает `Colors.axaml` (`ResourceInclude`).
- `Application.Styles` подключает базовую тему `FluentTheme`, стили `DataGrid`,
  затем `Typography.axaml`, `Controls.axaml` и стиль виджета `AppCheckBox`.
- Там же — глобальный стиль `Window` с иконкой приложения (см. [../features/icons.md](../features/icons.md)).

Тема приложения — светлая (`RequestedThemeVariant="Light"`).

## Ресурсы

- **Colors.axaml** — кисти приложения. Используются по имени как `StaticResource`,
  например `BrushBg` (фон окна), `BrushPrimaryText`, `BrushSecondaryText`.
  В календаре часть цветов задана прямо в коде (например, акцент `#FF3B30`,
  вторичный `#8E8E93`); единый «цвет по умолчанию» событий — в
  [EventColors](../../Core/Models/EventColors.cs).
- **Typography.axaml** — размеры/начертания шрифтов, например `FontSizeBase`.
- **Controls.axaml** — стили стандартных и кастомных контролов.

## Масштаб интерфейса

Масштаб (1.0–1.3, шаг 0.1) хранится в настройках (`AppSettings.LoadScale/SaveScale`)
и применяется через [Utils/Scaling.cs](../../Utils/Scaling.cs). Диапазон и шаг —
константы в [AppSettings](../../Utils/AppSettings.cs).

## Связанные документы

[[widgets]], [[dialogs]], [[../features/settings]]
