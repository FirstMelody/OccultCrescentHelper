using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using BOCCHI.Enums;
using BOCCHI.Modules.Automator;
using ECommons.DalamudServices;
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
    private static readonly TimeSpan PathfindTimeout = TimeSpan.FromSeconds(15);
    private const float SourceCrystalPositionTolerance = 15f;

    private readonly NorthernRouteStore store;
    private readonly AutomatorConfig config;
    private readonly VNavmesh vnav;
    private int skipNextSourceReturn;
    private int returnCancellationGeneration;
    private int returnInProgress;
    private readonly ConcurrentDictionary<
        CacheKey,
        Lazy<Task<NorthernNavigationPlan>>
    > cache = new();

    public NorthernRoutePlanner(
        NorthernRouteStore store,
        AutomatorConfig config,
        VNavmesh vnav
    )
    {
        this.store = store;
        this.config = config;
        this.vnav = vnav;
    }

    public void MarkReturnedToSource()
    {
        Interlocked.Exchange(ref skipNextSourceReturn, 1);
    }

    public bool ConsumeReturnedToSource()
    {
        return Interlocked.Exchange(ref skipNextSourceReturn, 0) == 1;
    }

    public bool IsReturnInProgress
    {
        get => Volatile.Read(ref returnInProgress) == 1;
    }

    public int BeginReturn()
    {
        Interlocked.Exchange(ref returnInProgress, 1);
        return Volatile.Read(ref returnCancellationGeneration);
    }

    public bool IsReturnCanceled(int generation)
    {
        return generation != Volatile.Read(
            ref returnCancellationGeneration
        );
    }

    public void EndReturn()
    {
        Interlocked.Exchange(ref returnInProgress, 0);
    }

    public void CancelActiveReturn()
    {
        Interlocked.Increment(ref returnCancellationGeneration);
        Interlocked.Exchange(ref returnInProgress, 0);
    }

    public bool IsNearSourceCrystal(uint territoryId, float range = 35f)
    {
        var player = Svc.Objects.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        foreach (var source in NorthernRouteDefaults.SourceOnlyRoutes.Where(
                     route => route.TerritoryId == territoryId
                              && route.BaseId != 0
                 ))
        {
            var expectedPosition =
                NorthernRouteStore.GetInteractionPosition(source);
            var sourceObject = Svc.Objects.FirstOrDefault(obj =>
                obj.BaseId == source.BaseId
                && Vector3.Distance(obj.Position, expectedPosition)
                <= SourceCrystalPositionTolerance
            );
            if (sourceObject != null
                && sourceObject.CurrentDistance <= range)
            {
                return true;
            }
        }

        return false;
    }

    public async Task<NorthernNavigationPlan> PlanAsync(
        Vector3 _playerPosition,
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
            destination,
            store.Revision
        );
        var calculation = cache.GetOrAdd(
            key,
            _ => new Lazy<Task<NorthernNavigationPlan>>(
                () => CalculatePlanAsync(destination, territoryId),
                LazyThreadSafetyMode.ExecutionAndPublication
            )
        );
        var plan = await calculation.Value;
        if (cache.Count > 128)
        {
            foreach (var stale in cache.Keys.Where(entry =>
                         entry.Revision != store.Revision
                     ))
            {
                cache.TryRemove(stale, out _);
            }
        }

        return plan;
    }

    private async Task<NorthernNavigationPlan> CalculatePlanAsync(
        Vector3 destination,
        uint territoryId
    )
    {
        if (!config.UseNorthernAethernetRoutes)
        {
            return NorthernNavigationPlan.Direct(
                float.PositiveInfinity,
                "北征魔路选路已关闭"
            );
        }

        var routes = store.GetRoutes(territoryId)
            .Where(route => route.Enabled)
            .ToList();
        var destinations = routes
            .Where(route =>
                route.HasArrival
                && !string.IsNullOrWhiteSpace(route.Name)
            )
            .OrderBy(route =>
                Vector3.Distance(
                    NorthernRouteStore.GetArrivalPosition(route),
                    destination
                )
            )
            .Take(3)
            .ToList();
        var source = GetCanonicalSource(routes, territoryId);
        if (destinations.Count == 0 || source == null)
        {
            return NorthernNavigationPlan.Direct(
                float.PositiveInfinity,
                "没有同时具备传送名称和到达坐标的已启用魔路"
            );
        }

        var directTask = GetPathLengthAsync(
            NorthernRouteStore.GetInteractionPosition(source),
            destination
        );
        var destinationTasks = destinations.ToDictionary(
            route => route.Id,
            route => GetPathLengthAsync(
                NorthernRouteStore.GetArrivalPosition(route),
                destination
            )
        );

        await Task.WhenAll(destinationTasks.Values.Append(directTask));

        var directCost = await directTask;
        var candidateCosts = destinations
            .Select(route => (Route: route, Cost: destinationTasks[route.Id].Result))
            .ToList();
        DebugLog.Debug(
            $"Northern route destination costs for {destination}: "
            + string.Join(
                " | ",
                candidateCosts.Select(candidate =>
                    $"{candidate.Route.Name}={candidate.Cost:F0}"
                )
            )
        );

        var bestDestination = candidateCosts
            .Where(candidate => float.IsFinite(candidate.Cost))
            .OrderBy(candidate => candidate.Cost)
            .FirstOrDefault();
        if (bestDestination.Route == null)
        {
            return NorthernNavigationPlan.Direct(
                directCost,
                "无法从任何魔路到达坐标寻路到目标"
            );
        }

        var teleportCost = Math.Max(0f, config.NorthernTeleportPenalty)
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
            source,
            bestDestination.Route,
            directCost,
            teleportCost,
            $"魔路更短（{bestDestination.Route.Name}）："
            + $"直走 {directCost:F0} / 魔路 {teleportCost:F0}"
        );
    }

    private NorthernAethernetRoute? GetCanonicalSource(
        IReadOnlyCollection<NorthernAethernetRoute> recordedRoutes,
        uint territoryId
    )
    {
        var builtInSource = NorthernRouteDefaults.SourceOnlyRoutes
            .FirstOrDefault(route =>
                route.TerritoryId == territoryId
            );
        if (builtInSource != null)
        {
            return builtInSource;
        }

        return recordedRoutes
            .OrderBy(route => route.Name, StringComparer.Ordinal)
            .FirstOrDefault();
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

    private readonly record struct CacheKey(
        uint TerritoryId,
        uint EventId,
        EventType EventType,
        int DestinationX,
        int DestinationZ,
        long Revision
    )
    {
        public static CacheKey Create(
            uint territoryId,
            uint eventId,
            EventType eventType,
            Vector3 destination,
            long revision
        )
        {
            return new CacheKey(
                territoryId,
                eventId,
                eventType,
                (int)MathF.Round(destination.X / 5f),
                (int)MathF.Round(destination.Z / 5f),
                revision
            );
        }
    }
}
