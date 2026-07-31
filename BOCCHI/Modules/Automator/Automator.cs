using System;
using System.Linq;
using System.Numerics;
using BOCCHI.ActionHelpers;
using BOCCHI.Chains;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Modules.CriticalEncounters;
using BOCCHI.Modules.Fates;
using BOCCHI.Modules.NorthernRoutes;
using BOCCHI.Modules.StateManager;
using ECommons.Automation.NeoTaskManager;
using ECommons.GameHelpers;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Ocelot.Chain;
using Ocelot.Chain.ChainEx;
using Ocelot.IPC;

namespace BOCCHI.Modules.Automator;

public class Automator
{
    private static bool IsChainActive
    {
        get => ChainManager.Queues.Count > 0;
    }

    public Activity? Activity { get; private set; } = null;

    private int idleTime = 0;
    private bool northernImmediateReturnInProgress;
    private DateTime nextNorthernCandidateWarmupAt = DateTime.MinValue;

    public void PostUpdate(AutomatorModule module, IFramework framework)
    {
        var vnav = module.GetIPCSubscriber<VNavmesh>();
        var lifestream = module.GetIPCSubscriber<Lifestream>();
        if (!vnav.IsReady()
            || (!ZoneData.IsInNorthernExpedition() && !lifestream.IsReady()))
        {
            return;
        }

        var states = module.GetModule<StateManagerModule>();
        if (northernImmediateReturnInProgress)
        {
            return;
        }

        if (Activity != null)
        {
            WarmNextNorthernActivityPlan(module);
        }

        if (Activity != null && !Activity.IsEnabled())
        {
            var disabledActivity = Activity;
            module.Debug(
                $"Selected activity was disabled; canceling immediately: "
                + $"{disabledActivity.GetName()} "
                + $"(type={disabledActivity.data.Type}, "
                + $"id={disabledActivity.data.Id}, "
                + $"state={disabledActivity.state})"
            );
            module.NorthernRoutePlanner.CancelActiveReturn();
            Plugin.Chain.Abort();
            vnav.Stop();
            if (module.Config.ShouldToggleAiProvider)
            {
                module.Config.AiProvider.Off();
            }

            Activity = null;
        }

        if (Activity == null)
        {
            if (states.GetState() == State.InCombat)
            {
                return;
            }

            if (states.GetState() == State.InCriticalEncounter)
            {
                var critical = module.GetModule<CriticalEncountersModule>();
                var activeEncounters = critical.CriticalEncounters.Values
                    .Where(ev =>
                        ev.EventType < 4
                        && ev.State != DynamicEventState.Inactive
                    )
                    .ToList();
                if (activeEncounters.Count == 0)
                {
                    return;
                }

                var encounter = activeEncounters[^1];
                var data = GetCriticalEncounterData(encounter);
                Activity = new CriticalEncounter(data, lifestream, vnav, module, critical);

                if (Activity != null)
                {
                    module.Debug($"Resuming running activity: {Activity.GetName()}");
                }

                return;
            }

            if (states.GetState() == State.InFate)
            {
                Activity ??= FindFate(module, lifestream, vnav);

                if (Activity != null)
                {
                    module.Debug($"Resuming running activity: {Activity.GetName()}");
                }

                return;
            }
        }

        if (Activity != null && !Activity.IsValid())
        {
            var finishedActivity = Activity;
            module.Debug(
                $"Selected activity is no longer present: "
                + $"{finishedActivity.GetName()} "
                + $"(type={finishedActivity.data.Type}, "
                + $"id={finishedActivity.data.Id}, "
                + $"state={finishedActivity.state})"
            );
            module.NorthernRoutePlanner.CancelActiveReturn();
            Plugin.Chain.Abort();
            vnav.Stop();
            Activity = null;

            // North event rows disappear as soon as the event expires. This
            // can happen while we are still routing to it, not only after the
            // participation chain starts. Treat every invalidated selected
            // event as completed so navigation cannot fall into an idle gap.
            if (ZoneData.IsInNorthernExpedition())
            {
                StartImmediateNorthernReturn(
                    module,
                    vnav,
                    finishedActivity
                );
            }
        }

        if (IsChainActive)
        {
            return;
        }

        if (Activity != null)
        {
            if (Activity.state == ActivityState.Done)
            {
                var finishedActivity = Activity;
                Activity = null;
                if (ZoneData.IsInNorthernExpedition())
                {
                    StartImmediateNorthernReturn(
                        module,
                        vnav,
                        finishedActivity
                    );
                }

                return;
            }

            var chain = Activity.GetChain(states);
            if (chain == null)
            {
                return;
            }

            Plugin.Chain.Submit(chain);
            return;
        }

        if (!module.Config.ShouldDoFates && !module.Config.ShouldDoCriticalEncounters)
        {
            return;
        }

        // Try and get the next activity
        Activity ??= module.Config.ShouldDoCriticalEncounters ? FindCriticalEncounter(module, lifestream, vnav) : null;
        Activity ??= module.Config.ShouldDoFates ? FindFate(module, lifestream, vnav) : null;
        if (Activity != null)
        {
            DebugLog.Info(
                $"Selected activity: {Activity.GetName()} "
                + $"(type={Activity.data.Type}, id={Activity.data.Id})"
            );
            return;
        }

        if (ZoneData.IsInNorthernExpedition())
        {
            return;
        }

        var closest = AethernetData.GetClosestToPlayer();
        if (closest.DistanceToPlayer() <= 4.5f)
        {
            return;
        }

        idleTime += framework.UpdateDelta.Milliseconds;
        if (idleTime > 3000)
        {
            idleTime = 0;

            Plugin.Chain.Submit(ChainHelper.ReturnChain(new ReturnChainConfig { ApproachAetheryte = true }));
        }
    }

    private static CriticalEncounter? FindCriticalEncounter(AutomatorModule module, Lifestream lifestream, VNavmesh vnav)
    {
        if (!module.TryGetModule<CriticalEncountersModule>(out var source) || source == null)
        {
            return null;
        }

        foreach (var encounter in source.CriticalEncounters.Values)
        {
            if (encounter.EventType >= 4)
            {
                continue;
            }

            if (!module.Config.IsCriticalEncounterEnabled(
                    Svc.ClientState.TerritoryType,
                    encounter.DynamicEventId
                ))
            {
                continue;
            }

            if (encounter.State != DynamicEventState.Register)
            {
                continue;
            }

            var data = GetCriticalEncounterData(encounter);
            return new CriticalEncounter(data, lifestream, vnav, module, source);
        }

        return null;
    }

    private static FateActivity? FindFate(AutomatorModule module, Lifestream lifestream, VNavmesh vnav)
    {
        if (!module.TryGetModule<FatesModule>(out var source) || source == null)
        {
            return null;
        }

        foreach (var fate in source.fates.Values)
        {
            // Newly spawned dynamic FATE rows can briefly expose a zero position.
            // Wait for the live row to populate instead of planning toward world origin.
            if (fate.StartPosition == Vector3.Zero)
            {
                continue;
            }

            if (!module.Config.IsFateEnabled(Svc.ClientState.TerritoryType, fate.Id))
            {
                continue;
            }

            return new FateActivity(fate.Data, lifestream, vnav, module, fate);
        }

        return null;
    }

    private static EventData GetCriticalEncounterData(DynamicEvent encounter)
    {
        if (EventData.CriticalEncounters.TryGetValue(encounter.DynamicEventId, out var knownData))
        {
            return knownData;
        }

        return new EventData
        {
            Id = encounter.DynamicEventId,
            Type = EventType.CriticalEncounter,
            InternalName = encounter.Name.ToString(),
            StartPosition = encounter.MapMarker.Position,
        };
    }

    public void Refresh()
    {
        Activity = null;
        idleTime = 0;
        northernImmediateReturnInProgress = false;
        nextNorthernCandidateWarmupAt = DateTime.MinValue;
    }

    private void StartImmediateNorthernReturn(
        AutomatorModule module,
        VNavmesh vnav,
        Activity finishedActivity
    )
    {
        if (!module.Config.ReturnToNorthernStandby
            || northernImmediateReturnInProgress)
        {
            return;
        }

        if (module.NorthernRoutePlanner.IsNearSourceCrystal(
                Svc.ClientState.TerritoryType
            ))
        {
            module.Debug(
                "Activity finished near the source crystal; skipping Return"
            );
            module.NorthernRoutePlanner.MarkReturnedToSource();
            return;
        }

        northernImmediateReturnInProgress = true;
        var wasMounted = false;
        NorthernReturnState? returnState = null;
        module.Debug(
            $"Activity finished; returning immediately before routing: "
            + finishedActivity.GetName()
        );

        var chain = Chain.Create("Illegal:NorthImmediateReturn")
            .ConditionalThen(
                _ => module.Config.ShouldToggleAiProvider,
                _ => module.Config.AiProvider.Off()
            )
            .Then(_ => vnav.Stop())
            .Then(_ =>
            {
                wasMounted = Player.Mounted;
                Actions.TryUnmount();
            })
            .ConditionalWait(_ => wasMounted, 500)
            .Then(_ =>
                returnState =
                    new NorthernReturnState(module.NorthernRoutePlanner)
            )
            .Then(new TaskManagerTask(
                () => returnState?.Update() == true,
                new TaskManagerConfiguration
                {
                    TimeLimitMS = 45000,
                    ShowError = false,
                }
            ))
            .ConditionalThen(
                _ => returnState?.SkippedNearSource == true,
                _ =>
                    module.Debug(
                        "Source crystal appeared before Return; skipping Return"
                    )
            )
            .OnFinally(() =>
            {
                if (returnState?.Completed == true)
                {
                    module.NorthernRoutePlanner.MarkReturnedToSource();
                }

                northernImmediateReturnInProgress = false;
            });
        Plugin.Chain.Submit(() => chain);
    }

    private void WarmNextNorthernActivityPlan(AutomatorModule module)
    {
        if (!ZoneData.IsInNorthernExpedition()
            || DateTime.UtcNow < nextNorthernCandidateWarmupAt)
        {
            return;
        }

        nextNorthernCandidateWarmupAt =
            DateTime.UtcNow + TimeSpan.FromSeconds(1);

        var currentActivity = Activity;
        if (module.Config.ShouldDoCriticalEncounters
            && module.TryGetModule<CriticalEncountersModule>(out var critical)
            && critical != null)
        {
            foreach (var encounter in critical.CriticalEncounters.Values)
            {
                if (encounter.EventType >= 4
                    || encounter.State != DynamicEventState.Register
                    || (currentActivity?.data.Type == EventType.CriticalEncounter
                        && currentActivity.data.Id == encounter.DynamicEventId)
                    || !module.Config.IsCriticalEncounterEnabled(
                        Svc.ClientState.TerritoryType,
                        encounter.DynamicEventId
                    ))
                {
                    continue;
                }

                var destination = encounter.MapMarker.Position;
                if (destination == Vector3.Zero)
                {
                    continue;
                }

                _ = module.NorthernRoutePlanner.PlanAsync(
                    Player.Position,
                    destination,
                    Svc.ClientState.TerritoryType,
                    encounter.DynamicEventId,
                    EventType.CriticalEncounter
                );
                return;
            }
        }

        if (!module.Config.ShouldDoFates
            || !module.TryGetModule<FatesModule>(out var fates)
            || fates == null)
        {
            return;
        }

        foreach (var fate in fates.fates.Values)
        {
            if ((currentActivity?.data.Type == EventType.Fate
                 && currentActivity.data.Id == fate.Id)
                || fate.StartPosition == Vector3.Zero
                || !module.Config.IsFateEnabled(
                    Svc.ClientState.TerritoryType,
                    fate.Id
                ))
            {
                continue;
            }

            _ = module.NorthernRoutePlanner.PlanAsync(
                Player.Position,
                fate.StartPosition,
                Svc.ClientState.TerritoryType,
                fate.Id,
                EventType.Fate
            );
            return;
        }
    }

}
