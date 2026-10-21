#!/usr/bin/env python3
"""Независимая от языка консоли проверка, что PG/D-Bus тесты действительно выполнялись."""
import argparse
import sys
import xml.etree.ElementTree as ET

# Эти проверки на Linux не выполняются по известной причине. Новые пропуски не разрешены.
BASELINE_SKIPS = {
    "SmartBase.Tests.Integration.PgLocalInstallTests.Backup_takes_pg_dump_from_the_newest_install",
    "SmartBase.Tests.Integration.PgLocalInstallTests.Server_buttons_drive_the_service_of_the_found_install",
    "SmartBase.Tests.Integration.PgLocalInstallTests.Every_install_recorded_by_the_installer_is_found",
    "SmartBase.Tests.Integration.UserValueTests.Author_column_is_filterable_sortable_and_exported",
    "SmartBase.Tests.Integration.TaskRecordsTests.Task_creation_fills_other_user_fields_the_same_with_and_without_due",
}


def check(path, postgres=False):
    root = ET.parse(path).getroot()
    results = root.findall(".//{*}UnitTestResult")
    if not results:
        raise ValueError("TRX не содержит результатов тестов")
    failures = []
    for result in results:
        name, outcome = result.get("testName", ""), result.get("outcome", "")
        if outcome != "Passed" and not (postgres and outcome == "NotExecuted" and name in BASELINE_SKIPS):
            failures.append(f"{name}: {outcome}")
    required = ["LinuxNotificationsTests."]
    if postgres:
        required.extend(["PgProviderTests.", "PgBackupTests."])
    for fragment in required:
        if not any(fragment in r.get("testName", "") and r.get("outcome") == "Passed" for r in results):
            failures.append(f"Нет успешно выполненного теста {fragment}")
    if failures:
        raise ValueError("\n".join(failures))
    skipped = sum(r.get("outcome") == "NotExecuted" for r in results)
    print(f"TRX: выполнено {len(results) - skipped}, известных пропусков {skipped}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report")
    parser.add_argument("--postgres", action="store_true")
    args = parser.parse_args()
    try:
        check(args.report, args.postgres)
    except (OSError, ET.ParseError, ValueError) as error:
        print(f"::error::{error}", file=sys.stderr)
        sys.exit(1)
