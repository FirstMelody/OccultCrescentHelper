using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BOCCHI.Data.Traps;
using BOCCHI.Enums;
using BOCCHI.Modules.Data;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Colors;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using Ocelot.Modules;
using Ocelot.Windows;

namespace BOCCHI.Modules.ForkedTower;

public class TowerRun(string hash)
{
    public readonly string Hash = hash;

    private readonly HashSet<string> DiscoveredTraps = [];

    private readonly HashSet<string> DiscoveredCandidates = [];

    private readonly Dictionary<string, TrackedGroup> TrackedGroups = [];

    public bool HasDiscoveredAllTraps(TrapGroup group)
    {
        if (TrackedGroups.TryGetValue(group.GetKey(), out var trackedGroup))
        {
            return trackedGroup.HasDiscoveredAllTraps();
        }

        return false;
    }

    public bool HasDiscoveredTrap(Vector3 position, OccultObjectType type)
    {
        return DiscoveredTraps.Contains(new TrapDatum(position, type).GetKey());
    }

    public void ObserveCandidate(uint baseId, Vector3 position)
    {
        DiscoveredCandidates.Add(GetCandidateKey(baseId, position));
    }

    public bool HasObservedCandidate(uint baseId, Vector3 position)
    {
        return DiscoveredCandidates.Contains(GetCandidateKey(baseId, position));
    }

    private static string GetCandidateKey(uint baseId, Vector3 position)
    {
        var x = (float)System.Math.Round(position.X, 2);
        var y = (float)System.Math.Round(position.Y, 2);
        var z = (float)System.Math.Round(position.Z, 2);
        return System.FormattableString.Invariant(
            $"{baseId}:{x:F2},{y:F2},{z:F2}"
        );
    }

    public void Update(UpdateContext context)
    {
        foreach (var trap in GetNearbyTraps())
        {
            var trapKey = trap.GetKey();
            if (!DiscoveredTraps.Add(trapKey))
            {
                continue;
            }

            var group = TrapData.GetGroup(trap);

            if (!TrackedGroups.TryGetValue(group.GetKey(), out var trackedGroup))
            {
                trackedGroup = new TrackedGroup(group);
                TrackedGroups.Add(group.GetKey(), trackedGroup);
            }

            trackedGroup.Observe(trapKey);
        }
    }

    public void Render(RenderContext context)
    {
        if (context.Config is not Config config)
        {
            return;
        }

        foreach (var trap in GetNearbyTraps())
        {
            if (Player.DistanceTo(trap) > config.ForkedTowerConfig.TrapDrawRange)
            {
                continue;
            }

            if (config.ForkedTowerConfig.DrawSmallTrapRange && trap.BaseId == (uint)OccultObjectType.Trap)
            {
                context.DrawCircle(trap.Position, 7f, ImGuiColors.DPSRed);
            }

            if (config.ForkedTowerConfig.DrawBigTrapRange && trap.BaseId == (uint)OccultObjectType.BigTrap)
            {
                context.DrawCircle(trap.Position, 30f, ImGuiColors.DalamudOrange);
            }
        }
    }

    private IEnumerable<IEventObj> GetNearbyTraps()
    {
        return Svc.Objects.OfType<IEventObj>().Where(o => o.BaseId is (uint)OccultObjectType.Trap or (uint)OccultObjectType.BigTrap);
    }
}
