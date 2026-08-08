#!/bin/bash
#
# Тело релиза для GitHub. Печатает готовый markdown в stdout.
#
#   Scripts/github-release/release-notes.sh 1.0.4              # посмотреть, что получится
#   Scripts/github-release/release-notes.sh 1.0.4 > notes.md   # так вызывает workflow
#
# Состав: описание версии из CHANGELOG.md → как поставить → предупреждение о неподписанных
# бинарниках → как обновить сетевую папку → контрольные суммы → полный список коммитов
# под спойлером.
#
# Список коммитов берётся у GitHub API (эндпоинт releases/generate-notes), а не флагом
# `gh release create --generate-notes`: так текст собирается здесь и целиком виден в этом
# файле, а не зависит от того, как конкретная версия gh склеит его со своим шаблоном.
# Без GH_TOKEN блок просто опускается — скрипт остаётся запускаемым локально.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
CHANGELOG="$ROOT/CHANGELOG.md"
VERSION="${1:-}"
REPO="${GITHUB_REPOSITORY:-rvburov/SmartBase}"

[ -n "$VERSION" ] || { echo "Использование: release-notes.sh <версия>" >&2; exit 1; }
[ -f "$CHANGELOG" ] || { echo "Нет файла CHANGELOG.md" >&2; exit 1; }

# ── Что нового: секция «## [версия]» до следующего «## [» ─────────────────────
BODY="$(awk -v v="${VERSION//./\\.}" '
  $0 ~ "^## \\[" v "\\]" { found = 1; next }
  found && /^## \[/      { exit }
  found                  { print }
' "$CHANGELOG")"

# Обрезать пустые строки в начале секции; в конце их уже съела подстановка $(...).
BODY="$(printf '%s\n' "$BODY" | sed -e '/./,$!d')"

if [ -z "$BODY" ]; then
  echo "В CHANGELOG.md нет непустой секции «## [$VERSION]»" >&2
  exit 1
fi

echo "## Что нового"
echo
printf '%s\n' "$BODY"
echo
cat <<EOF
## Установка

| ОС | Файл | Что делать |
|---|---|---|
| Windows | \`SmartBase-$VERSION-win-x64.zip\` | распаковать, запустить \`SmartBase-Setup.exe\` |
| macOS, Apple Silicon | \`SmartBase-$VERSION-osx-arm64.zip\` | распаковать, запустить \`SmartBase-Setup.app\` |
| macOS, Intel | \`SmartBase-$VERSION-osx-x64.zip\` | то же |
| Linux | \`SmartBase-$VERSION-linux-x64.tar.gz\` | \`tar -xzf …\`, затем \`./SmartBase-Setup\` |

.NET ставить не нужно: сборки self-contained, среда выполнения внутри.
В каждом архиве, кроме программы, лежат \`LICENSE\` и \`THIRD-PARTY-NOTICES.txt\`.

> **Бинарники не подписаны цифровым сертификатом.**
> **Windows:** SmartScreen покажет синее окно → «Подробнее» → «Выполнить в любом случае».
> **macOS:** система пометит приложение как загруженное из интернета. Снять пометку:
> \`xattr -dr com.apple.quarantine /путь/к/SmartBase-Setup.app\` — либо открыть
> «Системные настройки → Конфиденциальность и безопасность» и нажать «Всё равно открыть».

## Автообновление

Автообновление внутри программы берёт файлы из сетевой или файловой папки, а не с GitHub.
Чтобы обновить пользователей, распакуйте архив в эту папку так, чтобы \`SmartBase-Setup*\`
оказался **в её корне**. Подробности — \`docs/developer/guides/auto-update.md\`.

## Контрольные суммы

\`SHA256SUMS.txt\` во вложениях. Проверка: \`sha256sum -c SHA256SUMS.txt\` (Linux, macOS)
или \`Get-FileHash файл\` (Windows).
EOF

# ── Полный список коммитов ────────────────────────────────────────────────────
# GitHub сам находит предыдущий тег; для самого первого релиза previous_tag_name не
# передаётся — эндпоинт корректно отдаёт всё от начала истории.
if [ -n "${GH_TOKEN:-${GITHUB_TOKEN:-}}" ] && command -v gh >/dev/null 2>&1; then
  COMMITS="$(gh api --method POST "repos/$REPO/releases/generate-notes" \
    -f tag_name="v$VERSION" --jq .body 2>/dev/null)"
  if [ -n "$COMMITS" ]; then
    echo
    echo "<details><summary>Все изменения</summary>"
    echo
    printf '%s\n' "$COMMITS"
    echo
    echo "</details>"
  fi
fi
