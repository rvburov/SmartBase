namespace SmartBase.Tests.Benchmarks;

/// <summary>Общие мелочи для всех бенчмарков (устраняет копипасту прежних харнессов).</summary>
internal static class BenchUtil
{
    public static string? ArgStr(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static int? ArgInt(string[] args, string name) =>
        int.TryParse(ArgStr(args, name), out var v) ? v : null;

    public static string Fmt(double ms) => ms switch
    {
        < 1000   => $"{ms:0} мс",
        < 60_000 => $"{ms / 1000:0.0} с",
        _        => $"{(int)(ms / 60_000)} мин {ms % 60_000 / 1000:0} с",
    };

    public static double SizeGb(string path) =>
        File.Exists(path) ? new FileInfo(path).Length / 1024.0 / 1024.0 / 1024.0 : 0;
}
