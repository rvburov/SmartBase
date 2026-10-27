#!/usr/bin/env python3
"""Упаковать исходники VBA в архив для импорта модулей под Windows."""

from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile, ZipInfo


def export() -> Path:
    root = Path(__file__).resolve().parent
    files = {}
    for module in ("modProductGrouping", "modProductFormatting"):
        name = f"{module}.bas"
        text = (root / name).read_text(encoding="utf-8")
        expected = f'Attribute VB_Name = "{module}"\n'
        if not text.startswith(expected):
            raise ValueError(f"Неверное имя модуля: {name}")
        files[name] = text.replace("\n", "\r\n").encode("cp1251")
    for name in ("ThisWorkbook-menu-events.txt", "README.md"):
        files[name] = (root / name).read_text(encoding="utf-8").encode("utf-8")

    archive = root / "macros-windows.zip"
    with ZipFile(archive, "w", compression=ZIP_DEFLATED) as output:
        for name, data in files.items():
            entry = ZipInfo(name, date_time=(2026, 10, 27, 0, 0, 0))
            entry.compress_type = ZIP_DEFLATED
            entry.external_attr = 0o100644 << 16
            output.writestr(entry, data)
    return archive


if __name__ == "__main__":
    print(export())
