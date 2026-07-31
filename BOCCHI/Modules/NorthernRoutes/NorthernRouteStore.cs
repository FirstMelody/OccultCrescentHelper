using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace BOCCHI.Modules.NorthernRoutes;

public sealed class NorthernRouteStore
{
    private const string FileName = "northern_expedition_routes.json";

    private readonly object sync = new();
    private readonly string path;
    private readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
    };

    private NorthernRouteFile file = new();

    public long Revision { get; private set; }

    public string Path
    {
        get => path;
    }

    public NorthernRouteStore(string configDirectory)
    {
        path = System.IO.Path.Join(configDirectory, FileName);
        Load();
        ApplyBuiltInRoutes();
    }

    public IReadOnlyList<NorthernAethernetRoute> GetRoutes(uint territoryId)
    {
        lock (sync)
        {
            return file.Routes
                .Where(route => route.TerritoryId == territoryId)
                .Select(Clone)
                .OrderBy(route =>
                    route.TeleportMenuOrder > 0
                        ? route.TeleportMenuOrder
                        : int.MaxValue
                )
                .ThenBy(route => route.Name)
                .ToList();
        }
    }

    public NorthernAethernetRoute RecordRoute(
        uint territoryId,
        uint mapId,
        string name,
        int teleportMenuOrder,
        uint destinationId,
        uint activeCustomAetheryteId,
        uint baseId,
        Vector3 interactionPosition
    )
    {
        lock (sync)
        {
            var route = file.Routes.FirstOrDefault(entry =>
                entry.TerritoryId == territoryId
                && Vector3.Distance(GetInteractionPosition(entry), interactionPosition) <= 4f
            );
            if (route == null)
            {
                route = new NorthernAethernetRoute
                {
                    TerritoryId = territoryId,
                    MapId = mapId,
                    InteractionX = interactionPosition.X,
                    InteractionY = interactionPosition.Y,
                    InteractionZ = interactionPosition.Z,
                };
                file.Routes.Add(route);
            }

            route.MapId = mapId;
            route.Name = name.Trim();
            route.TeleportMenuOrder = Math.Max(0, teleportMenuOrder);
            route.LifestreamDestinationId = destinationId;
            route.ActiveCustomAetheryteId = activeCustomAetheryteId;
            route.BaseId = baseId;
            route.InteractionX = interactionPosition.X;
            route.InteractionY = interactionPosition.Y;
            route.InteractionZ = interactionPosition.Z;
            route.Enabled = true;

            SaveLocked();
            return Clone(route);
        }
    }

    public bool RecordArrival(Guid routeId, Vector3 arrivalPosition)
    {
        lock (sync)
        {
            var route = file.Routes.FirstOrDefault(entry => entry.Id == routeId);
            if (route == null)
            {
                return false;
            }

            route.ArrivalX = arrivalPosition.X;
            route.ArrivalY = arrivalPosition.Y;
            route.ArrivalZ = arrivalPosition.Z;
            route.HasArrival = true;
            SaveLocked();
            return true;
        }
    }

    public bool SetRouteEnabled(Guid routeId, bool enabled)
    {
        lock (sync)
        {
            var route = file.Routes.FirstOrDefault(entry => entry.Id == routeId);
            if (route == null)
            {
                return false;
            }

            route.Enabled = enabled;
            SaveLocked();
            return true;
        }
    }

    public bool DeleteRoute(Guid routeId)
    {
        lock (sync)
        {
            var removed = file.Routes.RemoveAll(entry => entry.Id == routeId) > 0;
            if (removed)
            {
                SaveLocked();
            }

            return removed;
        }
    }

    public static Vector3 GetInteractionPosition(NorthernAethernetRoute route)
    {
        return new Vector3(route.InteractionX, route.InteractionY, route.InteractionZ);
    }

    public static Vector3 GetArrivalPosition(NorthernAethernetRoute route)
    {
        return new Vector3(route.ArrivalX, route.ArrivalY, route.ArrivalZ);
    }

    private void Load()
    {
        lock (sync)
        {
            if (!File.Exists(path))
            {
                file = new NorthernRouteFile();
                return;
            }

            try
            {
                file = JsonSerializer.Deserialize<NorthernRouteFile>(
                    File.ReadAllText(path),
                    jsonOptions
                ) ?? new NorthernRouteFile();
                file.Version = Math.Max(file.Version, 2);
                file.Routes ??= [];
                file.StandbyPoints ??= [];
            }
            catch (Exception ex)
            {
                DebugLog.Warning(
                    ex,
                    $"Unable to load Northern route data from {path}"
                );
                file = new NorthernRouteFile();
            }
        }
    }

    private void ApplyBuiltInRoutes()
    {
        lock (sync)
        {
            var changed = false;
            foreach (var builtIn in NorthernRouteDefaults.Routes)
            {
                var route = file.Routes.FirstOrDefault(entry =>
                    entry.TerritoryId == builtIn.TerritoryId
                    && (
                        entry.BaseId == builtIn.BaseId
                        || string.Equals(
                            entry.Name,
                            builtIn.Name,
                            StringComparison.Ordinal
                        )
                        || Vector3.Distance(
                            GetInteractionPosition(entry),
                            GetInteractionPosition(builtIn)
                        ) <= 4f
                    )
                );
                if (route == null)
                {
                    file.Routes.Add(Clone(builtIn));
                    changed = true;
                    continue;
                }

                if (!BuiltInDataMatches(route, builtIn))
                {
                    route.MapId = builtIn.MapId;
                    route.Name = builtIn.Name;
                    route.TeleportMenuOrder = builtIn.TeleportMenuOrder;
                    route.BaseId = builtIn.BaseId;
                    route.InteractionX = builtIn.InteractionX;
                    route.InteractionY = builtIn.InteractionY;
                    route.InteractionZ = builtIn.InteractionZ;
                    route.ArrivalX = builtIn.ArrivalX;
                    route.ArrivalY = builtIn.ArrivalY;
                    route.ArrivalZ = builtIn.ArrivalZ;
                    route.HasArrival = true;
                    changed = true;
                }
            }

            if (changed)
            {
                SaveLocked();
            }
        }
    }

    private static bool BuiltInDataMatches(
        NorthernAethernetRoute route,
        NorthernAethernetRoute builtIn
    )
    {
        return route.MapId == builtIn.MapId
               && string.Equals(route.Name, builtIn.Name, StringComparison.Ordinal)
               && route.TeleportMenuOrder == builtIn.TeleportMenuOrder
               && route.BaseId == builtIn.BaseId
               && route.InteractionX.Equals(builtIn.InteractionX)
               && route.InteractionY.Equals(builtIn.InteractionY)
               && route.InteractionZ.Equals(builtIn.InteractionZ)
               && route.ArrivalX.Equals(builtIn.ArrivalX)
               && route.ArrivalY.Equals(builtIn.ArrivalY)
               && route.ArrivalZ.Equals(builtIn.ArrivalZ)
               && route.HasArrival;
    }

    private void SaveLocked()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(file, jsonOptions));
        File.Move(temporaryPath, path, true);
        Revision++;
    }

    private static NorthernAethernetRoute Clone(NorthernAethernetRoute route)
    {
        return new NorthernAethernetRoute
        {
            Id = route.Id,
            TerritoryId = route.TerritoryId,
            MapId = route.MapId,
            Name = route.Name,
            TeleportMenuOrder = route.TeleportMenuOrder,
            LifestreamDestinationId = route.LifestreamDestinationId,
            ActiveCustomAetheryteId = route.ActiveCustomAetheryteId,
            BaseId = route.BaseId,
            InteractionX = route.InteractionX,
            InteractionY = route.InteractionY,
            InteractionZ = route.InteractionZ,
            ArrivalX = route.ArrivalX,
            ArrivalY = route.ArrivalY,
            ArrivalZ = route.ArrivalZ,
            HasArrival = route.HasArrival,
            Enabled = route.Enabled,
            RecordedAt = route.RecordedAt,
        };
    }

}
