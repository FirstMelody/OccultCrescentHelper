using System;
using System.Linq;
using System.Numerics;
using BOCCHI.ActionHelpers;
using BOCCHI.Data;
using BOCCHI.Modules.Fates;
using BOCCHI.Modules.StateManager;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Ocelot.Chain;
using Ocelot.IPC;

namespace BOCCHI.Modules.Automator;

public class FateActivity(EventData data, Lifestream lifestream, VNavmesh vnav, AutomatorModule module, Fate fate)
    : Activity(data, lifestream, vnav, module)
{
    private const float TankRangedOpenerRange = 20f;

    protected override TaskManagerTask GetPathfindingWatcher(StateManagerModule states)
    {
        var lastTargetPos = Vector3.Zero;

        return new TaskManagerTask(() =>
        {
            if (EzThrottler.Throttle("FatePathfindingWatcher.EnemyScan", 100))
            {
                var enemy = GetEnemies().Centroid();
                if (enemy != null)
                {
                    // Replace stale/self/other targets as soon as a targetable
                    // enemy belonging to this FATE becomes available.
                    Svc.Targets.Target = enemy;

                    if (Vector3.Distance(enemy.Position, lastTargetPos) > 5f)
                    {
                        vnav.PathfindAndMoveTo(enemy.Position, false);
                        lastTargetPos = enemy.Position;
                    }

                    // IsTargetable only means that the client permits selecting
                    // the object; it can become true well outside combat range.
                    var surfaceDistance =
                        Vector3.Distance(Player.Position, enemy.Position)
                        - enemy.HitboxRadius;
                    if (surfaceDistance <= module.Config.EngagementRange)
                    {
                        module.Debug(
                            $"FATE target in engagement range: "
                            + $"{enemy.Name.TextValue} ({surfaceDistance:F1})"
                        );
                        Actions.TryUnmount();
                        vnav.Stop();
                        return true;
                    }
                }
            }

            if (!vnav.IsRunning())
            {
                throw new VnavmeshStoppedException();
            }

            return false;
        }, new TaskManagerConfiguration { TimeLimitMS = 180000, ShowError = false });
    }

    protected override float GetRadius()
    {
        return module.GetModule<FatesModule>().fates[data.Id].Radius;
    }

    protected override Func<Chain> GetArrivalChain()
    {
        return () => Chain.Create("Illegal:FateArrival")
            .Then(_ =>
            {
                vnav.Stop();
                Actions.TryUnmount();
            })
            .ConditionalWait(_ => Player.Mounted, 600)
            .Then(_ => TryTankRangedOpenerOnArrival());
    }

    public override bool IsValid()
    {
        return Svc.Fates.Any(f => f.FateId == fate.Id);
    }

    public override bool IsEnabled()
    {
        return module.Config.ShouldDoFates
               && module.Config.IsFateEnabled(
                   Svc.ClientState.TerritoryType,
                   fate.Id
               );
    }

    protected override bool IsParticipationComplete()
    {
        return module.Config.ReturnToNorthernStandby
               && fate.CurrentProgress >= 100;
    }

    protected override Vector3 GetPosition()
    {
        return fate.StartPosition;
    }

    public override string GetName()
    {
        return fate.Name;
    }

    protected override unsafe bool IsActivityTarget(IBattleNpc obj)
    {
        try
        {
            var battleChara = (BattleChara*)obj.Address;

            return battleChara->FateId == data.Id;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex.Message);
            return false;
        }
    }

    protected override ActivityState GetPostPathfindingState()
    {
        return ActivityState.Participating;
    }

    private void TryTankRangedOpenerOnArrival()
    {
        var player = Svc.Objects.LocalPlayer;
        if (player == null)
        {
            return;
        }

        var opener = GetTankRangedOpener(player.ClassJob.RowId);
        if (opener == null)
        {
            return;
        }

        var enemy = Svc.Targets.Target as IBattleNpc;
        if (enemy == null
            || !enemy.IsTargetable
            || !IsActivityTarget(enemy))
        {
            enemy = GetEnemies().Closest();
        }

        if (enemy == null || !enemy.IsTargetable)
        {
            module.Debug(
                "FATE tank ranged opener skipped: no targetable FATE enemy"
            );
            return;
        }

        var surfaceDistance =
            Vector3.Distance(Player.Position, enemy.Position)
            - enemy.HitboxRadius;
        var (action, actionName) = opener.Value;
        if (surfaceDistance > TankRangedOpenerRange
            || !action.CanCast())
        {
            module.Debug(
                $"FATE tank ranged opener unavailable: {actionName}, "
                + $"distance={surfaceDistance:F1}"
            );
            return;
        }

        Svc.Targets.Target = enemy;
        action.Cast();
        module.Debug(
            $"FATE tank ranged opener: {actionName} -> "
            + $"{enemy.Name.TextValue} "
            + $"({surfaceDistance:F1})"
        );
    }

    private static (
        BOCCHI.ActionHelpers.Action Action,
        string Name
    )? GetTankRangedOpener(uint classJobId)
    {
        return classJobId switch
        {
            1 or 19 => (Actions.Tank.ShieldLob, "投盾"),
            3 or 21 => (Actions.Tank.Tomahawk, "飞斧"),
            32 => (Actions.Tank.Unmend, "伤残"),
            37 => (Actions.Tank.LightningShot, "闪雷弹"),
            _ => null,
        };
    }
}
