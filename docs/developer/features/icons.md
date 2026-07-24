# Иконка приложения

Всё, что относится к иконке, лежит в **одной папке** `Assets/Branding/`.
Каждая ОС берёт иконку из своего файла и формата, но все они делаются из
одного мастера — `icon.png`.

## Папка Assets/Branding/

| Файл | Формат | Для чего |
|---|---|---|
| `icon.png` | PNG-32 (RGBA), мастер 1024×1024 (мин. 512) | исходник для генерации `.ico`/`.icns` + иконка меню на Linux |
| `icon.ico` | ICO, слои 16/24/32/48/64/128/256 | иконка окна (Windows/Linux) **и** иконка Windows `.exe` |
| `icon.icns` | ICNS | иконка macOS-бандла `.app` |
| `Info.plist` | — | метаданные macOS `.app` (ссылается на `icon.icns`) |
| `SmartBase.desktop` | — | пункт меню приложения для Linux |

`icon.ico` и `icon.icns` **генерируются из `icon.png`** — править их вручную не нужно.

## Как поменять иконку

Подготовьте квадратный мастер: **PNG-32 (RGBA), прозрачный фон, 1024×1024**
(минимум 512×512, иначе retina-слот macOS будет нечётким). Помните, что та же
картинка показывается и на 16px — тонкие линии и мелкие детали там сливаются в
кашу, поэтому лучше простой силуэт с толстыми штрихами.

**1. Замените мастер** `Assets/Branding/icon.png`.

**2. Перегенерируйте `icon.ico` и `icon.icns` из мастера:**

- **на Windows** — через ImageMagick (`winget install ImageMagick.ImageMagick`):
  ```powershell
  cd Assets/Branding
  magick icon.png -define icon:auto-resize=256,128,64,48,32,24,16 icon.ico
  magick icon.png icon.icns
  ```
- **на macOS** — штатным скриптом (использует `sips`, `iconutil`, `python3`):
  ```bash
  Scripts/generate-icons.sh
  ```
  > ⚠️ `generate-icons.sh` работает **только на macOS**. На Windows запускайте
  > команды ImageMagick выше.

**3. Если правите иконку только под Windows** — достаточно заменить один
`icon.ico` (это сразу и значок окна, и значок `.exe`). Мастер `.png` и `.icns`
нужны лишь для сборок под Linux/macOS.

## Как подключено

- **Окно (Windows/Linux):** глобальный стиль `Window` в [App.axaml](../../../App.axaml)
  задаёт `Icon` = `Assets/Branding/icon.ico` для всех окон, включая будущие диалоги.
  Указан именно `.ico`, а не `.png`: на Windows backend разбирает слои ICO и отдаёт
  Windows готовый кадр нужного размера (чётко в заголовке окна и таскбаре), а не
  масштабирует 512px PNG на лету. На Linux Avalonia декодирует `.ico` в bitmap для
  значка окна.
- **Windows `.exe`:** `<ApplicationIcon>` = `icon.ico` в [SmartBase.csproj](../../../SmartBase.csproj) —
  значок в Проводнике, ярлыке и закрепе на таскбаре.
- **macOS Dock/Finder/Launchpad:** только через `.app`-бандл (`Info.plist` →
  `CFBundleIconFile` → `icon.icns`). Свойство `Window.Icon` на маке иконку окна
  не показывает.
- **Linux меню/лаунчер:** `SmartBase.desktop` + `icon.png`, ставятся через
  `Scripts/linux-install.sh`.

## Сборка под каждую ОС

```bash
Scripts/generate-icons.sh                        # (только macOS) пересобрать .ico/.icns из icon.png

Scripts/macos-build.sh osx-arm64                 # macOS → dist/Version <X.Y.Z>/osx-arm64/SmartBase.app
open "dist/Version <X.Y.Z>/osx-arm64/SmartBase.app"

Scripts/windows-build.sh                          # Windows → dist/Version <X.Y.Z>/win-x64/SmartBase.exe (иконка вшита)

Scripts/linux-build.sh                            # Linux  → dist/Version <X.Y.Z>/linux-x64/SmartBase
Scripts/linux-install.sh                          # Linux: иконку/ярлык ставить на самой Linux-машине
```

`dist/` — артефакт сборки, в репозиторий не попадает (см. `.gitignore`).

## Скрипты (Scripts/)

- `windows-build.sh` — собирает `dist/<rid>/SmartBase.exe` (иконка вшита в `.exe`).
- `linux-build.sh` — собирает `dist/<rid>/SmartBase` (бит запуска выставлен).
- `macos-build.sh` — собирает `dist/<rid>/SmartBase.app` с иконкой.
- `linux-install.sh` — ставит иконку и ярлык в меню Linux.
- `generate-icons.sh` — генерирует `icon.ico` и `icon.icns` из `icon.png`
  (**только macOS**: нужны штатные `sips`, `iconutil`, `python3`).
