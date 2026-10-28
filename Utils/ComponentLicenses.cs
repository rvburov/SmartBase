using System.Text;

namespace SmartBase.Utils;

/// <summary>Лицензии поставляемых компонентов: читаются только при раскрытии блока в настройках.</summary>
internal static class ComponentLicenses
{
    public static string Read()
    {
        var text = new StringBuilder();
        Append(text, "Лицензия приложения", "Application");
        Append(text, "Уведомления о сторонних компонентах", "ThirdParty");
        Append(text, "PDFium: уведомления нативной библиотеки",
            OperatingSystem.IsMacOS() ? "Pdfium.MacOS" : "Pdfium.Common");
        return text.ToString();
    }

    private static void Append(StringBuilder text, string title, string resource)
    {
        using var stream = typeof(ComponentLicenses).Assembly
            .GetManifestResourceStream("SmartBase.Licenses." + resource)
            ?? throw new InvalidOperationException("Не найден встроенный текст лицензии: " + resource);
        using var reader = new StreamReader(stream);
        if (text.Length != 0) text.AppendLine().AppendLine();
        text.AppendLine(title).AppendLine().Append(reader.ReadToEnd());
    }
}
