using System;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;

namespace BOCCHI.Data;

/// <summary>
/// Prevents independent UiBuilder/render callbacks from reading the live object
/// table while a territory transition is rebuilding native game objects.
/// </summary>
public static class WorldObjectScanGuard
{
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(3);
    private static readonly object Sync = new();

    private static uint observedTerritoryId;
    private static uint observedMapId;
    private static DateTime safeAfter = DateTime.MaxValue;

    public static bool IsSafe()
    {
        lock (Sync)
        {
            var now = DateTime.UtcNow;
            var territoryId = Svc.ClientState.TerritoryType;
            var mapId = Svc.ClientState.MapId;
            var changed = territoryId != observedTerritoryId
                          || mapId != observedMapId;
            if (changed)
            {
                observedTerritoryId = territoryId;
                observedMapId = mapId;
                safeAfter = now + SettleTime;
            }

            var transitionActive =
                !Svc.ClientState.IsLoggedIn
                || territoryId == 0
                || mapId == 0
                || Svc.Objects.LocalPlayer == null
                || Svc.Condition[ConditionFlag.BetweenAreas]
                || Svc.Condition[ConditionFlag.BetweenAreas51]
                || Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent]
                || Svc.Condition[ConditionFlag.WatchingCutscene]
                || Svc.Condition[ConditionFlag.WatchingCutscene78];
            if (transitionActive)
            {
                safeAfter = now + SettleTime;
                return false;
            }

            return now >= safeAfter;
        }
    }
}
