using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ECommons.DalamudServices;

namespace BOCCHI.Modules.DevMap;

internal static class NorthernLinkerMarkerCatalog
{
    public const uint TerritoryId = 1346;
    private const string RelativePath = "Data/NorthHorn/linker_markers.json";

    private static readonly HashSet<DevMarkerType> ManagedTypes =
    [
        DevMarkerType.SilverChest,
        DevMarkerType.BronzeChest,
        DevMarkerType.FortuneCarrot,
        DevMarkerType.PotChest,
        DevMarkerType.RerollChest,
    ];

    public static bool IsManagedType(DevMarkerType type)
    {
        return ManagedTypes.Contains(type);
    }

    public static bool IsManagedMarker(DevMapMarker marker)
    {
        return marker.TerritoryId == TerritoryId && IsManagedType(marker.Type);
    }

    public static IReadOnlyList<DevMapMarker> Load()
    {
        var path = Path.Join(
            Svc.PluginInterface.AssemblyLocation.DirectoryName,
            RelativePath
        );
        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() },
            };
            var file = JsonSerializer.Deserialize<LinkerMarkerCatalogFile>(
                File.ReadAllText(path),
                options
            ) ?? throw new InvalidDataException("Linker marker catalog is empty.");
            if (file.SchemaVersion != 1
                || !string.Equals(file.Source, "linker-catalog", StringComparison.Ordinal)
                || file.Markers.Count == 0)
            {
                throw new InvalidDataException("Linker marker catalog metadata is invalid.");
            }

            var markers = file.Markers
                .Where(record =>
                    record.TerritoryId == TerritoryId
                    && record.MapId is 1135 or 1244
                    && IsManagedType(record.Kind)
                    && float.IsFinite(record.X)
                    && float.IsFinite(record.Y)
                    && float.IsFinite(record.Z)
                )
                .Select(record => new DevMapMarker
                {
                    Type = record.Kind,
                    TerritoryId = record.TerritoryId,
                    MapId = record.MapId,
                    X = record.X,
                    Y = record.Y,
                    Z = record.Z,
                })
                .ToList();
            if (markers.Count != file.Markers.Count)
            {
                throw new InvalidDataException(
                    $"Linker marker catalog contains invalid records "
                    + $"({markers.Count}/{file.Markers.Count})."
                );
            }

            return markers;
        }
        catch (Exception exception)
        {
            Svc.Log.Error(exception, "Failed to load Eureka Linker marker catalog from {Path}", path);
            return Array.Empty<DevMapMarker>();
        }
    }

    private sealed class LinkerMarkerCatalogFile
    {
        public int SchemaVersion { get; set; }

        public string Source { get; set; } = "";

        public List<LinkerMarkerRecord> Markers { get; set; } = [];
    }

    private sealed class LinkerMarkerRecord
    {
        public DevMarkerType Kind { get; set; }

        public uint TerritoryId { get; set; }

        public uint MapId { get; set; }

        public float X { get; set; }

        public float Y { get; set; }

        public float Z { get; set; }
    }
}
