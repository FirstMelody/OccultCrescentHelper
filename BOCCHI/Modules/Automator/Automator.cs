using System.Linq;
using BOCCHI.Chains;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Modules.CriticalEncounters;
using BOCCHI.Modules.Fates;
using BOCCHI.Modules.StateManager;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Ocelot.Chain;
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

    public void PostUpdate(AutomatorModule module, IFramework framework)
    {
        var vnav = module.GetIPCSubscriber<VNavmesh>();
        var lifestream = module.GetIPCSubscriber<Lifestream>();
        if (!vnav.IsReady() || !lifestream.IsReady())
        {
            return;
        }

        var states = module.GetModule<StateManagerModule>();
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
                    .Where(ev => ev.State != DynamicEventState.Inactive)
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
            Plugin.Chain.Abort();
            vnav.Stop();
            Activity = null;
        }

        if (IsChainActive)
        {
            return;
        }

        if (Activity != null)
        {
            if (Activity.state == ActivityState.Done)
            {
                Activity = null;
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
            Svc.Log.Info($"Selected activity: {Activity.GetName()}");
            return;
        }

        // North aethernet IDs and destinations are not known before release.
        // Stay idle instead of returning to coordinates from South Horn.
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
            Radius = 19f,
        };
    }

    public void Refresh()
    {
        Activity = null;
        idleTime = 0;
    }
}
