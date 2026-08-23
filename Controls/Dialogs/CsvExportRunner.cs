using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using SmartBase.Core.Exporters;
using SmartBase.Utils;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Выгрузка таблицы в CSV из интерфейса: окно подтверждения → выбор файла → прогресс
/// с процентами и оценкой времени → итоговое сообщение. Общая для «Диагностики»
/// и «Истории изменений» — оба экрана отличаются только заголовками и источником строк.
///
/// Источник строк — ЛЕНИВАЯ последовательность (keyset-страницы), которая разворачивается
/// уже в фоновом потоке: журнал может быть на миллионы строк, и ни память, ни UI-поток
/// этого не замечают. Прогресс дросселируется целым процентом (иначе 10 млн вызовов
/// завалили бы диспетчер сообщениями), оценка времени — <see cref="EtaEstimator"/>,
/// та же формулировка, что при монтировании баз.
/// </summary>
public static class CsvExportRunner
{
    /// <summary>Что выгружаем. Rows зовется в фоне: внутри — свои подключения к БД.</summary>
    public sealed record Job(
        string Title,                  // заголовок окна прогресса, напр. «Выгрузка диагностики»
        string What,                   // что именно выгружается, для окна подтверждения
        long   TotalRows,              // сколько строк ожидается (0 — выгружать нечего)
        bool   Filtered,               // применены ли фильтры таблицы
        string DefaultFileName,        // имя файла в диалоге сохранения (с расширением)
        IReadOnlyList<string> Headers,
        Func<CancellationToken, IEnumerable<IReadOnlyList<string>>> Rows);

    public static async Task Run(Window owner, Job job)
    {
        // Окно прогресса появляется не сразу (сначала подтверждение и выбор файла),
        // поэтому переменные объявлены снаружи try: в catch окно закрывается, только
        // если оно вообще успело открыться.
        ProgressDialog? progress = null;
        Task? progressClosed = null;
        string? path = null;

        try
        {
            if (job.TotalRows == 0)
            {
                await ConfirmDialog.Info($"{job.What}: выгружать нечего — строк нет.").ShowDialog(owner);
                return;
            }

            var scope = job.Filtered
                ? "Выгружаются строки с учетом активных фильтров таблицы."
                : "Выгружаются все строки таблицы.";
            var confirmed = await new ConfirmDialog(
                    $"{job.What} будет выгружена в файл CSV (UTF-8, разделитель «;») — " +
                    $"его открывает Excel и любая таблица.\n\n" +
                    $"Строк: {job.TotalRows:N0}. {scope}\n\n" +
                    "После подтверждения нужно выбрать, куда сохранить файл.",
                    confirmText: "Подтвердить")
                .ShowDialog<bool>(owner);
            if (!confirmed) return;

            var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title               = "Куда сохранить выгрузку",
                SuggestedFileName   = job.DefaultFileName,
                DefaultExtension    = "csv",
                ShowOverwritePrompt = true,
                FileTypeChoices     = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
            });
            path = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            progress = new ProgressDialog { Title = job.Title };
            using var cts = new CancellationTokenSource();
            progress.EnableCancel(() => cts.Cancel());

            IProgress<(double Fraction, string Info)> ui =
                new Progress<(double Fraction, string Info)>(t =>
                {
                    progress.SetProgress(t.Fraction, "Выгрузка строк…");
                    progress.SetInfo(t.Info);
                });

            progress.SetProgress(0, "Подготовка…");   // фаза до показа — окно не меняет высоту
            progress.SetInfo($"Строк: {job.TotalRows:N0}");
            progressClosed = progress.ShowDialog(owner);

            var target  = path;
            var written = await Task.Run(() =>
            {
                var watch   = Stopwatch.StartNew();
                var eta     = new EtaEstimator();
                var lastPct = -1;

                return CsvDump.Write(target, job.Headers, job.Rows(cts.Token), cts.Token, done =>
                {
                    var fraction = Math.Min(1.0, done / (double)job.TotalRows);
                    var pct      = (int)(fraction * 100);
                    if (pct == lastPct) return;   // гейт: обновляем UI раз на процент
                    lastPct = pct;

                    var info = $"{done:N0} из {job.TotalRows:N0}";
                    if (eta.Update(fraction, watch.Elapsed) is string left) info += " · " + left;
                    ui.Report((fraction, info));
                });
            });

            await CloseProgress(progress, progressClosed);
            progress = null;
            await ConfirmDialog.Info($"Выгружено строк: {written:N0}.\nФайл: {path}").ShowDialog(owner);
        }
        catch (OperationCanceledException)
        {
            // Недописанный файл удален в CsvDump — половина журнала выглядела бы как целый.
            await CloseProgress(progress, progressClosed);
        }
        catch (Exception ex)
        {
            // Сюда попадают и ошибки записи, и сбои диалога сохранения: кнопка не должна
            // молча «не работать» — пользователь всегда получает причину.
            await CloseProgress(progress, progressClosed);
            await ConfirmDialog.Info(ExportErrors.Map(ex, path ?? "").Message).ShowDialog(owner);
        }
    }

    private static async Task CloseProgress(ProgressDialog? progress, Task? closed)
    {
        if (progress is null) return;
        progress.Done();
        if (closed is not null) await closed;
    }

    /// <summary>
    /// Имя файла по умолчанию: «Диагностика 2026-08-15 20-45.csv». Имя базы данных —
    /// свободный текст пользователя, поэтому чистится тем же санитайзером, что и остальной
    /// экспорт: «Отдел/Продажи» иначе увел бы диалог сохранения в чужую папку.
    /// </summary>
    public static string FileName(string prefix, DateTime now) =>
        $"{ExportPlanner.SanitizeFileName(prefix)} {now:yyyy-MM-dd HH-mm}.csv";
}
