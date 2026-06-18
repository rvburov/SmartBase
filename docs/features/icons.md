# Иконка приложения

Всё, что относится к иконке, лежит в **одной папке** `Assets/Branding/`.
Каждая ОС берёт иконку из своего источника и формата, но генерируются они
из одного мастер-файла.

## Папка Assets/Branding/

| Файл | Для чего |
|---|---|
| `icon.png` | мастер (512×512). Иконка окна на Windows/Linux + иконка меню на Linux |
| `icon.ico` | иконка Windows `.exe` (генерируется из `icon.png`) |
| `icon.icns` | иконка macOS-бандла (генерируется из `icon.png`) |
| `Info.plist` | метаданные macOS `.app` (ссылается на иконку) |
| `DataBase.desktop` | пункт меню приложения для Linux |

`icon.ico` и `icon.icns` **генерируются** — править их вручную не нужно.
Чтобы сменить иконку: замените `icon.png` и запустите `Scripts/generate-icons.sh`.
Идеальный мастер — 1024×1024 (тогда и retina-слот macOS будет чётким).

## Как подключено

- **Окно (Windows/Linux):** глобальный стиль `Window` в [App.axaml](../../App.axaml)
  задаёт `Icon` = `Assets/Branding/icon.png` для всех окон, включая будущие диалоги.
- **Windows `.exe`:** `<ApplicationIcon>` в [DataBase.csproj](../../DataBase.csproj).
- **macOS Dock/Finder/Launchpad:** только через `.app`-бандл (`Info.plist` + `icon.icns`).
  Свойство `Window.Icon` на маке иконку не показывает.

## Сборка под каждую ОС

```bash
Scripts/generate-icons.sh                        # пересобрать .ico/.icns из icon.png

Scripts/build-macos-app.sh osx-arm64 Release     # macOS → dist/osx-arm64/DataBase.app
open dist/osx-arm64/DataBase.app

dotnet publish -c Release -r win-x64 --self-contained   # Windows: иконка вшита в .exe

Scripts/install-linux.sh                          # Linux: запускать на самой Linux-машине
```

`dist/` — артефакт сборки, в репозиторий не попадает (см. `.gitignore`).

## Скрипты (Scripts/)

- `generate-icons.sh` — генерирует `icon.ico` и `icon.icns` из `icon.png`
  (нужны только штатные `sips`, `iconutil`, `python3`).
- `build-macos-app.sh` — собирает `dist/<rid>/DataBase.app` с иконкой.
- `install-linux.sh` — ставит иконку и ярлык в меню Linux.
