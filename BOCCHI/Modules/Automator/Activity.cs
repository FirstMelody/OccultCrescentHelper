using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BOCCHI.Chains;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Modules.StateManager;
using BOCCHI.Modules.NorthernRoutes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using Ocelot.Chain;
using Ocelot.IPC;

namespace BOCCHI.Modules.Automator;

public abstract class Activity
{
    public readonly EventData data;

    private readonly Lifestream lifestream;

    protected readonly VNavmesh vnav;

    protected readonly AutomatorModule module;

    public ActivityState state = ActivityState.Idle;

    protected readonly Dictionary<ActivityState, Func<StateManagerModule, Func<Chain>?>> handlers;

    protected Activity(EventData data, Lifestream lifestream, VNavmesh vnav, AutomatorModule module)
    {
        this.data = data;
        this.lifestream = lifestream;
        this.vnav = vnav;
        this.module = module;

        handlers = new Dictionary<ActivityState, Func<StateManagerModule, Func<Chain>?>>
        {
            { ActivityState.Idle, GetIdleChain },
            { ActivityState.Pathfinding, GetPathfindingChain },
            { ActivityState.Participating, GetParticipatingChain },
            { ActivityState.Done, GetDoneChain },
        };

        var states = module.GetModule<StateManagerModule>();
        if (states.GetState() == State.InFate || states.GetState() == State.InCriticalEncounter)
        {
            state = ActivityState.Participating;
        }
    }


    public Func<Chain>? GetChain(StateManagerModule states)
    {
        return !IsValid() ? null : handlers[state](states);
    }

    private Func<Chain> GetIdleChain(StateManagerModule states)
    {
        return () =>
        {
            bool ShouldToggleAi(ChainContext _)
            {
                return module.Config.ShouldToggleAiProvider && !Svc.Condition[ConditionFlag.InCombat];
            }

            return Chain.Create("Illegal:Idle")
                .ConditionalThen(ShouldToggleAi, _ => module.Config.AiProvider.Off())
                .Then(_ => vnav.Stop())
                .Then(_ => state = ActivityState.Pathfinding);
        };
    }

    private Func<Chain> GetPathfindingChain(StateManagerModule states)
    {
        return () =>
        {
            var isFate = data.Type == EventType.Fate;

            if (ZoneData.IsInNorthernExpedition())
            {
                return Chain.Create("Illegal:Pathfinding:North")
                    .ConditionalWait(
                        _ => !isFate && module.Config.ShouldDelayCriticalEncounters,
                        Random.Shared.Next(10000, 15001)
                    )
                    .Then(new NorthernRouteNavigationChain(
                        module.NorthernRoutePlanner,
                        vnav,
                        GetPosition(),
                        data
                    ))
                    .ConditionalThen(
                        _ => ShouldMountToPathfindTo(GetPosition()),
                        ChainHelper.MountChain()
                    )
                    .Then(GetPathfindingWatcher(states))
                    .Then(GetArrivalChain())
                    .Then(_ => state = GetPostPathfindingState());
            }

            var playerShard = AethernetData.AllByDistance().First();
            var activityShard = GetAethernetData();
            var navType = SmartNavigation.Decide(Player.Position, GetPosition(), activityShard);

            module.Debug("Selected navigation type: " + navType);

            var chain = Chain.Create("Illegal:Pathfinding")
                .ConditionalWait(_ => !isFate && module.Config.ShouldDelayCriticalEncounters, Random.Shared.Next(10000, 15001));

            switch (navType)
            {
                case NavigationType.Walk:
                    chain
                        .Then(new PathfindingChain(vnav, GetPosition(), data))
                        .ConditionalThen(_ => ShouldMountToPathfindTo(GetPosition()), ChainHelper.MountChain());
                    break;

                case NavigationType.ReturnWalk:
                    chain
                        .Then(ChainHelper.ReturnChain())
                        .Then(new PathfindingChain(vnav, GetPosition(), data))
                        .ConditionalThen(_ => ShouldMountToPathfindTo(GetPosition()), ChainHelper.MountChain());
                    break;

                case NavigationType.ReturnTeleportWalk:
                    chain
                        .Then(ChainHelper.ReturnChain(new ReturnChainConfig { ApproachAetheryte = true }))
                        .Then(ChainHelper.TeleportChain(activityShard.Aethernet))
                        .Debug("Waiting for lifestream to not be 'busy'")
                        .Then(new TaskManagerTask(() => !lifestream.IsBusy(), new TaskManagerConfiguration { TimeLimitMS = 30000 }))
                        .Then(new PathfindingChain(vnav, GetPosition(), data))
                        .ConditionalThen(_ => ShouldMountToPathfindTo(GetPosition()), ChainHelper.MountChain());
                    break;

                case NavigationType.WalkTeleportWalk:
                    chain
                        .Then(ChainHelper.PathfindToAndWait(playerShard.Position, AethernetData.DISTANCE))
                        .Then(ChainHelper.TeleportChain(activityShard.Aethernet))
                        .Debug("Waiting for lifestream to not be 'busy'")
                        .Then(new TaskManagerTask(() => !lifestream.IsBusy(), new TaskManagerConfiguration { TimeLimitMS = 30000 }))
                        .Then(new PathfindingChain(vnav, GetPosition(), data))
                        .ConditionalThen(_ => ShouldMountToPathfindTo(GetPosition()), ChainHelper.MountChain());
                    break;
            }

            chain
                .Then(GetPathfindingWatcher(states))
                .Then(GetArrivalChain())
                .Then(_ => state = GetPostPathfindingState());

            return chain;
        };
    }


    private Func<Chain> GetParticipatingChain(StateManagerModule states)
    {
        return () =>
        {
            var participationState = data.Type == EventType.Fate
                ? State.InFate
                : State.InCriticalEncounter;
            var hasEnteredParticipation =
                states.GetState() == participationState;

            bool IsParticipationFinished()
            {
                var currentState = states.GetState();
                hasEnteredParticipation |= currentState == participationState;

                return !IsValid()
                       || IsParticipationComplete()
                       || (hasEnteredParticipation
                           && currentState == State.Idle);
            }

            return Chain.Create("Illegal:Participating")
                .ConditionalThen(_ => module.Config.ShouldToggleAiProvider, _ => module.Config.AiProvider.On())
                .Then(_ => vnav.Stop())
                .Then(new TaskManagerTask(() =>
                {
                    if (!module.Config.ShouldForceTarget || !EzThrottler.Throttle("Participating.ForceTarget", 500))
                    {
                        return IsParticipationFinished();
                    }

                    var enemies = GetEnemies();
                    Svc.Targets.Target = module.Config.ShouldForceTargetCentralEnemy ? enemies.Centroid() : enemies.Closest();

                    return IsParticipationFinished();
                }, new TaskManagerConfiguration { TimeLimitMS = int.MaxValue }))
                .Then(_ => state = ActivityState.Done);
        };
    }

    private Func<Chain>? GetDoneChain(StateManagerModule states)
    {
        return null;
    }

    protected List<IBattleNpc> GetEnemies()
    {
        return TargetHelper.Enemies.Where(IsActivityTarget).ToList();
    }

    protected abstract bool IsActivityTarget(IBattleNpc obj);

    protected virtual bool IsParticipationComplete()
    {
        return false;
    }

    private AethernetData GetAethernetData()
    {
        return data.Aethernet?.GetData() ?? AethernetData.AllByDistance(GetPosition()).First();
    }

    protected bool IsInZone()
    {
        var radius = data.Radius ?? GetRadius();

        return Player.DistanceTo(GetPosition()) <= radius;
    }

    private bool ShouldMountToPathfindTo(Vector3 destination)
    {
        if (!module.PluginConfig.TeleporterConfig.ShouldMount)
        {
            return false;
        }

        return Vector3.Distance(Player.Position, destination) > 20f;
    }

    protected abstract float GetRadius();

    protected abstract TaskManagerTask GetPathfindingWatcher(StateManagerModule states);

    protected virtual Func<Chain> GetArrivalChain()
    {
        return () => Chain.Create();
    }

    public abstract bool IsValid();

    public virtual bool IsEnabled()
    {
        return true;
    }

    protected abstract Vector3 GetPosition();

    public abstract string GetName();

    protected abstract ActivityState GetPostPathfindingState();
}
