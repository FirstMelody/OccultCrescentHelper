using System.Text.Json;

public static class LinkerMarkerCatalog
{
    public static IReadOnlyList<TelemetryMarker> Load(string path)
    {
        var file = JsonSerializer.Deserialize<LinkerMarkerCatalogFile>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        ) ?? throw new InvalidDataException("Linker marker catalog is empty.");
        if (file.SchemaVersion != 1
            || !string.Equals(file.Source, "linker-catalog", StringComparison.Ordinal)
            || file.Markers.Count == 0)
        {
            throw new InvalidDataException("Linker marker catalog metadata is invalid.");
        }

        return file.Markers
            .Select(marker => new TelemetryMarker(
                file.Source,
                marker.Kind,
                marker.TerritoryId,
                marker.MapId,
                null,
                null,
                null,
                "Eureka Linker",
                marker.X,
                marker.Y,
                marker.Z,
                null,
                null
            ))
            .ToList();
    }

    private sealed class LinkerMarkerCatalogFile
    {
        public int SchemaVersion { get; set; }

        public string Source { get; set; } = "";

        public List<LinkerMarkerRecord> Markers { get; set; } = [];
    }

    private sealed class LinkerMarkerRecord
    {
        public string Kind { get; set; } = "";

        public uint TerritoryId { get; set; }

        public uint MapId { get; set; }

        public double X { get; set; }

        public double Y { get; set; }

        public double Z { get; set; }
    }
}
