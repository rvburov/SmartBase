using SmartBase;
using SmartBase.Utils;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Оценка оставшегося времени (<see cref="EtaEstimator"/>) для долгих операций —
/// выгрузки CSV и монтирования баз. Класс не смотрит на часы: время подаёт вызывающий,
/// поэтому поведение проверяется точно, без ожиданий и таймеров.
/// </summary>
public sealed class EtaEstimatorTests
{
    [Fact]
    public void No_estimate_until_there_is_something_to_extrapolate()
    {
        var eta = new EtaEstimator();

        Assert.Null(eta.Update(0, TimeSpan.FromSeconds(10)));            // ничего не сделано
        Assert.Null(eta.Update(0.01, TimeSpan.FromSeconds(10)));         // меньше порога 2%
        Assert.Null(eta.Update(0.5, TimeSpan.FromMilliseconds(200)));    // прошло меньше секунды
        Assert.Null(eta.Update(1, TimeSpan.FromSeconds(10)));            // уже готово
    }

    /// <summary>Половина за минуту → минута до конца: «≈ 1 мин».</summary>
    [Fact]
    public void Half_way_extrapolates_the_remaining_half()
    {
        var eta = new EtaEstimator();
        Assert.Equal("осталось ≈ 1 мин", eta.Update(0.5, TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void Less_than_a_minute_is_phrased_without_numbers()
    {
        var eta = new EtaEstimator();
        // 90% за 90 секунд → ~10 секунд остатка.
        Assert.Equal("осталось меньше минуты", eta.Update(0.9, TimeSpan.FromSeconds(90)));
    }

    /// <summary>
    /// Скачок скорости не должен выбрасывать оценку целиком: сглаживание тянет её
    /// к новому значению постепенно, иначе строка прыгала бы «20 мин → 2 мин → 20 мин».
    /// </summary>
    [Fact]
    public void Smoothing_damps_a_sudden_speed_change()
    {
        var eta = new EtaEstimator(smoothing: 0.3);

        // Ровный ход: 10% за 100 с → остаток 900 с (15 мин).
        Assert.Equal("осталось ≈ 15 мин", eta.Update(0.1, TimeSpan.FromSeconds(100)));

        // Резкое ускорение: сырая оценка стала 100 с, но показанная — между старой и новой.
        var text = eta.Update(0.5, TimeSpan.FromSeconds(100));
        Assert.Equal("осталось ≈ 11 мин", text);   // 0.7*900 + 0.3*100 = 660 с

        // Дальше оценка продолжает сходиться к быстрой: 0.7*660 + 0.3*100 = 492 с.
        Assert.Equal("осталось ≈ 9 мин", eta.Update(0.5, TimeSpan.FromSeconds(100)));
    }

    /// <summary>
    /// Имя базы данных — свободный текст пользователя и попадает в имя файла выгрузки.
    /// «Отдел/Продажи» без чистки увёл бы диалог сохранения в другую папку (а на Windows
    /// дал бы недопустимое имя), поэтому префикс проходит общий санитайзер экспорта.
    /// </summary>
    [Fact]
    public void Export_file_name_is_safe_for_the_file_system()
    {
        var now  = new DateTime(2026, 8, 15, 20, 45, 0);
        var name = Controls.Dialogs.CsvExportRunner.FileName("История изменений — Отдел/Продажи", now);

        Assert.Equal("История изменений — Отдел_Продажи 2026-08-15 20-45.csv", name);
        Assert.DoesNotContain(System.IO.Path.GetInvalidFileNameChars(), name.Contains);
    }

    [Fact]
    public void Export_file_name_keeps_the_stamp_format()
    {
        Assert.Equal("Диагностика 2026-08-15 20-45.csv",
            Controls.Dialogs.CsvExportRunner.FileName("Диагностика", new DateTime(2026, 8, 15, 20, 45, 0)));
    }

    [Theory]
    [InlineData(0, "осталось меньше минуты")]
    [InlineData(49, "осталось меньше минуты")]
    [InlineData(50, "осталось ≈ 1 мин")]
    [InlineData(61, "осталось ≈ 2 мин")]
    [InlineData(3600, "осталось ≈ 60 мин")]
    public void Format_matches_the_wording_used_when_mounting_databases(double seconds, string expected)
    {
        Assert.Equal(expected, EtaEstimator.Format(seconds));
    }
}
