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

if (search == "--map-schema")
{
    Console.WriteLine(
        string.Join(
            Environment.NewLine,
            typeof(Map)
                .GetProperties()
                .OrderBy(property => property.Name)
                .Select(property => $"{property.PropertyType.Name} {property.Name}")
        )
    );
    return 0;
}

using var gameData = new GameData(
    sqpackPath,
    new LuminaOptions
    {
        DefaultExcelLanguage = Language.ChineseSimplified,
    }
);

if (search.StartsWith("--extract-icons=", StringComparison.Ordinal))
{
    var iconIds = search["--extract-icons=".Length..]
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(uint.Parse)
        .Distinct()
        .OrderBy(iconId => iconId)
        .ToList();
    foreach (var iconId in iconIds)
    {
        ExtractIcon(gameData, outputDirectory, iconId);
    }

    return 0;
}

if (search.StartsWith("--list-territory-maps=", StringComparison.Ordinal))
{
    var territoryIds = search["--list-territory-maps=".Length..]
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(uint.Parse)
        .ToHashSet();
    var mapSheet =
        gameData.GetExcelSheet<Map>(Language.ChineseSimplified)
        ?? throw new InvalidOperationException("Map sheet is unavailable.");
    var mapRows = mapSheet
        .Where(row => territoryIds.Contains(row.TerritoryType.RowId))
        .Select(row => new
        {
            MapId = row.RowId,
            TerritoryId = row.TerritoryType.RowId,
            ResourceId = row.Id.ToString(),
            PlaceName = row.PlaceName.ValueNullable?.Name.ToString() ?? "",
            PlaceNameSub = row.PlaceNameSub.ValueNullable?.Name.ToString() ?? "",
            row.SizeFactor,
            row.OffsetX,
            row.OffsetY,
            MapType = row.MapType.RowId,
            MapCondition = row.MapCondition.RowId,
        })
        .OrderBy(row => row.TerritoryId)
        .ThenBy(row => row.MapId)
        .ToList();
    Console.WriteLine(
        JsonSerializer.Serialize(
            mapRows,
            new JsonSerializerOptions { WriteIndented = true }
        )
    );
    return 0;
}

if (search.StartsWith("--extract-place=", StringComparison.Ordinal))
{
    var placeSearch = search["--extract-place=".Length..];
    var mapSheet =
        gameData.GetExcelSheet<Map>(Language.ChineseSimplified)
        ?? throw new InvalidOperationException("Map sheet is unavailable.");
    var extractedMaps = mapSheet
        .Where(row =>
            (row.PlaceName.ValueNullable?.Name.ToString() ?? "")
            .Contains(placeSearch, StringComparison.OrdinalIgnoreCase)
        )
        .OrderBy(row => row.RowId)
        .Select(row =>
            ExtractMap(
                gameData,
                outputDirectory,
                row.TerritoryType.RowId,
                row.RowId,
                row.Id.ToString(),
                row.PlaceNameSub.ValueNullable?.Name.ToString() ?? "",
                row.PlaceName.ValueNullable?.Name.ToString() ?? "",
                row.SizeFactor,
                row.OffsetX,
                row.OffsetY
            )
        )
        .Where(map => map != null)
        .Cast<ExtractedMap>()
        .ToList();
    WriteMergedCatalog(outputDirectory, extractedMaps);
    Console.WriteLine(
        JsonSerializer.Serialize(
            extractedMaps,
            new JsonSerializerOptions { WriteIndented = true }
        )
    );
    return 0;
}

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
    var map = ExtractMap(
        gameData,
        outputDirectory,
        row.TerritoryId,
        row.MapId,
        row.MapResourceId,
        row.PlaceName,
        row.ContentName,
        row.SizeFactor,
        row.OffsetX,
        row.OffsetY
    );
    if (map == null)
    {
        continue;
    }

    extracted.Add(map);
}

WriteMergedCatalog(outputDirectory, extracted);

Console.WriteLine(
    JsonSerializer.Serialize(
        extracted,
        new JsonSerializerOptions { WriteIndented = true }
    )
);

return 0;

static ExtractedMap? ExtractMap(
    GameData gameData,
    string outputDirectory,
    uint territoryId,
    uint mapId,
    string mapResourceId,
    string placeName,
    string contentName,
    ushort sizeFactor,
    short offsetX,
    short offsetY
)
{
    var resourceId = mapResourceId.Trim('/');
    var textureName = resourceId.Replace("/", "", StringComparison.Ordinal) + "_m.tex";
    var texturePath = $"ui/map/{resourceId}/{textureName}";
    var texture = gameData.GetFile<TexFile>(texturePath);
    if (texture == null)
    {
        Console.Error.WriteLine($"Map texture not found: {texturePath}");
        return null;
    }

    var outputName = $"{territoryId}-{mapId}.webp";
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
    return new ExtractedMap(
        territoryId,
        mapId,
        mapResourceId,
        placeName,
        contentName,
        sizeFactor,
        offsetX,
        offsetY,
        outputName,
        texture.Header.Width,
        texture.Header.Height
    );
}

static void ExtractIcon(GameData gameData, string outputDirectory, uint iconId)
{
    var iconGroup = iconId - iconId % 1000;
    var texturePath = $"ui/icon/{iconGroup:D6}/{iconId:D6}_hr1.tex";
    var texture = gameData.GetFile<TexFile>(texturePath);
    if (texture == null)
    {
        Console.Error.WriteLine($"Icon texture not found: {texturePath}");
        return;
    }

    var outputPath = Path.Join(outputDirectory, $"{iconId}.webp");
    using var image = Image.LoadPixelData<Bgra32>(
        texture.ImageData,
        texture.Header.Width,
        texture.Header.Height
    );
    image.Save(
        outputPath,
        new WebpEncoder
        {
            FileFormat = WebpFileFormatType.Lossless,
        }
    );
    Console.WriteLine($"{iconId}: {texture.Header.Width}x{texture.Header.Height} -> {outputPath}");
}

static void WriteMergedCatalog(string outputDirectory, List<ExtractedMap> extracted)
{
    var catalogPath = Path.Join(outputDirectory, "catalog.json");
    var existing = File.Exists(catalogPath)
        ? JsonSerializer.Deserialize<MapCatalog>(
              File.ReadAllText(catalogPath),
              new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
          )?.Maps ?? []
        : [];
    var merged = existing
        .Concat(extracted)
        .GroupBy(map => (map.TerritoryId, map.MapId))
        .Select(group => group.Last())
        .OrderBy(map => map.TerritoryId)
        .ThenBy(map => map.MapId)
        .ToList();
    File.WriteAllText(
        catalogPath,
        JsonSerializer.Serialize(
            new { maps = merged },
            new JsonSerializerOptions { WriteIndented = true }
        )
    );
}

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

internal sealed record MapCatalog(List<ExtractedMap> Maps);
