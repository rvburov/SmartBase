#!/bin/bash
#
# Сверка номера версии в трёх местах, где он записан:
#
#   SmartBase.csproj                            <Version>1.0.3</Version>   ← источник правды
#   README.md                                   badge/version-1.0.3-blue   ← бейдж в шапке
#   Scripts/github-release/release-history.md   ## [1.0.3] — 2026-08-07    ← секция с описанием
#
# Использование:
#
#   Scripts/github-release/check-version.sh                 # сверить три места между собой
#   Scripts/github-release/check-version.sh 1.0.4           # ...и с ожидаемой версией
#   Scripts/github-release/check-version.sh --changelog-only 1.0.4
#                                                    # только секция в release-history.md
#                                                    # (предусловие выпуска: секция пишется
#                                                    #  ДО правки версии)
#
# Один файл на три точки вызова — release.sh, job `gate` в .github/workflows/release.yml и
# release-test.sh. Если правила разложить по трём местам, они разъедутся.
#
# Код возврата: 0 — всё сходится, 1 — есть расхождения (все перечисляются, не только первое).
set -uo pipefail   # без -e: проверки должны выполниться все, а не оборваться на первой

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
CSPROJ="$ROOT/SmartBase.csproj"
README="$ROOT/README.md"
HISTORY="$ROOT/Scripts/github-release/release-history.md"

fail=0
err() { echo "  ✗ $1" >&2; fail=1; }

# Версия из csproj — источник правды. Читается тем же выражением, что в Scripts/*-build.sh.
csproj_version() {
  sed -nE 's#.*<Version>([^<]*)</Version>.*#\1#p' "$CSPROJ" | head -n1
}

# Версия из бейджа README: ![Версия](https://img.shields.io/badge/version-1.0.3-blue)
# Дефис у shields.io — разделитель полей label-message-color, поэтому [^-]* и никаких
# суффиксов вида 1.0.4-rc.1 (см. проверку формата в release.sh).
readme_version() {
  sed -nE 's#.*badge/version-([^-]*)-blue.*#\1#p' "$README" | head -n1
}

# Есть ли в release-history.md непустая секция «## [версия]». Непустая — значит между её заголовком
# и следующим «## [» есть хотя бы одна непробельная строка. Пустая секция означает, что версию
# завели, но описать забыли: релиз с пустым телом никому не нужен.
changelog_section_filled() {
  # Точки экранируются: в регулярном выражении awk «1.0.4» совпало бы и с «1x0y4».
  awk -v v="${1//./\\.}" '
    $0 ~ "^## \\[" v "\\]" { found = 1; next }
    found && /^## \[/      { exit }
    found && NF            { filled = 1; exit }
    END                    { exit !(found && filled) }
  ' "$HISTORY"
}

# ── Режим «только CHANGELOG»: предусловие выпуска ────────────────────────────
# Вызывается из release.sh ДО правки версии, когда csproj и README ещё несут старый номер,
# а секция для новой версии уже должна быть написана человеком.
if [ "${1:-}" = "--changelog-only" ]; then
  want="${2:-}"
  [ -n "$want" ] || { echo "Использование: check-version.sh --changelog-only <версия>" >&2; exit 1; }
  [ -f "$HISTORY" ] || { err "нет файла release-history.md"; exit 1; }
  changelog_section_filled "$want" \
    || err "в release-history.md нет непустой секции «## [$want]» — опишите, что изменилось"
  exit $fail
fi

# ── Обычный режим: сверка трёх мест ──────────────────────────────────────────
for f in "$CSPROJ" "$README" "$HISTORY"; do
  [ -f "$f" ] || { echo "Нет файла: $f" >&2; exit 1; }
done

CSPROJ_VER="$(csproj_version)"
README_VER="$(readme_version)"
WANT="${1:-$CSPROJ_VER}"

[ -n "$CSPROJ_VER" ] || err "не удалось прочитать <Version> из SmartBase.csproj"
[ -n "$README_VER" ] || err "не удалось прочитать бейдж версии из README.md"

[ "$CSPROJ_VER" = "$WANT" ] || err "SmartBase.csproj: $CSPROJ_VER, ожидалось $WANT"
[ "$README_VER" = "$WANT" ] || err "бейдж README.md: $README_VER, ожидалось $WANT"
changelog_section_filled "$WANT" \
  || err "release-history.md: нет непустой секции «## [$WANT]»"

if [ $fail -eq 0 ]; then
  echo "✓ версия $WANT согласована: SmartBase.csproj, README.md, release-history.md"
else
  echo "Версии разошлись. Источник правды — <Version> в SmartBase.csproj." >&2
fi
exit $fail
