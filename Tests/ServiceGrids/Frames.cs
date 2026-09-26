using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SmartBase.Tests.ServiceGrids;

/// <summary>
/// Кадры: сохранение, чтение и попиксельное сравнение — как в стенде снимков GridProto: строгое
/// число различий и число с допуском на сглаживание, карта различий (эталон, выбеленный наполовину,
/// различия — красным) и разбивка различий по строкам и колонкам таблицы. Оба кадра читаются из
/// PNG одним декодером, чтобы порядок каналов совпал.
/// </summary>
static class Frames
{
    public static WriteableBitmap Load(string file)
    {
        using var fs = File.OpenRead(file);
        return WriteableBitmap.Decode(fs);
    }

    public static void Save(WriteableBitmap? bmp, string file)
    {
        if (bmp is null) { Console.WriteLine($"  кадр не снят: {file}"); return; }
        using var fs = File.Create(file);
        bmp.Save(fs);
    }

    /// <summary>Сравнение (a — эталон, b — снятый). <paramref name="band"/> называет полосу таблицы
    /// по пикселю кадра для разбивки различий.</summary>
    public static (int Strict, int Loose, WriteableBitmap Diff, List<(string Name, int Count)> Bands)
        Compare(WriteableBitmap a, WriteableBitmap b, int tolerance, Func<int, int, string> band)
    {
        var size = a.PixelSize;
        var diff = new WriteableBitmap(size, a.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fa = a.Lock();
        using var fb = b.Lock();
        using var fd = diff.Lock();
        var bufA = new byte[fa.RowBytes * size.Height];
        var bufB = new byte[fb.RowBytes * size.Height];
        var bufD = new byte[fd.RowBytes * size.Height];
        System.Runtime.InteropServices.Marshal.Copy(fa.Address, bufA, 0, bufA.Length);
        System.Runtime.InteropServices.Marshal.Copy(fb.Address, bufB, 0, bufB.Length);

        var strict = 0;
        var loose  = 0;
        var bands  = new Dictionary<string, int>();
        for (var y = 0; y < size.Height; y++)
        {
            for (var x = 0; x < size.Width; x++)
            {
                var ia = y * fa.RowBytes + x * 4;
                var ib = y * fb.RowBytes + x * 4;
                var id = y * fd.RowBytes + x * 4;
                var d = Math.Max(Math.Max(Math.Abs(bufA[ia] - bufB[ib]), Math.Abs(bufA[ia + 1] - bufB[ib + 1])),
                                 Math.Abs(bufA[ia + 2] - bufB[ib + 2]));
                if (d == 0)
                {
                    bufD[id]     = (byte)(128 + bufA[ia] / 2);
                    bufD[id + 1] = (byte)(128 + bufA[ia + 1] / 2);
                    bufD[id + 2] = (byte)(128 + bufA[ia + 2] / 2);
                    bufD[id + 3] = 255;
                    continue;
                }
                strict++;
                var hard = d > tolerance;
                if (hard) loose++;
                bufD[id]     = hard ? (byte)0   : (byte)200;
                bufD[id + 1] = hard ? (byte)0   : (byte)200;
                bufD[id + 2] = hard ? (byte)255 : (byte)255;
                bufD[id + 3] = 255;
                if (!hard) continue;
                var key = band(x, y);
                bands[key] = bands.GetValueOrDefault(key) + 1;
            }
        }
        System.Runtime.InteropServices.Marshal.Copy(bufD, 0, fd.Address, bufD.Length);
        return (strict, loose, diff, bands.Select(kv => (kv.Key, kv.Value)).ToList());
    }

    /// <summary>Вырезка кадра, увеличенная в 6 раз без сглаживания (координаты в DIP × масштаб).</summary>
    public static void SaveCrop(WriteableBitmap src, int x, int y, int w, int h, double scale, string file)
    {
        const int zoom = 6;
        var px = Math.Max(0, (int)(x * scale)); var py = Math.Max(0, (int)(y * scale));
        var pw = (int)(w * scale); var ph = (int)(h * scale);
        pw = Math.Min(pw, src.PixelSize.Width - px); ph = Math.Min(ph, src.PixelSize.Height - py);
        if (pw <= 0 || ph <= 0) return;
        var dst = new WriteableBitmap(new PixelSize(pw * zoom, ph * zoom), src.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fs = src.Lock();
        using var fd = dst.Lock();
        var bufS = new byte[fs.RowBytes * src.PixelSize.Height];
        var bufD = new byte[fd.RowBytes * dst.PixelSize.Height];
        System.Runtime.InteropServices.Marshal.Copy(fs.Address, bufS, 0, bufS.Length);
        for (var yy = 0; yy < ph * zoom; yy++)
            for (var xx = 0; xx < pw * zoom; xx++)
            {
                var s = (py + yy / zoom) * fs.RowBytes + (px + xx / zoom) * 4;
                var d = yy * fd.RowBytes + xx * 4;
                bufD[d] = bufS[s]; bufD[d + 1] = bufS[s + 1]; bufD[d + 2] = bufS[s + 2]; bufD[d + 3] = 255;
            }
        System.Runtime.InteropServices.Marshal.Copy(bufD, 0, fd.Address, bufD.Length);
        using var f = File.Create(file);
        dst.Save(f);
    }
}
