#!/usr/bin/env bash
# Проверки маршрутизации и обязательных гейтов. Без .NET, сети и изменений веток.
set -euo pipefail
cd "$(dirname "$0")/../.."
ci_here=Scripts/ci
ci_workflow=.github/workflows/build.yml
ci_passed=0
ok() { printf 'OK: %s\n' "$1"; ci_passed=$((ci_passed + 1)); }
fail() { printf 'ERROR: %s\n' "$1" >&2; exit 1; }

for script in "$ci_here"/*.sh; do bash -n "$script"; done
ok 'синтаксис bash'

expect_scope() {
  local event=$1 ref=$2 base=$3 full=$4 expected=$5 actual
  actual="$(GITHUB_EVENT_NAME="$event" GITHUB_REF="$ref" GITHUB_BASE_REF="$base" WORKFLOW_FULL="$full" \
    bash "$ci_here/select-scope.sh")"
  [[ "$actual" == "profile=$expected"$'\n'"full=$([[ "$expected" == full ]] && echo true || echo false)" ]] \
    || fail "неверный профиль: $event $ref"
}
expect_scope push refs/heads/work '' false quick
expect_scope push refs/heads/worktree-fix '' false quick
expect_scope push refs/heads/ci '' false full
expect_scope pull_request refs/pull/42/merge main false quick
expect_scope workflow_dispatch refs/heads/main '' false full
# Reusable workflow видит исходное событие release: push тега, а не workflow_call.
expect_scope push refs/tags/v1.2.3 '' true full
for ref in refs/heads/main refs/tags/v1.2.3; do
  if GITHUB_EVENT_NAME=push GITHUB_REF="$ref" WORKFLOW_FULL=false bash "$ci_here/select-scope.sh" >/dev/null 2>&1; then
    fail "самостоятельный push $ref не должен запускать проверки"
  fi
done
if GITHUB_EVENT_NAME=pull_request GITHUB_BASE_REF=other WORKFLOW_FULL=false bash "$ci_here/select-scope.sh" >/dev/null 2>&1; then
  fail 'PR не в main принят за разрешенный запуск'
fi
ok 'quick/full, ручной запуск и exact-tag reuse'

check_results() {
  CI_PROFILE="$1" CI_SCOPE_RESULT=success CI_QUICK_RESULT="$2" CI_MATRIX_RESULT="$3" \
    CI_CLOSED_RESULT="$4" CI_POSTGRES_RESULT="$5" CI_TOOLS_RESULT="$6" bash "$ci_here/check-results.sh" >/dev/null 2>&1
}
check_results quick success skipped skipped skipped skipped
check_results full success success success success success
if check_results full success success success skipped success; then fail 'пропуск PG не остановил полный гейт'; fi
if check_results full success cancelled success success success; then fail 'отмена матрицы не остановила полный гейт'; fi
if check_results quick skipped skipped skipped skipped skipped; then fail 'пропуск quick не остановил гейт'; fi
if check_results unknown success skipped skipped skipped skipped; then fail 'неизвестный профиль принят'; fi
if CI_REQUIRE_FULL=true check_results quick success skipped skipped skipped skipped; then
  fail 'запрошенный полный профиль заменился быстрым'
fi
ok 'обязательный итог не принимает пропуск/отмену гейтов'

if command -v actionlint >/dev/null 2>&1; then
  actionlint "$ci_workflow"
fi
if python3 -c 'import yaml' >/dev/null 2>&1; then
  python3 - "$ci_workflow" <<'PY'
import sys, yaml
with open(sys.argv[1], encoding="utf-8") as source:
    data = yaml.load(source, Loader=yaml.BaseLoader)
assert data["name"] == "Проверки"
assert data["on"]["push"]["branches-ignore"] == ["main"]
assert data["on"]["pull_request"]["branches"] == ["main"]
assert "workflow_dispatch" in data["on"] and "workflow_call" in data["on"]
assert data["on"]["workflow_call"]["inputs"]["full"]["type"] == "boolean"
assert data["on"]["workflow_call"]["inputs"]["ref"]["type"] == "string"
jobs = data["jobs"]
assert set(jobs) == {"scope", "agent-tests", "test", "closed-loop", "postgres", "tools", "checks"}
assert jobs["checks"]["if"] == "always()"
assert set(jobs["checks"]["needs"]) == set(jobs) - {"checks"}
for name in ("test", "closed-loop", "postgres", "tools"):
    assert jobs[name]["needs"] == "scope"
    assert jobs[name]["if"] == "needs.scope.outputs.full == 'true'"
assert {m["os"] for m in jobs["test"]["strategy"]["matrix"]["include"]} == {
    "ubuntu-latest", "windows-latest", "macos-latest", "macos-15-intel"}
assert jobs["postgres"]["services"]["postgres"]["image"] == "postgres:16"
for name, results in (("agent-tests", "quick-results"), ("test", "matrix-results")):
    steps = jobs[name]["steps"]
    python_setup = next(s for s in steps if s.get("uses") == "actions/setup-python@v6")
    assert python_setup["with"]["python-version"] == "3.13"
    assert steps.index(python_setup) > next(i for i, s in enumerate(steps) if s.get("uses") == "actions/setup-dotnet@v5")
    unit = next(s["run"] for s in steps if s.get("run", "").startswith("dotnet test Tests/Unit "))
    extensions = next(s["run"] for s in steps if s.get("run", "").startswith("bash Tests/run-extensions.sh "))
    assert "--logger 'trx;LogFileName=unit.trx'" in unit
    assert f'--results-directory "$RUNNER_TEMP/{results}"' in unit
    assert '--logger trx --results-directory "$RUNNER_TEMP/extensions-results"' in extensions
    assert "LogFileName" not in extensions  # Несколько проектов не пишут в один файл.
    reporter = next(s for s in steps if "python Scripts/ci/report-failures.py" in s.get("run", ""))
    assert steps.index(python_setup) < steps.index(reporter)
    assert reporter["if"] == "failure()"
    assert "shopt -s nullglob" in reporter["run"]
    assert f'"$RUNNER_TEMP/{results}/"*.trx' in reporter["run"]
    assert '"$RUNNER_TEMP/extensions-results/"*.trx' in reporter["run"]
    assert 'python Scripts/ci/report-failures.py "${reports[@]}"' in reporter["run"]
for job in jobs.values():
    for step in job.get("steps", []):
        if step.get("uses", "").startswith("actions/checkout@"):
            assert step["with"]["ref"] == "${{ inputs.ref || github.sha }}"
PY
else
  echo 'SKIP: структурная проверка YAML требует PyYAML'
fi
ok 'workflow и общий набор гейтов'

[[ ! -e .github/workflows/agent-tests.yml ]] || fail 'старый workflow дублирует общий'
ci_status=0
grep -nE 'contents: write|git/refs/heads|git push.*--delete|github-release/|release\.sh' "$ci_workflow" || ci_status=$?
case "$ci_status" in
  0) fail 'workflow проверок меняет ветки или запускает публикацию' ;;
  1) ;;
  *) fail 'не удалось проверить отсутствие публикации/удаления веток' ;;
esac
for target in SmartBase.csproj Tests/Unit Tests/Integration Tests/DocReader Tests/RecordPage Tests/Benchmarks Tests/DbGenerator Tests/MultiProcess Tests/run-extensions.sh; do
  [[ -e "$target" ]] || fail "нет цели $target"
done
ok 'отдельной публикации и удаления веток нет; цели существуют'

# TRX guard проверяется на маленьких XML-фикстурах: нет доступа к настоящим тестовым БД.
python3 -B - "$ci_here" <<'PY'
import contextlib, importlib.util, io, pathlib, sys, tempfile
folder = pathlib.Path(sys.argv[1])
spec = importlib.util.spec_from_file_location("check_trx", folder / "check-trx.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
def report(names):
    return '<TestRun><Results>' + ''.join(f'<UnitTestResult testName="{n}" outcome="{o}"/>' for n,o in names) + '</Results></TestRun>'
good = [("X.LinuxNotificationsTests.Binding", "Passed"), ("X.PgProviderTests.Crud", "Passed"), ("X.PgBackupTests.Dump", "Passed")]
with tempfile.TemporaryDirectory(prefix="smartbase-ci-") as temporary:
    path = pathlib.Path(temporary) / "report.trx"
    with contextlib.redirect_stdout(io.StringIO()):
        path.write_text(report(good + [(next(iter(module.BASELINE_SKIPS)), "NotExecuted")]))
        module.check(path, True)
        for bad in ([], good + [("X.NewPgTests.NewCase", "NotExecuted")], good[:-1], good + [("X.Case", "Failed")]):
            path.write_text(report(bad))
            try:
                module.check(path, True)
            except ValueError:
                pass
            else:
                raise AssertionError("TRX guard accepted missing/failed/skipped tests")
PY
ok 'TRX не принимает пустой отчет, новый skip и отсутствие native-backup тестов'

python3 -B - "$ci_here" <<'PY'
import importlib.util, os, pathlib, subprocess, sys, tempfile, xml.etree.ElementTree as ET
spec = importlib.util.spec_from_file_location("report_failures", pathlib.Path(sys.argv[1]) / "report-failures.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
assert module.escape("%,:\r\n", True) == "%25%2C%3A%0D%0A"
assert module.escape("%,:\r\n") == "%25,:%0D%0A"
with tempfile.TemporaryDirectory(prefix="smartbase-ci-errors-") as temporary:
    path = pathlib.Path(temporary) / "failed.trx"
    root = ET.Element("{urn:trx}TestRun")
    results = ET.SubElement(root, "{urn:trx}Results")
    ET.SubElement(results, "{urn:trx}UnitTestResult", testName="Passed", outcome="Passed")
    ET.SubElement(results, "{urn:trx}UnitTestResult", testName="Skipped", outcome="NotExecuted")
    for index in range(12):
        result = ET.SubElement(results, "{urn:trx}UnitTestResult", testName=f"Namespace.Case{index},:\r\n%", outcome="Failed")
        error = ET.SubElement(ET.SubElement(result, "{urn:trx}Output"), "{urn:trx}ErrorInfo")
        ET.SubElement(error, "{urn:trx}Message").text = "failure%\r\n" + "x" * 2000
    ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)
    lines = list(module.annotations([path, path]))
    assert len(lines) == 10
    assert all(line.startswith("::error title=Namespace.Case") for line in lines)
    assert all("%2C%3A%0D%0A%25::" in line for line in lines)
    assert all("\r" not in line and "\n" not in line for line in lines)
    assert all(len(line.split("::", 2)[2].replace("%25", "%").replace("%0D", "\r").replace("%0A", "\n")) <= 1500 for line in lines)
    assert list(module.annotations([pathlib.Path(temporary) / "absent.trx"])) == []
    unicode_path = pathlib.Path(temporary) / "cyrillic.trx"
    unicode_root = ET.Element("TestRun")
    result = ET.SubElement(ET.SubElement(unicode_root, "Results"), "UnitTestResult",
                           testName="Namespace.Русский_тест", outcome="Failed")
    error = ET.SubElement(ET.SubElement(result, "Output"), "ErrorInfo")
    ET.SubElement(error, "Message").text = "Ошибка проверки: русское сообщение"
    ET.ElementTree(unicode_root).write(unicode_path, encoding="utf-8", xml_declaration=True)
    cli = subprocess.run([sys.executable, "-B", str(spec.origin), str(unicode_path)],
                         env={**os.environ, "PYTHONIOENCODING": "cp1252"}, capture_output=True)
    assert cli.returncode == 0, cli.stderr.decode("ascii", "backslashreplace")
    assert cli.stderr == b""
    assert cli.stdout.decode("utf-8").splitlines() == list(module.annotations([unicode_path]))
    path.write_text("broken", encoding="utf-8")
    assert list(module.annotations([path])) == ["::warning title=TRX::Не удалось прочитать отчет тестов"]
PY
ok 'публичные annotations ограничены, экранированы и содержат только ошибки TRX'
printf 'Проверок модуля CI пройдено: %s\n' "$ci_passed"
