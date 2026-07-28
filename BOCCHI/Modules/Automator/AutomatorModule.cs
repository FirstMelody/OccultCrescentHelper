using System;
using System.Collections.Generic;
using System.Linq;
using BOCCHI.Data;
using BOCCHI.Modules.CriticalEncounters;
using BOCCHI.Modules.Fates;
using BOCCHI.Modules.StateManager;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Ocelot;
using Ocelot.IPC;
using Ocelot.Modules;
using Ocelot.Windows;

namespace BOCCHI.Modules.Automator;

[OcelotModule(int.MaxValue - 1)]
public class AutomatorModule : Module
{
    public override AutomatorConfig Config
    {
        get => PluginConfig.AutomatorConfig;
    }

    public override bool IsEnabled
    {
        get => Config.IsPropertyEnabled(nameof(Config.Enabled));
    }

    public readonly Automator automator = new();

    public readonly Panel panel = new();

    public IReadOnlyDictionary<uint, string> ActiveCriticalEncounterNames
    {
        get => activeCriticalEncounterNames;
    }

    public IReadOnlyDictionary<uint, string> ActiveFateNames
    {
        get => activeFateNames;
    }

    private Dictionary<uint, string> activeCriticalEncounterNames = [];
    private Dictionary<uint, string> activeFateNames = [];
    private DateTime nextEventNameScanAt = DateTime.MinValue;

    public AutomatorModule(Plugin plugin, Config config)
        : base(plugin, config)
    {
        Config.RecordedCriticalEncounterNames ??= [];
        Config.RecordedCriticalEncounterEnabled ??= [];
        Config.RecordedFateNames ??= [];
        Config.RecordedFateEnabled ??= [];

        Svc.PluginInterface.UiBuilder.Draw += RecordLiveEventNames;
        Svc.Framework.Update += NorthFrameworkUpdate;
    }


    public override void PostUpdate(UpdateContext context)
    {
        automator.PostUpdate(this, context.Framework);
    }


    public override bool RenderMainUi(RenderContext context)
    {
        panel.Draw(this);
        return true;
    }

    public override void OnTerritoryChanged(uint id)
    {
        activeCriticalEncounterNames.Clear();
        activeFateNames.Clear();

        if (ZoneData.IsPluginTerritory(id))
        {
            return;
        }

        automator.Refresh();
        Config.Enabled = false;
        PluginConfig.Save();
    }

    public static void ToggleIllegalMode(OcelotPlugin plugin)
    {
        var module = plugin.Modules.GetModule<AutomatorModule>();
        if (!module.Config.Enabled)
        {
            module.EnableIllegalMode();
        }
        else
        {
            module.DisableIllegalMode();
        }
    }

    public void EnableIllegalMode()
    {
        var wasDisabled = !Config.Enabled;
        Config.Enabled = true;
        PluginConfig.Save();

        if (wasDisabled)
        {
            Svc.Chat.Print(T("messages.on"));
        }
    }

    public void DisableIllegalMode()
    {
        var wasEnabled = Config.Enabled;
        Config.Enabled = false;
        automator.Refresh();
        Plugin.IPC.GetSubscriber<VNavmesh>().Stop();
        Plugin.Chain.Abort();
        PluginConfig.Save();

        if (wasEnabled)
        {
            Svc.Chat.Print(T("messages.off"));
        }
    }

    public IEnumerable<(uint Id, string Name, bool Enabled)> GetRecordedCriticalEncounters()
    {
        var territoryId = Svc.ClientState.TerritoryType;
        var prefix = $"{territoryId}:";
        return Config.RecordedCriticalEncounterNames
            .Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(entry =>
            {
                var id = uint.TryParse(entry.Key[prefix.Length..], out var parsed) ? parsed : 0;
                var enabled = !Config.RecordedCriticalEncounterEnabled.TryGetValue(
                    entry.Key,
                    out var configured
                ) || configured;
                return (Id: id, Name: entry.Value, Enabled: enabled);
            })
            .Where(entry => entry.Id != 0)
            .OrderBy(entry => entry.Name);
    }

    public IEnumerable<(uint Id, string Name, bool Enabled)> GetRecordedFates()
    {
        var territoryId = Svc.ClientState.TerritoryType;
        var prefix = $"{territoryId}:";
        return Config.RecordedFateNames
            .Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(entry =>
            {
                var id = uint.TryParse(entry.Key[prefix.Length..], out var parsed) ? parsed : 0;
                var enabled = !Config.RecordedFateEnabled.TryGetValue(
                    entry.Key,
                    out var configured
                ) || configured;
                return (Id: id, Name: entry.Value, Enabled: enabled);
            })
            .Where(entry => entry.Id != 0)
            .OrderBy(entry => entry.Name);
    }

    public void SetRecordedCriticalEncounterEnabled(uint eventId, bool enabled)
    {
        Config.RecordedCriticalEncounterEnabled[
            AutomatorConfig.GetEventKey(Svc.ClientState.TerritoryType, eventId)
        ] = enabled;
        PluginConfig.Save();
    }

    public void SetRecordedFateEnabled(uint eventId, bool enabled)
    {
        Config.RecordedFateEnabled[
            AutomatorConfig.GetEventKey(Svc.ClientState.TerritoryType, eventId)
        ] = enabled;
        PluginConfig.Save();
    }

    private unsafe void RecordLiveEventNames()
    {
        if (!ZoneData.IsInPluginTerritory())
        {
            activeCriticalEncounterNames.Clear();
            activeFateNames.Clear();
            return;
        }

        if (DateTime.UtcNow < nextEventNameScanAt)
        {
            return;
        }

        nextEventNameScanAt = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        var territoryId = Svc.ClientState.TerritoryType;
        var changed = false;
        var currentFates = new Dictionary<uint, string>();

        foreach (var fate in Svc.Fates)
        {
            try
            {
                var id = (uint)fate.FateId;
                var name = fate.Name.ToString();
                if (id == 0 || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                currentFates[id] = name;
                changed |= RecordEvent(
                    Config.RecordedFateNames,
                    Config.RecordedFateEnabled,
                    territoryId,
                    id,
                    name
                );
            }
            catch (AccessViolationException)
            {
                // The FATE despawned during enumeration; retry on the next scan.
            }
        }

        activeFateNames = currentFates;

        var currentCriticalEncounters = new Dictionary<uint, string>();
        var occultCrescent = PublicContentOccultCrescent.GetInstance();
        if (occultCrescent != null)
        {
            foreach (var encounter in occultCrescent->DynamicEventContainer.Events.ToArray())
            {
                if (encounter.EventType >= 4 || encounter.State == DynamicEventState.Inactive)
                {
                    continue;
                }

                var id = (uint)encounter.DynamicEventId;
                var name = encounter.Name.ToString();
                if (id == 0 || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                currentCriticalEncounters[id] = name;
                changed |= RecordEvent(
                    Config.RecordedCriticalEncounterNames,
                    Config.RecordedCriticalEncounterEnabled,
                    territoryId,
                    id,
                    name
                );
            }
        }

        activeCriticalEncounterNames = currentCriticalEncounters;
        if (changed)
        {
            PluginConfig.Save();
        }
    }

    private static bool RecordEvent(
        IDictionary<string, string> names,
        IDictionary<string, bool> enabledStates,
        uint territoryId,
        uint eventId,
        string name
    )
    {
        var key = AutomatorConfig.GetEventKey(territoryId, eventId);
        var changed = !names.TryGetValue(key, out var recordedName) || recordedName != name;
        names[key] = name;
        if (!enabledStates.ContainsKey(key))
        {
            enabledStates[key] = true;
            changed = true;
        }

        return changed;
    }

    private void NorthFrameworkUpdate(IFramework framework)
    {
        if (!ZoneData.IsInNorthernExpedition() || !Config.Enabled)
        {
            return;
        }

        var fates = GetModule<FatesModule>();
        var criticalEncounters = GetModule<CriticalEncountersModule>();
        var states = GetModule<StateManagerModule>();

        try
        {
            fates.tracker.Update();
            criticalEncounters.Tracker.Tick(framework);
            states.Tick();
            automator.PostUpdate(this, framework);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "North Illegal Mode update failed; disabling automation");
            DisableIllegalMode();
            Svc.Chat.PrintError("[BOCCHI] 北征 Illegal Mode 更新失败，已自动关闭以避免连续报错。");
        }
    }

    public override void Dispose()
    {
        Svc.PluginInterface.UiBuilder.Draw -= RecordLiveEventNames;
        Svc.Framework.Update -= NorthFrameworkUpdate;
        base.Dispose();
    }
}
