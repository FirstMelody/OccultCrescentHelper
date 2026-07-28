using System;
using System.Numerics;
using System.Threading.Tasks;
using BOCCHI.Chains;
using BOCCHI.Data;
using BOCCHI.Enums;
using Dalamud.Game.ClientState.Conditions;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using Ocelot.Chain;
using Ocelot.IPC;

namespace BOCCHI.Modules.NorthernRoutes;

public sealed class NorthernRouteNavigationChain(
    NorthernRoutePlanner planner,
    VNavmesh vnav,
    Lifestream lifestream,
    Vector3 destination,
    EventData data
) : ChainFactory
{
    protected override Chain Create(Chain chain)
    {
        Task<NorthernNavigationPlan>? planTask = null;
        var plan = NorthernNavigationPlan.Direct(float.PositiveInfinity, "尚未计算");
        var teleportIssued = false;
        var sawTeleportBusyState = false;
        var teleportRequestedAt = DateTime.MinValue;

        return chain
            .Then(_ =>
            {
                planTask = planner.PlanAsync(
                    Player.Position,
                    destination,
                    Svc.ClientState.TerritoryType,
                    data.Id,
                    data.Type
                );
            })
            .Then(new TaskManagerTask(
                () =>
                {
                    if (planTask == null || !planTask.IsCompleted)
                    {
                        return false;
                    }

                    plan = planTask.IsCompletedSuccessfully
                        ? planTask.Result
                        : NorthernNavigationPlan.Direct(
                            float.PositiveInfinity,
                            "选路计算失败，改为直走"
                        );
                    DebugLog.Debug($"Northern navigation: {plan.Reason}");
                    return true;
                },
                new TaskManagerConfiguration
                {
                    TimeLimitMS = 65000,
                    ShowError = false,
                }
            ))
            .ConditionalThen(
                _ => plan is { UseTeleport: true, SourceRoute: not null },
                ChainHelper.PathfindToAndWait(
                    plan.SourceRoute == null
                        ? Player.Position
                        : NorthernRouteStore.GetInteractionPosition(plan.SourceRoute),
                    4.3f
                )
            )
            .ConditionalThen(
                _ => plan is { UseTeleport: true, DestinationRoute: not null },
                _ =>
                {
                    vnav.Stop();
                    teleportIssued = planner.TryTeleport(plan.DestinationRoute!);
                    teleportRequestedAt = DateTime.UtcNow;
                }
            )
            .ConditionalThen(
                _ => teleportIssued,
                new TaskManagerTask(
                    () =>
                    {
                        var busy = lifestream.IsBusy()
                                   || Svc.Condition[ConditionFlag.BetweenAreas]
                                   || Svc.Condition[ConditionFlag.BetweenAreas51];
                        sawTeleportBusyState |= busy;
                        if (sawTeleportBusyState && !busy)
                        {
                            return true;
                        }

                        if (plan.DestinationRoute?.HasArrival == true
                            && Player.DistanceTo(
                                NorthernRouteStore.GetArrivalPosition(
                                    plan.DestinationRoute
                                )
                            ) <= 25f)
                        {
                            return true;
                        }

                        // A failed or unusually silent Lifestream request must not
                        // block Illegal Mode forever. Continue with direct vnav.
                        return DateTime.UtcNow - teleportRequestedAt
                               >= TimeSpan.FromSeconds(20);
                    },
                    new TaskManagerConfiguration
                    {
                        TimeLimitMS = 25000,
                        ShowError = false,
                    }
                )
            )
            .Then(new PathfindingChain(vnav, destination, data));
    }
}
