using SmartBase.Core.Exporters.TableWriters;
using SmartBase.Core.Models;

namespace SmartBase.Core.Exporters;

/// <summary>
/// Выгрузка ГОТОВЫХ строк в один CSV-файл: журнал изменений, диагностика — всё, что
/// уже посчитано источником и не нуждается в планировщике коллекций (<see cref="ExportPlanner"/>).
///
/// Пишет тем же <see cref="CsvTableWriter"/>, что экспорт коллекций: UTF-8 с BOM,
/// разделитель «;», CRLF, кавычки RFC 4180 — Excel открывает без вопросов, а наш импорт
/// понимает файл обратно. Память O(1): источник — ленивая последовательность (keyset-страницы),
/// строки уходят в поток сразу, поэтому 10 млн строк выгружаются без роста памяти.
///
/// Недописанный файл после ошибки или отмены удаляется — как в экспорте коллекций
/// (<c>SidebarTree.RunExport</c>): половина журнала выглядела бы как целый журнал.
/// </summary>
public static class CsvDump
{
    /// <summary>
    /// Пишет файл и возвращает число записанных строк (без шапки).
    /// <paramref name="onRow"/> зовётся после каждой строки — для прогресса; троттлинг
    /// на стороне UI. Отмена проверяется на каждой строке: страница читается за
    /// миллисекунды, реакция мгновенная.
    /// </summary>
    public static long Write(string path, IReadOnlyList<string> headers,
                             IEnumerable<IReadOnlyList<string>> rows,
                             CancellationToken ct = default, Action<long>? onRow = null)
    {
        // Диалог сохранения уже спросил про замену, а CsvTableWriter открывает файл
        // строго CreateNew (в экспорте коллекций имя уникализирует планировщик).
        if (File.Exists(path)) File.Delete(path);

        // Все значения источник отдаёт уже отформатированными — тип «text» велит
        // писателю отдать их как есть, без дат/чекбоксов/мультиселектов.
        var columns = headers.Select(h => new ExportColumn(h, h, null, "text")).ToList();

        long written = 0;
        try
        {
            using (var writer = new CsvTableWriter(path))
            {
                writer.BeginTable("", columns, writeHeader: true);
                foreach (var row in rows)
                {
                    ct.ThrowIfCancellationRequested();
                    writer.WriteRow(row);
                    // Счётчик — отдельной строкой: в «onRow?.Invoke(++written)» аргумент
                    // не вычисляется, когда onRow == null (без прогресса счёт стоял бы на нуле).
                    written++;
                    onRow?.Invoke(written);
                }
                writer.EndTable();
                writer.Finish();
            }
            return written;
        }
        catch
        {
            // Писатель уже закрыт (using отработал при выходе из блока или при исключении).
            try { File.Delete(path); } catch { /* best-effort */ }
            throw;
        }
    }
}
