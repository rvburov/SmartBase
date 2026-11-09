// Вынимает нужные модели PaddleOCR v5 из ресурсов сборки пакета Sdcb.PaddleOCR.Models.LocalV5
// (116 МБ, все языки) в папку — без загрузки самой сборки (System.Reflection.Metadata).
// Запускает сборка плагина (SmartBase.Extensions.PdfRename.csproj, цель SbPaddleModels):
//   dotnet run --file ExtractPaddleModels.cs -- <сборка моделей> <папка> <модель>[;<модель>…]
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length != 3)
{
    Console.Error.WriteLine("ExtractPaddleModels <Sdcb.PaddleOCR.Models.LocalV5.dll> <папка> <модели через ;>");
    return 2;
}

var (source, destination, models) = (args[0], args[1], args[2].Split(';', StringSplitOptions.RemoveEmptyEntries));
using var stream = File.OpenRead(source);
using var pe     = new PEReader(stream);
var metadata = pe.GetMetadataReader();
var block    = pe.GetSectionData(pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress);

var written = 0;
foreach (var handle in metadata.ManifestResources)
{
    var resource = metadata.GetManifestResource(handle);
    var name     = metadata.GetString(resource.Name);
    foreach (var model in models)
    {
        var prefix = $"Sdcb.PaddleOCR.Models.LocalV5.models.{model}.";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;

        var reader = block.GetReader((int)resource.Offset, block.Length - (int)resource.Offset);
        var bytes  = reader.ReadBytes(reader.ReadInt32());
        var dir    = Path.Combine(destination, model);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name[prefix.Length..]), bytes);
        written++;
    }
}

if (written != models.Length * 3)
{
    Console.Error.WriteLine($"Ожидалось файлов моделей: {models.Length * 3}, вынуто: {written}");
    return 1;
}
return 0;
