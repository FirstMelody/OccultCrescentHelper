using System.Text.Json;
using Lumina;
using Lumina.Data;
using Lumina.Data.Files;
using Lumina.Excel.Sheets;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

if (args.Length < 2)
{
    Console.Error.WriteLine(
        "Usage: ClientMapExtractor <game-sqpack-path> <output-directory> [search]"
    );
    return 2;
}

var sqpackPath = Path.GetFullPath(args[0]);
var outputDirectory = Path.GetFullPath(args[1]);
var search = args.Length > 2 ? args[2] : "";
Directory.CreateDirectory(outputDirectory);

using var gameData = new GameData(
    sqpackPath,
    new LuminaOptions
    {
        DefaultExcelLanguage = Language.ChineseSimplified,
    }
);

var territorySheet =
    gameData.GetExcelSheet<TerritoryType>(Language.ChineseSimplified)
    ?? throw new InvalidOperationException("TerritoryType sheet is unavailable.");
var matches = territorySheet
    .Select(row =>
    {
        var placeName = row.PlaceName.ValueNullable?.Name.ToString() ?? "";
        var map = row.Map.ValueNullable;
        return new TerritoryMapRow(
            row.RowId,
            row.Map.RowId,
            map?.Id.ToString() ?? "",
            placeName,
            row.ContentFinderCondition.ValueNullable?.Name.ToString() ?? "",
            map?.SizeFactor ?? 100,
            map?.OffsetX ?? 0,
            map?.OffsetY ?? 0
        );
    })
    .Where(row =>
        string.IsNullOrWhiteSpace(search)
        || row.PlaceName.Contains(search, StringComparison.OrdinalIgnoreCase)
        || row.ContentName.Contains(search, StringComparison.OrdinalIgnoreCase)
        || row.MapResourceId.Contains(search, StringComparison.OrdinalIgnoreCase)
    )
    .OrderBy(row => row.TerritoryId)
    .ToList();

var extracted = new List<ExtractedMap>();
foreach (var row in matches.Where(row => !string.IsNullOrWhiteSpace(row.MapResourceId)))
{
    var resourceId = row.MapResourceId.Trim('/');
    var textureName = resourceId.Replace("/", "", StringComparison.Ordinal) + "_m.tex";
    var texturePath = $"ui/map/{resourceId}/{textureName}";
    var texture = gameData.GetFile<TexFile>(texturePath);
    if (texture == null)
    {
        Console.Error.WriteLine($"Map texture not found: {texturePath}");
        continue;
    }

    var outputName = $"{row.TerritoryId}-{row.MapId}.webp";
    var outputPath = Path.Join(outputDirectory, outputName);
    using var image = Image.LoadPixelData<Bgra32>(
        texture.ImageData,
        texture.Header.Width,
        texture.Header.Height
    );
    image.Save(
        outputPath,
        new WebpEncoder
        {
            Quality = 92,
            FileFormat = WebpFileFormatType.Lossy,
        }
    );
    extracted.Add(
        new ExtractedMap(
            row.TerritoryId,
            row.MapId,
            row.MapResourceId,
            row.PlaceName,
            row.ContentName,
            row.SizeFactor,
            row.OffsetX,
            row.OffsetY,
            outputName,
            texture.Header.Width,
            texture.Header.Height
        )
    );
}

var catalogPath = Path.Join(outputDirectory, "catalog.json");
File.WriteAllText(
    catalogPath,
    JsonSerializer.Serialize(
        new { maps = extracted },
        new JsonSerializerOptions { WriteIndented = true }
    )
);

Console.WriteLine(
    JsonSerializer.Serialize(
        extracted,
        new JsonSerializerOptions { WriteIndented = true }
    )
);

return 0;

internal sealed record TerritoryMapRow(
    uint TerritoryId,
    uint MapId,
    string MapResourceId,
    string PlaceName,
    string ContentName,
    ushort SizeFactor,
    short OffsetX,
    short OffsetY
);

internal sealed record ExtractedMap(
    uint TerritoryId,
    uint MapId,
    string MapResourceId,
    string PlaceName,
    string ContentName,
    ushort SizeFactor,
    short OffsetX,
    short OffsetY,
    string Image,
    int Width,
    int Height
);
