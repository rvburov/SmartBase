#!/usr/bin/env python3
"""Первые ошибки TRX в публичных GitHub annotations; исходный статус job не меняется."""
import sys
import re
import xml.etree.ElementTree as ET

MAX_FAILURES = 10
MAX_MESSAGE = 1500
MAX_NAME = 500


def escape(value, property_value=False):
    value = value.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
    if property_value:
        value = value.replace(":", "%3A").replace(",", "%2C")
    return value


def stack_excerpt(trace):
    frames = list(dict.fromkeys(line.strip() for line in trace.splitlines() if line.strip()))
    def priority(line):
        if re.search(r"\bSmartBase(?:\.|[\\/])", line):
            return 0
        if re.search(r"\.cs:\s*(?:(?:line|строка)\s*)?\d+", line):
            return 1
        return 2
    frames.sort(key=priority)  # Сохраняем порядок внутри каждой группы.
    # Начало кадра показывает метод, конец — путь и строку исходника.
    return "\n".join(line if len(line) <= 190 else line[:80] + "..." + line[-107:]
                     for line in frames[:3])


def annotations(paths):
    count = 0
    for path in paths:
        try:
            root = ET.parse(path).getroot()
        except FileNotFoundError:
            continue  # Предыдущий шаг мог упасть до запуска тестов.
        except (OSError, ET.ParseError):
            yield "::warning title=TRX::Не удалось прочитать отчет тестов"
            continue
        for result in root.findall(".//{*}UnitTestResult"):
            if result.get("outcome") not in {"Failed", "Error", "Timeout", "Aborted", "NotRunnable"}:
                continue
            name = result.get("testName", "Неизвестный тест")[:MAX_NAME]
            message = result.findtext("{*}Output/{*}ErrorInfo/{*}Message") or result.get("outcome", "Failed")
            trace = stack_excerpt(result.findtext("{*}Output/{*}ErrorInfo/{*}StackTrace") or "")
            suffix = "\nStack:\n" + trace if trace else ""
            prefix = f"{name}: "
            body = prefix + message[:MAX_MESSAGE - len(prefix) - len(suffix)] + suffix
            yield f"::error title={escape(name, True)}::{escape(body)}"
            count += 1
            if count >= MAX_FAILURES:
                return


if __name__ == "__main__":
    # Windows pipe может унаследовать cp1252; GitHub workflow commands читаются как UTF-8.
    sys.stdout.reconfigure(encoding="utf-8")
    for annotation in annotations(sys.argv[1:]):
        print(annotation)
