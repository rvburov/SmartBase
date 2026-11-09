namespace SmartBase.Extensions.PdfRename.Ocr;

/// <summary>Строка, прочитанная движком: текст, уверенность 0..1 (шкала у каждого движка
/// своя, сравнивать между движками нельзя) и место на картинке области.</summary>
internal sealed record OcrLine(string Text, double Confidence, int Left, int Top, int Right, int Bottom)
{
    public int CenterY => (Top + Bottom) / 2;
    public int Height => Bottom - Top;
}

/// <summary>Результат одного движка по одной области: строки сверху вниз.</summary>
internal sealed record OcrReading(string Engine, IReadOnlyList<OcrLine> Lines)
{
    public bool IsEmpty => Lines.Count == 0;

    /// <summary>Наименьшая уверенность среди строк; 0 — строк нет.</summary>
    public double MinConfidence => Lines.Count == 0 ? 0 : Lines.Min(l => l.Confidence);

    public string Text => string.Join("\n", Lines.Select(l => l.Text));
}

/// <summary>Движок распознавания области. Экземпляр не потокобезопасен: у каждого рабочего
/// потока свой.</summary>
internal interface IOcrEngine : IDisposable
{
    string Name { get; }

    /// <summary>Прочитать подготовленную область (8 бит, оттенки серого, белый фон).</summary>
    OcrReading Read(OpenCvSharp.Mat gray);
}
