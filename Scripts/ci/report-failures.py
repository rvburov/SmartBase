#!/usr/bin/env python3
"""Первые ошибки TRX в публичных GitHub annotations; исходный статус job не меняется."""
import sys
import xml.etree.ElementTree as ET

MAX_FAILURES = 10
MAX_MESSAGE = 1500
MAX_NAME = 500


def escape(value, property_value=False):
    value = value.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
    if property_value:
        value = value.replace(":", "%3A").replace(",", "%2C")
    return value


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
            body = f"{name}: {message}"[:MAX_MESSAGE]
            yield f"::error title={escape(name, True)}::{escape(body)}"
            count += 1
            if count >= MAX_FAILURES:
                return


if __name__ == "__main__":
    for annotation in annotations(sys.argv[1:]):
        print(annotation)
