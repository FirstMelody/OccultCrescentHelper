using System;
using System.Numerics;
using System.Threading.Tasks;
using BOCCHI.ActionHelpers;
using BOCCHI.Chains;
using BOCCHI.Data;
using BOCCHI.Enums;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using Ocelot.Chain;
using Ocelot.Chain.ChainEx;
using Ocelot.IPC;

namespace BOCCHI.Modules.NorthernRoutes;

public sealed class NorthernRouteNavigationChain(
    NorthernRoutePlanner planner,
    VNavmesh vnav,
    Vector3 destination,
    EventData data
) : ChainFactory
{
    private const float SourceNearbyDistance = 25f;

    protected override Chain Create(Chain chain)
    {
        Task<NorthernNavigationPlan>? planTask = null;
        var wasMountedBeforeReturn = false;
        var alreadyReturnedToSource = planner.ConsumeReturnedToSource();
        NorthernReturnState? returnState = null;
        var plan = NorthernNavigationPlan.Direct(float.PositiveInfinity, "尚未计算");

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
                _ => plan is { UseTeleport: true, SourceRoute: not null }
                     && !alreadyReturnedToSource
                     && !planner.IsNearSourceCrystal(
                         Svc.ClientState.TerritoryType
                     )
                     && Player.DistanceTo(
                         NorthernRouteStore.GetInteractionPosition(
                             plan.SourceRoute
                         )
                     ) > SourceNearbyDistance,
                () => Chain.Create("NorthernRouteReturnToSource")
                    .Then(_ => vnav.Stop())
                    .Then(_ =>
                    {
                        DebugLog.Debug(
                            "Northern navigation: 当前不在出生魔路附近，先使用返回"
                        );
                        wasMountedBeforeReturn = Player.Mounted;
                        Actions.TryUnmount();
                    })
                    .ConditionalWait(_ => wasMountedBeforeReturn, 500)
                    .Then(_ =>
                        returnState = new NorthernReturnState(planner)
                    )
                    .Then(new TaskManagerTask(
                        () => returnState?.Update() == true,
                        new TaskManagerConfiguration
                        {
                            TimeLimitMS = 45000,
                            ShowError = false,
                        }
                    ))
            )
            .ConditionalThen(
                _ => alreadyReturnedToSource,
                _ => DebugLog.Debug(
                    "Northern navigation: event completion Return already "
                    + "reached the source; skipping duplicate Return"
                )
            )
            .ConditionalThen(
                _ => plan is { UseTeleport: true, SourceRoute: not null }
                     && !alreadyReturnedToSource
                     && planner.IsNearSourceCrystal(
                         Svc.ClientState.TerritoryType
                     ),
                _ => DebugLog.Debug(
                    "Northern navigation: source crystal is nearby; "
                    + "skipping Return"
                )
            )
            .ConditionalThen(
                _ => plan is { UseTeleport: true, SourceRoute: not null },
                () =>
                {
                    var sourcePosition =
                        NorthernRouteStore.GetInteractionPosition(
                            plan.SourceRoute!
                        );
                    return Chain.Create("NorthernRouteApproachSource")
                        .ConditionalThen(
                            _ => Player.DistanceTo(sourcePosition) > 4.3f,
                            _ => Chain.Create()
                                .Then(new PathfindAndMoveToChain(
                                    vnav,
                                    sourcePosition
                                ))
                                .WaitUntilNear(vnav, sourcePosition, 4.3f)
                                .Then(_ => vnav.Stop())
                        );
                }
            )
            .ConditionalThen(
                _ => plan is
                {
                    UseTeleport: true,
                    SourceRoute: not null,
                    DestinationRoute: not null,
                },
                () => Chain.Create()
                    .Then(__ => vnav.Stop())
                    .Then(new NorthernAethernetTeleportChain(
                        plan.SourceRoute!,
                        plan.DestinationRoute!
                    ))
            )
            .Then(new PathfindingChain(vnav, destination, data));
    }
}
