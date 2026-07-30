using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BOCCHI.Data;
using BOCCHI.Modules.CriticalEncounters;
using BOCCHI.Modules.Fates;
using BOCCHI.Modules.NorthernRoutes;
using BOCCHI.Modules.StateManager;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
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

    public NorthernRoutePlanner NorthernRoutePlanner { get; }

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

        NorthernRoutePlanner = new NorthernRoutePlanner(
            plugin.NorthernRoutes,
            Config,
            GetIPCSubscriber<VNavmesh>()
        );

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

        NorthernRoutePlanner.CancelActiveReturn();
        Plugin.Chain.Abort();
        Plugin.IPC.GetSubscriber<VNavmesh>().Stop();
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
        var shouldCancelReturnCast =
            NorthernRoutePlanner.IsReturnInProgress
            && IsCastingReturn();
        Config.Enabled = false;
        NorthernRoutePlanner.CancelActiveReturn();
        automator.Refresh();
        Plugin.IPC.GetSubscriber<VNavmesh>().Stop();
        Plugin.Chain.Abort();
        if (shouldCancelReturnCast)
        {
            CancelCurrentCast();
            Debug(
                "Emergency Stop canceled the active Northern Return cast"
            );
        }

        PluginConfig.Save();

        if (wasEnabled)
        {
            Svc.Chat.Print(T("messages.off"));
        }
    }

    private static unsafe bool IsCastingReturn()
    {
        var player = Svc.Objects.LocalPlayer;
        var actionManager = ActionManager.Instance();
        return player?.IsCasting == true
               && actionManager != null
               && actionManager->CastActionType
               == ActionType.GeneralAction
               && actionManager->CastActionId == 8;
    }

    private static unsafe void CancelCurrentCast()
    {
        var uiState = UIState.Instance();
        if (uiState != null)
        {
            uiState->Hotbar.CancelCast();
        }
    }

    public IEnumerable<(uint Id, string Name, bool Enabled)> GetRecordedCriticalEncounters()
    {
        return GetConfiguredEvents(
            Svc.ClientState.TerritoryType,
            NorthernEventCatalog.CriticalEncounters,
            Config.RecordedCriticalEncounterNames,
            Config.RecordedCriticalEncounterEnabled
        );
    }

    public IEnumerable<(uint Id, string Name, bool Enabled)> GetRecordedFates()
    {
        return GetConfiguredEvents(
            Svc.ClientState.TerritoryType,
            NorthernEventCatalog.Fates,
            Config.RecordedFateNames,
            Config.RecordedFateEnabled
        );
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

    private static IEnumerable<(uint Id, string Name, bool Enabled)> GetConfiguredEvents(
        uint territoryId,
        IReadOnlyDictionary<uint, string> northernCatalog,
        IReadOnlyDictionary<string, string> recordedNames,
        IReadOnlyDictionary<string, bool> recordedEnabled
    )
    {
        var events = territoryId == ZoneData.NORTHHORN
            ? northernCatalog.ToDictionary(entry => entry.Key, entry => entry.Value)
            : new Dictionary<uint, string>();
        var prefix = $"{territoryId}:";

        foreach (var entry in recordedNames.Where(entry =>
                     entry.Key.StartsWith(prefix, StringComparison.Ordinal)
                 ))
        {
            if (uint.TryParse(entry.Key[prefix.Length..], out var id) && id != 0)
            {
                events[id] = entry.Value;
            }
        }

        return events
            .Select(entry =>
            {
                var key = AutomatorConfig.GetEventKey(territoryId, entry.Key);
                var enabled = !recordedEnabled.TryGetValue(key, out var configured)
                              || configured;
                return (Id: entry.Key, Name: entry.Value, Enabled: enabled);
            })
            .OrderBy(entry => entry.Name);
    }

    public IReadOnlyList<NorthernAethernetRoute> GetNorthernRoutes()
    {
        return Plugin.NorthernRoutes.GetRoutes(Svc.ClientState.TerritoryType);
    }

    public NorthernStandbyPoint? GetNorthernStandbyPoint()
    {
        return Plugin.NorthernRoutes.GetStandbyPoint(Svc.ClientState.TerritoryType);
    }

    public bool RecordCurrentNorthernRoute(string requestedName, uint destinationId)
    {
        if (!ZoneData.IsInNorthernExpedition() || Svc.Objects.LocalPlayer == null)
        {
            Svc.Chat.PrintError("[BOCCHI] 请在北征之章内记录魔路。");
            return false;
        }

        var playerPosition = Player.Position;
        var candidate = Svc.Targets.Target;
        if (candidate == null || Vector3.Distance(candidate.Position, playerPosition) > 15f)
        {
            candidate = Svc.Objects
                .Where(obj =>
                    obj.ObjectKind is ObjectKind.EventObj or ObjectKind.Aetheryte
                    && Vector3.Distance(obj.Position, playerPosition) <= 15f
                )
                .OrderBy(obj => Vector3.Distance(obj.Position, playerPosition))
                .FirstOrDefault();
        }

        var detectedName = candidate?.Name.ToString();
        var name = string.IsNullOrWhiteSpace(requestedName)
            ? detectedName
            : requestedName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Svc.Chat.PrintError(
                "[BOCCHI] 请填写 Lifestream 传送列表中显示的魔路名称。"
            );
            return false;
        }

        var activeCustomId = 0u;
        try
        {
            var lifestream = GetIPCSubscriber<Lifestream>();
            if (lifestream.IsReady())
            {
                activeCustomId = lifestream.GetActiveCustomAetheryte();
            }
        }
        catch
        {
            // Optional diagnostic metadata; recording must still succeed.
        }

        var route = Plugin.NorthernRoutes.RecordRoute(
            Svc.ClientState.TerritoryType,
            Svc.ClientState.MapId,
            name,
            destinationId,
            activeCustomId,
            candidate?.BaseId ?? 0,
            candidate?.Position ?? playerPosition
        );
        Svc.Chat.Print(
            $"[BOCCHI] 已记录已共鸣魔路“{route.Name}”。"
            + "请传送到该魔路后，再记录到达坐标。"
        );
        return true;
    }

    public bool RecordNorthernRouteArrival(Guid routeId)
    {
        if (!ZoneData.IsInNorthernExpedition() || Svc.Objects.LocalPlayer == null)
        {
            return false;
        }

        var route = GetNorthernRoutes().FirstOrDefault(entry => entry.Id == routeId);
        if (route == null
            || !Plugin.NorthernRoutes.RecordArrival(routeId, Player.Position))
        {
            return false;
        }

        Svc.Chat.Print($"[BOCCHI] 已记录“{route.Name}”的传送到达坐标。");
        return true;
    }

    public void SetCurrentNorthernStandbyPoint(string name)
    {
        if (!ZoneData.IsInNorthernExpedition() || Svc.Objects.LocalPlayer == null)
        {
            Svc.Chat.PrintError("[BOCCHI] 请在北征之章内设置蹲守点。");
            return;
        }

        Plugin.NorthernRoutes.SetStandbyPoint(
            Svc.ClientState.TerritoryType,
            Svc.ClientState.MapId,
            name,
            Player.Position
        );
        Svc.Chat.Print("[BOCCHI] 已将当前位置设置为 Illegal Mode 事件结束蹲守点。");
    }

    public void SetNorthernRouteEnabled(Guid routeId, bool enabled)
    {
        Plugin.NorthernRoutes.SetRouteEnabled(routeId, enabled);
    }

    public void DeleteNorthernRoute(Guid routeId)
    {
        Plugin.NorthernRoutes.DeleteRoute(routeId);
    }

    private unsafe void RecordLiveEventNames()
    {
        if (!WorldObjectScanGuard.IsSafe()
            || !ZoneData.IsInPluginTerritory())
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
        if (!WorldObjectScanGuard.IsSafe()
            || !ZoneData.IsInNorthernExpedition()
            || !Config.Enabled)
        {
            return;
        }

        var fates = GetModule<FatesModule>();
        var criticalEncounters = GetModule<CriticalEncountersModule>();
        var states = GetModule<StateManagerModule>();

        try
        {
            // TargetModule is not ticked by the South-only plugin update loop in
            // North, so refresh the shared hostile/targetable enemy snapshot here.
            TargetHelper.Update();
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
