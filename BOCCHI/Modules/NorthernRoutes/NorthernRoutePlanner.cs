using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using BOCCHI.Enums;
using BOCCHI.Modules.Automator;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using Ocelot.IPC;

namespace BOCCHI.Modules.NorthernRoutes;

public sealed record NorthernNavigationPlan(
    bool UseTeleport,
    NorthernAethernetRoute? SourceRoute,
    NorthernAethernetRoute? DestinationRoute,
    float DirectCost,
    float SelectedCost,
    string Reason
)
{
    public static NorthernNavigationPlan Direct(float cost, string reason)
    {
        return new NorthernNavigationPlan(false, null, null, cost, cost, reason);
    }
}

public sealed class NorthernRoutePlanner
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PathfindTimeout = TimeSpan.FromSeconds(15);

    private readonly NorthernRouteStore store;
    private readonly AutomatorConfig config;
    private readonly VNavmesh vnav;
    private readonly Lifestream lifestream;
    private readonly ConcurrentDictionary<CacheKey, CacheEntry> cache = new();

    public NorthernRoutePlanner(
        NorthernRouteStore store,
        AutomatorConfig config,
        VNavmesh vnav,
        Lifestream lifestream
    )
    {
        this.store = store;
        this.config = config;
        this.vnav = vnav;
        this.lifestream = lifestream;
    }

    public async Task<NorthernNavigationPlan> PlanAsync(
        Vector3 playerPosition,
        Vector3 destination,
        uint territoryId,
        uint eventId,
        EventType eventType
    )
    {
        var key = CacheKey.Create(
            territoryId,
            eventId,
            eventType,
            playerPosition,
            destination,
            store.Revision
        );
        if (cache.TryGetValue(key, out var cached)
            && DateTime.UtcNow - cached.CreatedAt <= CacheLifetime)
        {
            return cached.Plan;
        }

        var plan = await CalculatePlanAsync(playerPosition, destination, territoryId);
        cache[key] = new CacheEntry(DateTime.UtcNow, plan);
        if (cache.Count > 64)
        {
            foreach (var stale in cache.Where(entry =>
                         DateTime.UtcNow - entry.Value.CreatedAt > CacheLifetime
                     ))
            {
                cache.TryRemove(stale.Key, out _);
            }
        }

        return plan;
    }

    public bool TryTeleport(NorthernAethernetRoute destinationRoute)
    {
        if (!lifestream.IsReady() || lifestream.IsBusy())
        {
            return false;
        }

        try
        {
            lifestream.Abort();
            if (!string.IsNullOrWhiteSpace(destinationRoute.Name)
                && lifestream.AethernetTeleport(destinationRoute.Name))
            {
                return true;
            }

            return destinationRoute.LifestreamDestinationId > 0
                   && lifestream.AethernetTeleportById(
                       destinationRoute.LifestreamDestinationId
                   );
        }
        catch (Exception ex)
        {
            DebugLog.Warning(ex, "Northern route teleport request failed");
            return false;
        }
    }

    private async Task<NorthernNavigationPlan> CalculatePlanAsync(
        Vector3 playerPosition,
        Vector3 destination,
        uint territoryId
    )
    {
        if (!config.UseNorthernAethernetRoutes || !lifestream.IsReady())
        {
            return NorthernNavigationPlan.Direct(
                Vector3.Distance(playerPosition, destination),
                "北岛魔路选路已关闭或 Lifestream 不可用"
            );
        }

        var routes = store.GetRoutes(territoryId)
            .Where(route => route.Enabled)
            .ToList();
        var destinations = routes
            .Where(route =>
                route.HasArrival
                && (!string.IsNullOrWhiteSpace(route.Name)
                    || route.LifestreamDestinationId > 0)
            )
            .OrderBy(route =>
                Vector3.Distance(NorthernRouteStore.GetArrivalPosition(route), destination)
            )
            .Take(3)
            .ToList();
        if (destinations.Count == 0 || routes.Count == 0)
        {
            return NorthernNavigationPlan.Direct(
                Vector3.Distance(playerPosition, destination),
                "没有同时具备传送名称/ID和到达坐标的已启用魔路"
            );
        }

        var directTask = GetPathLengthAsync(playerPosition, destination);
        var sources = routes
            .OrderBy(route =>
                Vector3.Distance(
                    NorthernRouteStore.GetInteractionPosition(route),
                    playerPosition
                )
            )
            .Take(3)
            .ToList();

        var sourceTasks = sources.ToDictionary(
            route => route.Id,
            route => GetPathLengthAsync(
                playerPosition,
                NorthernRouteStore.GetInteractionPosition(route)
            )
        );
        var destinationTasks = destinations.ToDictionary(
            route => route.Id,
            route => GetPathLengthAsync(
                NorthernRouteStore.GetArrivalPosition(route),
                destination
            )
        );

        await Task.WhenAll(
            sourceTasks.Values
                .Concat(destinationTasks.Values)
                .Append(directTask)
        );

        var directCost = await directTask;
        var bestSource = sources
            .Select(route => (Route: route, Cost: sourceTasks[route.Id].Result))
            .Where(candidate => float.IsFinite(candidate.Cost))
            .OrderBy(candidate => candidate.Cost)
            .FirstOrDefault();
        if (bestSource.Route == null)
        {
            return NorthernNavigationPlan.Direct(directCost, "无法寻路到任何已记录魔路");
        }

        var bestDestination = destinations
            .Select(route => (Route: route, Cost: destinationTasks[route.Id].Result))
            .Where(candidate => float.IsFinite(candidate.Cost))
            .OrderBy(candidate => candidate.Cost)
            .FirstOrDefault();
        if (bestDestination.Route == null)
        {
            return NorthernNavigationPlan.Direct(directCost, "无法从任何魔路到达坐标寻路到目标");
        }

        var teleportCost = bestSource.Cost
                           + Math.Max(0f, config.NorthernTeleportPenalty)
                           + bestDestination.Cost;
        if (!float.IsFinite(teleportCost)
            || (float.IsFinite(directCost) && teleportCost >= directCost))
        {
            return NorthernNavigationPlan.Direct(
                directCost,
                $"直走更短：直走 {directCost:F0} / 魔路 {teleportCost:F0}"
            );
        }

        return new NorthernNavigationPlan(
            true,
            bestSource.Route,
            bestDestination.Route,
            directCost,
            teleportCost,
            $"魔路更短：直走 {directCost:F0} / 魔路 {teleportCost:F0}"
        );
    }

    private async Task<float> GetPathLengthAsync(Vector3 start, Vector3 destination)
    {
        if (Vector3.Distance(start, destination) <= 4f)
        {
            return 0f;
        }

        try
        {
            var pathStart = vnav.FindNearestPointOnMesh(start, 5f, 5f) ?? start;
            var pathDestination =
                vnav.FindNearestPointOnMesh(destination, 8f, 8f) ?? destination;
            var path = await vnav.Pathfind(pathStart, pathDestination, false)
                .WaitAsync(PathfindTimeout);
            if (path.Count == 0)
            {
                return float.PositiveInfinity;
            }

            var length = 0f;
            var previous = pathStart;
            foreach (var point in path)
            {
                length += Vector3.Distance(previous, point);
                previous = point;
            }

            return length;
        }
        catch (Exception ex)
        {
            DebugLog.Debug($"Northern vnav cost query failed: {ex.Message}");
            return float.PositiveInfinity;
        }
    }

    private sealed record CacheEntry(DateTime CreatedAt, NorthernNavigationPlan Plan);

    private readonly record struct CacheKey(
        uint TerritoryId,
        uint EventId,
        EventType EventType,
        int PlayerX,
        int PlayerZ,
        int DestinationX,
        int DestinationZ,
        long Revision
    )
    {
        public static CacheKey Create(
            uint territoryId,
            uint eventId,
            EventType eventType,
            Vector3 player,
            Vector3 destination,
            long revision
        )
        {
            return new CacheKey(
                territoryId,
                eventId,
                eventType,
                (int)MathF.Round(player.X / 25f),
                (int)MathF.Round(player.Z / 25f),
                (int)MathF.Round(destination.X / 5f),
                (int)MathF.Round(destination.Z / 5f),
                revision
            );
        }
    }
}
