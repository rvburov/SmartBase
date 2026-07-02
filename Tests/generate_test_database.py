# -*- coding: utf-8 -*-
"""
Генератор тестовой БД Tests/test_database.db на 10 000 000 календарных записей —
стенд для проверки производительности календаря (см. docs/guides/test-database.md).

Профиль данных (точное воспроизведение исходного стенда):
  • одна коллекция «Задачи» (#007AFF) с одним полем-датой «Дата» (источник календаря);
  • записи 1..5 000 000 — «Событие N»: разброс по годам, старт
    2020-01-01T00:00:00 + (N−1)×68 секунд (покрывает ~2020–2030), длительность 1 час;
  • записи 5 000 001..10 000 000 — «День N»: ГИГАНТСКИЙ день 2026-07-01, старт
    2026-07-01T00:00:00 + ((N−5 000 001) mod 86400) секунд (≈58 записей на каждую
    секунду суток), длительность 1 час (хвост уходит за полночь — это нормально);
  • db_records несут денормализованные StartDt/EndDt/AllDay + Title (как пишет приложение),
    db_field_values хранит значение поля-даты в формате DateValue «start/end» с точностью
    до минут, calendar_index — ISO-строки формата "o" (7 знаков дробных секунд).

Схема — снята с рабочей БД приложения (EF Core 9, миграции по 20260702085732
включительно). Более новые миграции приложение применит само при подключении файла.

Запуск:  python3 Tests/generate_test_database.py            (создаёт Tests/test_database.db)
         python3 Tests/generate_test_database.py --force    (пересоздать существующий файл)
Время/размер: ~10–20 минут, ~4.6 ГБ (без индексов вставка быстрее — они строятся в конце).
"""

import os
import sqlite3
import sys
import time
from datetime import datetime, timedelta

DB_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'test_database.db')

SCATTERED = 5_000_000          # «Событие N» — разброс по годам
HUGE      = 5_000_000          # «День N» — один гигантский день
SCATTER_BASE = datetime(2020, 1, 1, 0, 0, 0)
SCATTER_STEP = 68              # секунд между соседними «Событие N»
HUGE_DAY     = datetime(2026, 7, 1, 0, 0, 0)
DURATION     = 3600            # длительность каждой записи, секунд
CREATED_AT   = '2026-07-01T00:00:00.0000000+03:00'
BATCH        = 100_000

# Схема приложения (EF Core), таблицы — без индексов: индексы строятся ПОСЛЕ вставки.
TABLES_SQL = """
CREATE TABLE "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);
CREATE TABLE "__EFMigrationsLock" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK___EFMigrationsLock" PRIMARY KEY,
    "Timestamp" TEXT NOT NULL
);
CREATE TABLE "categories" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_categories" PRIMARY KEY AUTOINCREMENT,
    "Name" TEXT NOT NULL,
    "Color" TEXT NOT NULL
);
CREATE TABLE "db_groups" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_db_groups" PRIMARY KEY AUTOINCREMENT,
    "Name" TEXT NOT NULL,
    "ParentId" INTEGER NULL,
    "Position" INTEGER NOT NULL,
    CONSTRAINT "FK_db_groups_db_groups_ParentId" FOREIGN KEY ("ParentId") REFERENCES "db_groups" ("Id") ON DELETE SET NULL
);
CREATE TABLE "db_collections" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_db_collections" PRIMARY KEY AUTOINCREMENT,
    "Name" TEXT NOT NULL,
    "GroupId" INTEGER NULL,
    "Position" INTEGER NOT NULL,
    "Icon" TEXT NOT NULL,
    "Color" TEXT NOT NULL,
    CONSTRAINT "FK_db_collections_db_groups_GroupId" FOREIGN KEY ("GroupId") REFERENCES "db_groups" ("Id") ON DELETE SET NULL
);
CREATE TABLE "db_fields" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_db_fields" PRIMARY KEY AUTOINCREMENT,
    "CollectionId" INTEGER NOT NULL,
    "Name" TEXT NOT NULL,
    "FieldType" TEXT NOT NULL,
    "ConfigJson" TEXT NOT NULL,
    "Position" INTEGER NOT NULL,
    "IsCalStart" INTEGER NOT NULL,
    "IsCalEnd" INTEGER NOT NULL,
    "IsSystem" INTEGER NOT NULL,
    "IsRequired" INTEGER NOT NULL,
    CONSTRAINT "FK_db_fields_db_collections_CollectionId" FOREIGN KEY ("CollectionId") REFERENCES "db_collections" ("Id") ON DELETE CASCADE
);
CREATE TABLE "db_records" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_db_records" PRIMARY KEY AUTOINCREMENT,
    "CollectionId" INTEGER NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "Position" INTEGER NOT NULL,
    "StartDt" TEXT NULL,
    "EndDt" TEXT NULL,
    "AllDay" INTEGER NOT NULL, "Description" TEXT NULL, "Title" TEXT NOT NULL DEFAULT '',
    CONSTRAINT "FK_db_records_db_collections_CollectionId" FOREIGN KEY ("CollectionId") REFERENCES "db_collections" ("Id") ON DELETE CASCADE
);
CREATE TABLE "db_field_values" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_db_field_values" PRIMARY KEY AUTOINCREMENT,
    "RecordId" INTEGER NOT NULL,
    "FieldId" INTEGER NOT NULL,
    "Value" TEXT NOT NULL,
    CONSTRAINT "FK_db_field_values_db_fields_FieldId" FOREIGN KEY ("FieldId") REFERENCES "db_fields" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_db_field_values_db_records_RecordId" FOREIGN KEY ("RecordId") REFERENCES "db_records" ("Id") ON DELETE CASCADE
);
CREATE TABLE "calendar_index" (
    "RecordId" INTEGER NOT NULL,
    "FieldId" INTEGER NOT NULL,
    "CollectionId" INTEGER NOT NULL,
    "StartIso" TEXT NOT NULL,
    "EndIso" TEXT NOT NULL,
    "AllDay" INTEGER NOT NULL,
    CONSTRAINT "PK_calendar_index" PRIMARY KEY ("RecordId", "FieldId"),
    CONSTRAINT "FK_calendar_index_db_fields_FieldId" FOREIGN KEY ("FieldId") REFERENCES "db_fields" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_calendar_index_db_records_RecordId" FOREIGN KEY ("RecordId") REFERENCES "db_records" ("Id") ON DELETE CASCADE
);
CREATE TABLE "column_display" (
    "CollectionId" INTEGER NOT NULL,
    "Key" TEXT NOT NULL,
    "Visible" INTEGER NOT NULL,
    "Level" INTEGER NOT NULL,
    CONSTRAINT "PK_column_display" PRIMARY KEY ("CollectionId", "Key")
);
CREATE TABLE "column_order" (
    "CollectionId" INTEGER NOT NULL,
    "Key" TEXT NOT NULL,
    "Position" INTEGER NOT NULL,
    CONSTRAINT "PK_column_order" PRIMARY KEY ("CollectionId", "Key")
);
CREATE TABLE "record_display" (
    "CollectionId" INTEGER NOT NULL,
    "RecordId" INTEGER NOT NULL,
    "Visible" INTEGER NOT NULL,
    "Level" INTEGER NOT NULL,
    CONSTRAINT "PK_record_display" PRIMARY KEY ("CollectionId", "RecordId")
);
CREATE TABLE "system_field_meta" (
    "CollectionId" INTEGER NOT NULL,
    "Key" TEXT NOT NULL,
    "DisplayName" TEXT NULL,
    "Position" INTEGER NOT NULL,
    CONSTRAINT "PK_system_field_meta" PRIMARY KEY ("CollectionId", "Key")
);
CREATE TABLE "ui_col_widths" (
    "CollectionId" INTEGER NOT NULL,
    "FieldId" INTEGER NOT NULL,
    "Width" INTEGER NOT NULL,
    CONSTRAINT "PK_ui_col_widths" PRIMARY KEY ("CollectionId", "FieldId")
);
CREATE TABLE "ui_collection_settings" (
    "CollectionId" INTEGER NOT NULL CONSTRAINT "PK_ui_collection_settings" PRIMARY KEY AUTOINCREMENT,
    "ColResize" INTEGER NOT NULL,
    "RowResize" INTEGER NOT NULL,
    "ViewMode" TEXT NOT NULL
);
CREATE TABLE "ui_row_heights" (
    "CollectionId" INTEGER NOT NULL,
    "RecordId" INTEGER NOT NULL,
    "Height" INTEGER NOT NULL,
    CONSTRAINT "PK_ui_row_heights" PRIMARY KEY ("CollectionId", "RecordId")
);
"""

INDEXES_SQL = """
CREATE INDEX "IX_calendar_index_CollectionId_AllDay_StartIso_RecordId_FieldId" ON "calendar_index" ("CollectionId", "AllDay", "StartIso", "RecordId", "FieldId");
CREATE INDEX "IX_calendar_index_CollectionId_StartIso" ON "calendar_index" ("CollectionId", "StartIso");
CREATE INDEX "IX_calendar_index_FieldId" ON "calendar_index" ("FieldId");
CREATE UNIQUE INDEX "IX_categories_Name" ON "categories" ("Name");
CREATE INDEX "IX_db_collections_GroupId" ON "db_collections" ("GroupId");
CREATE INDEX "IX_db_field_values_FieldId_Value" ON "db_field_values" ("FieldId", "Value");
CREATE UNIQUE INDEX "IX_db_field_values_RecordId_FieldId" ON "db_field_values" ("RecordId", "FieldId");
CREATE UNIQUE INDEX "IX_db_fields_CollectionId_Name" ON "db_fields" ("CollectionId", "Name");
CREATE INDEX "IX_db_groups_ParentId" ON "db_groups" ("ParentId");
CREATE INDEX "IX_db_records_CollectionId_Position" ON "db_records" ("CollectionId", "Position");
"""

# История миграций EF на момент снятия схемы: приложение при подключении файла само
# применит только более новые миграции (обычный путь обновления пользовательской БД).
MIGRATIONS = [
    '20260607161850_InitialCreate',
    '20260607161926_AddRecordTitleAndDescription',
    '20260609220000_DedupAndUniqueFieldNames',
    '20260612133732_AddSystemFieldMeta',
    '20260612141652_AddColumnOrder',
    '20260613164841_AddColumnDisplay',
    '20260615113001_AddRecordDisplay',
    '20260620100028_AddRecordCollectionPositionIndex',
    '20260620121455_AddFieldValueIndex',
    '20260623133636_AddCalendarIndex',
    '20260701125542_AddCalendarAllDayIndex',
    '20260701163054_ExtendCalendarAllDayIndexTiebreak',
    '20260702083828_AddRecordsTitleFtsTrigram',
    '20260702085732_RemoveRecordsTitleFtsTrigram',
]
EF_PRODUCT_VERSION = '9.0.0'


def iso(dt):
    """ISO-строка формата DateTime.ToString("o") без смещения: 7 знаков дробных секунд."""
    return dt.strftime('%Y-%m-%dT%H:%M:%S.0000000')


def value(dt_start, dt_end):
    """Значение поля-даты (DateValue.ToStorage): минутная точность, «start/end»."""
    return dt_start.strftime('%Y-%m-%dT%H:%M') + '/' + dt_end.strftime('%Y-%m-%dT%H:%M')


def rows():
    """(Id, StartDt, EndDt, Title) для всех 10 млн записей — в порядке Id."""
    dur = timedelta(seconds=DURATION)
    for n in range(1, SCATTERED + 1):
        s = SCATTER_BASE + timedelta(seconds=(n - 1) * SCATTER_STEP)
        yield n, s, s + dur, f'Событие {n}'
    # Гигантский день: старты повторяются каждые 86 400 записей — кешируем секунды суток.
    cache = [(HUGE_DAY + timedelta(seconds=sec), HUGE_DAY + timedelta(seconds=sec) + dur)
             for sec in range(86_400)]
    for n in range(SCATTERED + 1, SCATTERED + HUGE + 1):
        s, e = cache[(n - SCATTERED - 1) % 86_400]
        yield n, s, e, f'День {n}'


def main():
    if os.path.exists(DB_PATH):
        if '--force' not in sys.argv:
            print(f'{DB_PATH} уже существует — перезапуск с --force пересоздаст файл.')
            return 1
        os.remove(DB_PATH)

    t0 = time.time()
    con = sqlite3.connect(DB_PATH)
    cur = con.cursor()
    cur.execute('PRAGMA journal_mode=OFF')
    cur.execute('PRAGMA synchronous=OFF')
    cur.execute('PRAGMA temp_store=MEMORY')
    cur.execute('PRAGMA cache_size=-262144')   # 256 МБ

    # CRLF — как в DDL, созданной EF Core на Windows: текст схемы в sqlite_master
    # совпадает с оригиналом байт-в-байт.
    cur.executescript(TABLES_SQL.replace('\n', '\r\n'))
    cur.executemany('INSERT INTO "__EFMigrationsHistory" VALUES (?, ?)',
                    [(m, EF_PRODUCT_VERSION) for m in MIGRATIONS])
    cur.execute('INSERT INTO db_collections (Name, GroupId, Position, Icon, Color) '
                "VALUES ('Задачи', NULL, 1000, 'table', '#007AFF')")
    col_id = cur.lastrowid
    cur.execute('INSERT INTO db_fields (CollectionId, Name, FieldType, ConfigJson, Position, '
                'IsCalStart, IsCalEnd, IsSystem, IsRequired) '
                "VALUES (?, 'Дата', 'date', '{}', 1000, 1, 0, 1, 0)", (col_id,))
    field_id = cur.lastrowid
    con.commit()

    total = SCATTERED + HUGE
    rec_batch, val_batch, idx_batch = [], [], []

    def flush():
        cur.executemany(
            'INSERT INTO db_records (Id, CollectionId, CreatedAt, Position, StartDt, EndDt, '
            'AllDay, Description, Title) VALUES (?, ?, ?, ?, ?, ?, 0, NULL, ?)', rec_batch)
        cur.executemany(
            'INSERT INTO db_field_values (RecordId, FieldId, Value) VALUES (?, ?, ?)', val_batch)
        cur.executemany(
            'INSERT INTO calendar_index (RecordId, FieldId, CollectionId, StartIso, EndIso, '
            'AllDay) VALUES (?, ?, ?, ?, ?, 0)', idx_batch)
        con.commit()
        rec_batch.clear(); val_batch.clear(); idx_batch.clear()

    for n, s, e, title in rows():
        s_iso, e_iso = iso(s), iso(e)
        rec_batch.append((n, col_id, CREATED_AT, n, s_iso, e_iso, title))
        val_batch.append((n, field_id, value(s, e)))
        idx_batch.append((n, field_id, col_id, s_iso, e_iso))
        if len(rec_batch) >= BATCH:
            flush()
            if n % 1_000_000 == 0:
                print(f'  {n:>10,} / {total:,}  ({time.time() - t0:.0f} c)')
    flush()

    print('Строю индексы…')
    cur.executescript(INDEXES_SQL.replace('\n', '\r\n'))
    con.commit()
    cur.execute('PRAGMA journal_mode=DELETE')
    con.close()

    size = os.path.getsize(DB_PATH) / 1024 ** 3
    print(f'Готово: {DB_PATH}\n  записей: {total:,}, размер: {size:.2f} ГБ, '
          f'время: {(time.time() - t0) / 60:.1f} мин')
    return 0


if __name__ == '__main__':
    sys.exit(main())
