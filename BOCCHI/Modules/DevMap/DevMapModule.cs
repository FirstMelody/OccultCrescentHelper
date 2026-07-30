using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using BOCCHI.Data;
using BOCCHI.Data.Traps;
using BOCCHI.Enums;
using BOCCHI.Modules.ForkedTower;
using BOCCHI.Modules.Telemetry;
using BOCCHI.Modules.Treasure;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Textures;
using Dalamud.Bindings.ImGui;
using ECommons.DalamudServices;
using ECommons.GameFunctions;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Ocelot.Modules;
using Ocelot.Windows;
using Bounds = FFXIVClientStructs.FFXIV.Common.Math.Bounds;

namespace BOCCHI.Modules.DevMap;

[OcelotModule(900)]
public class DevMapModule : Module
{
    private const string MarkerFileName = "northern_expedition_markers.json";
    private const string ForkedTowerEventObjFileName = "forked_tower_eventobjs.json";
    private const string EditWindowId = "编辑地图标注###BOCCHI_DevMap_Edit";
    private const float ChestMergeDistance = 4f;
    private const float EventMergeDistance = 8f;
    private const float MonsterMergeDistance = 200f;
    private const float MonsterDisplayClusterRadius = 230f;
    private const float MonsterCollisionOnlyZoom = 1.5f;
    private const float MonsterLabelCollisionGap = 3f;
    private const int MonsterLabelsPerCluster = 4;
    private const int MonsterLabelsPerColumn = 4;
    private const float NearbyMonsterDistance = 100f;
    private const float MonsterMovementTolerance = 0.15f;
    private const int ExpectedBuiltInTrapGroupCount = 47;
    private static readonly TimeSpan AutoScanInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MonsterStationaryDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MonsterTrackExpiry = TimeSpan.FromSeconds(5);
    private static readonly DevMarkerType[] EditableTypes =
    [
        DevMarkerType.SilverChest,
        DevMarkerType.BronzeChest,
        DevMarkerType.FortuneCarrot,
        DevMarkerType.PotChest,
        DevMarkerType.RerollChest,
        DevMarkerType.Fate,
        DevMarkerType.CriticalEncounter,
        DevMarkerType.InvestigationLocation,
    ];
    private static readonly DevMarkerType[] MarkerFilterTypes =
    [
        DevMarkerType.BronzeChest,
        DevMarkerType.SilverChest,
        DevMarkerType.PotChest,
        DevMarkerType.RerollChest,
        DevMarkerType.FortuneCarrot,
        DevMarkerType.InvestigationLocation,
        DevMarkerType.Fate,
        DevMarkerType.CriticalEncounter,
        DevMarkerType.UnknownChest,
        DevMarkerType.Monster,
    ];

    private readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Dictionary<DevMarkerType, uint> iconIds = new()
    {
        // Same verified icons used by EurekaTrackerAutoPopper.
        [DevMarkerType.SilverChest] = 60355,
        [DevMarkerType.BronzeChest] = 60356,
        [DevMarkerType.FortuneCarrot] = 25207,
        [DevMarkerType.FortuneCarrotChest] = 60354,
        [DevMarkerType.PotChest] = 60354,
        [DevMarkerType.RerollChest] = 61473,
        [DevMarkerType.Fate] = 60502,
        [DevMarkerType.CriticalEncounter] = 63909,
        [DevMarkerType.InvestigationLocation] = 60474,
        [DevMarkerType.UnknownChest] = 60354,
    };
    private DevMapMarkerFile markerFile = new();
    private readonly IReadOnlyList<DevMapMarker> linkerMarkers;
    private ForkedTowerEventObjFile forkedTowerEventObjFile = new();
    private DevMapMarker? pendingEdit;
    private ForkedTowerEventObjRecord? pendingTowerTrapEdit;
    private bool editorOpen;
    private bool towerTrapEditorOpen;
    private bool deleteConfirmationRequested;
    private readonly Dictionary<uint, MonsterMovementTrack> monsterMovementTracks = [];
    private List<MonsterMapCluster> cachedMonsterClusters = [];
    private int cachedMonsterClusterFingerprint;
    private uint cachedMonsterClusterTerritoryId;
    private uint cachedMonsterClusterMapId;
    private bool warnedUnexpectedBuiltInTrapGroupCount;
    private DateTime nextAutoScanAt = DateTime.MinValue;
    private DateTime nextReadOnlyScanAt = DateTime.MinValue;
    private uint trackedMonsterTerritoryId;
    private uint trackedMonsterMapId;
    private string? lastError;
    private nint areaMapAddonAddress;

    private string MarkerPath
    {
        get => Path.Join(Svc.PluginInterface.ConfigDirectory.FullName, MarkerFileName);
    }

    private string ForkedTowerEventObjPath
    {
        get => Path.Join(
            Svc.PluginInterface.ConfigDirectory.FullName,
            ForkedTowerEventObjFileName
        );
    }

    public override bool ShouldInitialize
    {
        get => true;
    }

    public override bool IsEnabled
    {
        get => true;
    }

    public override bool ShouldUpdate
    {
        get => true;
    }

    public DevMapModule(Plugin plugin, Config config)
        : base(plugin, config)
    {
        linkerMarkers = NorthernLinkerMarkerCatalog.Load();
        LoadMarkers();
        LoadForkedTowerEventObjects();
        Svc.AddonLifecycle.RegisterListener(
            AddonEvent.PostDraw,
            "AreaMap",
            OnAreaMapAvailable
        );
        Svc.AddonLifecycle.RegisterListener(
            AddonEvent.PreFinalize,
            "AreaMap",
            OnAreaMapFinalizing
        );
        Svc.PluginInterface.UiBuilder.Draw += DrawDevMapUi;
    }

    public IReadOnlyList<DevMapMarker> GetTelemetryMarkersSnapshot()
    {
        return markerFile.Markers
            .Where(marker => !IsSupersededByLinkerCatalog(marker))
            .Select(CloneMarker)
            .ToList();
    }

    public IReadOnlyList<ForkedTowerEventObjRecord> GetTelemetryTowerObjectsSnapshot()
    {
        return forkedTowerEventObjFile.EventObjects
            .Select(record => new ForkedTowerEventObjRecord
            {
                Id = record.Id,
                TerritoryId = record.TerritoryId,
                MapId = record.MapId,
                BaseId = record.BaseId,
                Name = record.Name,
                X = record.X,
                Y = record.Y,
                Z = record.Z,
                HitboxRadius = record.HitboxRadius,
                MechanicRadius = record.MechanicRadius,
                Type = record.Type,
                WasTargetable = record.WasTargetable,
                TowerRunId = record.TowerRunId,
                ObservedRunIds = [.. record.ObservedRunIds],
                FirstSeenAt = record.FirstSeenAt,
            })
            .ToList();
    }

    public override bool RenderMainUi(RenderContext context)
    {
        if (!PluginConfig.DevModeEnabled)
        {
            return false;
        }

        RenderDevUi();
        return true;
    }

    public void RenderDevUi()
    {
        var debugLogging = PluginConfig.DebugLoggingEnabled;
        if (ImGui.Checkbox(
                "调试日志（默认关闭）##BOCCHI_DebugLogging",
                ref debugLogging
            ))
        {
            PluginConfig.DebugLoggingEnabled = debugLogging;
            PluginConfig.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("dev 地图标注");
        ImGui.Separator();

        if (!PluginConfig.DevModeEnabled)
        {
            ImGui.TextWrapped("dev 模式未开启。点击窗口标题栏的代码图标，或使用 /bocchi dev on。");
            return;
        }

        var territoryId = Svc.ClientState.TerritoryType;
        var mapId = Svc.ClientState.MapId;
        ImGui.TextUnformatted($"当前 Territory: {territoryId} / Map: {mapId}");

        ImGui.Separator();
        ImGui.TextUnformatted("Forked Tower: Blood dev 采集");
        if (ImGui.Button("将当前 Territory 设为 Forked Tower: Blood##DevMap_BindTower"))
        {
            BindCurrentTerritoryAsForkedTowerBlood();
        }

        if (PluginConfig.ForceForkedTowerBloodTerritory)
        {
            ImGui.SameLine();
            if (ImGui.Button("停止强制 Tower 判定##DevMap_StopForceTower"))
            {
                PluginConfig.ForceForkedTowerBloodTerritory = false;
                PluginConfig.Save();
                Svc.Chat.Print("[BOCCHI] 已停止强制 Tower 判定，恢复塔内状态检测。");
            }
        }

        if (PluginConfig.ForkedTowerBloodTerritoryId > 0)
        {
            ImGui.TextDisabled(
                $"Tower 绑定：Territory={PluginConfig.ForkedTowerBloodTerritoryId}, "
                + $"Map={PluginConfig.ForkedTowerBloodMapId}, "
                + $"强制={PluginConfig.ForceForkedTowerBloodTerritory}"
            );
        }

        if (ZoneData.IsInForkedTower()
            && ImGui.Button("开始新的 Tower 塔次##DevMap_NewTowerRun"))
        {
            GetModule<ForkedTowerModule>().StartNewRun();
            Svc.Chat.Print("[BOCCHI] 已清空本塔次雷发现/排除状态并开始新塔次。");
        }

        var towerObjectCount = forkedTowerEventObjFile.EventObjects.Count(record =>
            record.TerritoryId == territoryId && record.MapId == mapId
        );
        ImGui.TextDisabled(
            $"本 Territory/Map 已记录 {towerObjectCount} 个 Tower EventObj；"
            + "记录 BaseId、XYZ、HitboxRadius 和已知机制半径。"
        );
        ImGui.TextDisabled($"Tower JSON: {ForkedTowerEventObjPath}");

        var showTowerObjects = PluginConfig.ShowForkedTowerEventObjectsOnMap;
        if (ImGui.Checkbox("在大地图显示 Tower EventObj##DevMap_ShowTowerObjects", ref showTowerObjects))
        {
            PluginConfig.ShowForkedTowerEventObjectsOnMap = showTowerObjects;
            PluginConfig.Save();
        }

        var showUnknownTowerObjects = PluginConfig.ShowUnknownForkedTowerEventObjectsOnMap;
        if (ImGui.Checkbox(
                "包含未知/非雷 EventObj##DevMap_ShowUnknownTowerObjects",
                ref showUnknownTowerObjects
            ))
        {
            PluginConfig.ShowUnknownForkedTowerEventObjectsOnMap = showUnknownTowerObjects;
            PluginConfig.Save();
        }

        var showTowerLabels = PluginConfig.ShowForkedTowerEventObjLabels;
        if (ImGui.Checkbox("显示 BaseId 后四位##DevMap_ShowTowerLabels", ref showTowerLabels))
        {
            PluginConfig.ShowForkedTowerEventObjLabels = showTowerLabels;
            PluginConfig.Save();
        }

        var showPotentialTraps = PluginConfig.ShowForkedTowerPotentialTrapPositionsOnMap;
        if (ImGui.Checkbox(
                "显示雷候选点位和机制范围##DevMap_ShowPotentialTraps",
                ref showPotentialTraps
            ))
        {
            PluginConfig.ShowForkedTowerPotentialTrapPositionsOnMap = showPotentialTraps;
            PluginConfig.Save();
        }

        var forkedTowerModule = GetModule<ForkedTowerModule>();
        var showPotentialTrapsInWorld =
            forkedTowerModule.Config.DrawPotentialTrapPositions;
        if (ImGui.Checkbox(
                "在游戏世界显示候选雷组##DevMap_ShowPotentialTrapsInWorld",
                ref showPotentialTrapsInWorld
            ))
        {
            forkedTowerModule.Config.DrawPotentialTrapPositions =
                showPotentialTrapsInWorld;
            PluginConfig.Save();
        }

        var showTrapGroups = PluginConfig.ShowForkedTowerTrapGroupLabelsOnMap;
        if (ImGui.Checkbox("显示雷互斥编组##DevMap_ShowTrapGroups", ref showTrapGroups))
        {
            PluginConfig.ShowForkedTowerTrapGroupLabelsOnMap = showTrapGroups;
            PluginConfig.Save();
        }

        var customGroupCount = forkedTowerEventObjFile.TrapGroups.Count(group =>
            group.TerritoryId == territoryId && group.MapId == mapId
        );
        ImGui.TextDisabled(
            $"本 Territory/Map 有 {customGroupCount} 个自定义雷组；"
            + "候选点与互斥组现在只读，由插件自动采集和判定。"
        );
        ImGui.TextDisabled(
            "大地图图例：实心=本次已发现，空心=仍可能出现，×=同组已满足而排除；"
            + "红色小雷范围 7，橙色大雷范围 30。"
        );
        if (PluginConfig.ForceForkedTowerBloodTerritory)
        {
            ImGui.TextColored(
                new Vector4(1f, 0.72f, 0.24f, 1f),
                "当前为强制采集模式：此 Territory 内观察到的全部 EventObj 都会记录，离塔后请停止强制判定。"
            );
        }

        if (ZoneData.IsInSouthHorn())
        {
            ImGui.TextColored(new Vector4(1f, 0.78f, 0.25f, 1f), "当前区域：南征之章 dev 功能测试");
        }
        else if (ZoneData.IsInNorthernExpedition())
        {
            ImGui.TextColored(new Vector4(0.45f, 0.9f, 0.55f, 1f), "当前区域：蜃景幻界：新月岛 北征之章");
        }
        else
        {
            ImGui.TextWrapped("提示：在北征之章内执行 /bocchi dev bind，可将当前区域永久绑定并在以后进入时自动打开插件。");
        }

        ImGui.TextWrapped(
            "附近出现的宝箱、好运胡萝卜、调查地点、静止未交战怪物、FATE 和 CE "
            + "都会自动记录。地图点位为只读，不再提供手动新增、改类型或删除。"
        );
        var count = markerFile.Markers.Count(m => m.TerritoryId == territoryId);
        ImGui.TextDisabled($"本区域已自动保存 {count} 个只读标注。");
        ImGui.TextDisabled("打开大地图后，可用地图上方的 Linker 风格图标按钮分别开关各类标记。");
        ImGui.TextDisabled($"JSON: {MarkerPath}");

        if (lastError != null)
        {
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), lastError);
        }
    }

    public override void Update(UpdateContext context)
    {
        AutoScan();
    }

    public void BindCurrentTerritoryAsForkedTowerBlood()
    {
        var territoryId = Svc.ClientState.TerritoryType;
        var mapId = Svc.ClientState.MapId;
        if (Svc.Objects.LocalPlayer == null || territoryId == 0 || mapId == 0)
        {
            lastError = "无法取得当前玩家、Territory 或 Map。";
            return;
        }

        PluginConfig.ForkedTowerBloodTerritoryId = territoryId;
        PluginConfig.ForkedTowerBloodMapId = mapId;
        PluginConfig.ForceForkedTowerBloodTerritory = true;
        PluginConfig.DevModeEnabled = true;
        PluginConfig.Save();
        GetModule<ForkedTowerModule>().StartNewRun();
        lastError = null;

        Svc.Chat.Print(
            $"[BOCCHI] 已将当前区域强制设置为 Forked Tower: Blood："
            + $"Territory={territoryId}, Map={mapId}。"
        );
    }

    private unsafe void AutoScan()
    {
        if (!WorldObjectScanGuard.IsSafe()
            || !PluginConfig.DevModeEnabled
            || !ZoneData.IsInPluginTerritory()
            || DateTime.UtcNow < nextAutoScanAt)
        {
            return;
        }

        nextAutoScanAt = DateTime.UtcNow + AutoScanInterval;

        var territoryId = Svc.ClientState.TerritoryType;
        var mapId = Svc.ClientState.MapId;
        if (territoryId == 0 || mapId == 0)
        {
            return;
        }

        if (ZoneData.IsInForkedTower())
        {
            AutoScanForkedTowerEventObjects(territoryId, mapId);
            return;
        }

        var originalMarkers = markerFile.Markers.Select(CloneMarker).ToList();
        var recorded = new List<string>();

        // Carrots must be processed before treasure coffers. Once a carrot has been
        // seen at a position, its spawned coffer is intentionally kept as one carrot marker.
        foreach (var carrot in Svc.Objects.Where(obj =>
                     obj.ObjectKind == ObjectKind.EventObj
                     && obj.BaseId is (uint)OccultObjectType.Carrot
                         or (uint)OccultObjectType.BunnyChest
                     && obj is { IsDead: false }
                     && obj.IsValid()
                 ))
        {
            if (RecordDetectedMarker(
                    DevMarkerType.FortuneCarrot,
                    carrot.Position,
                    territoryId,
                    mapId
                ))
            {
                recorded.Add(GetLabel(DevMarkerType.FortuneCarrot));
            }
        }

        var investigationObjects = Svc.Objects.OfType<IEventObj>()
            .Where(obj =>
                obj.IsValid()
                && IsValidPosition(obj.Position)
                && obj.Name.TextValue.Contains("调查", StringComparison.Ordinal)
            )
            .ToList();
        foreach (var investigation in investigationObjects)
        {
            if (RecordDetectedMarker(
                    DevMarkerType.InvestigationLocation,
                    investigation.Position,
                    territoryId,
                    mapId,
                    name: investigation.Name.TextValue,
                    baseId: investigation.BaseId
                ))
            {
                recorded.Add(GetLabel(DevMarkerType.InvestigationLocation));
            }
        }

        foreach (var obj in Svc.Objects.Where(obj =>
                     obj.ObjectKind == ObjectKind.Treasure
                     && obj is { IsDead: false, IsTargetable: true }
                     && obj.IsValid()
                 ))
        {
            var treasure = new Treasure.Treasure(obj);
            var markerType = treasure.GetTreasureType() switch
            {
                TreasureType.Silver => DevMarkerType.SilverChest,
                TreasureType.Bronze => DevMarkerType.BronzeChest,
                TreasureType.Gold => DevMarkerType.PotChest,
                _ => DevMarkerType.UnknownChest,
            };

            if (RecordDetectedMarker(
                    markerType,
                    treasure.GetPosition(),
                    territoryId,
                    mapId,
                    name: treasure.GetObjectName(),
                    baseId: treasure.GetBaseId()
                ))
            {
                recorded.Add(GetLabel(markerType));
            }
        }

        // Use Dalamud's live FATE table directly. Unlike the normal FATE panel, this
        // also works for future North-chapter FATE IDs that are not in EventData yet.
        try
        {
            foreach (var fate in Svc.Fates.ToArray())
            {
                if (RecordDetectedMarker(
                        DevMarkerType.Fate,
                        fate.Position,
                        territoryId,
                        mapId,
                        fate.FateId,
                        fate.Name.ToString()
                    ))
                {
                    recorded.Add(GetLabel(DevMarkerType.Fate));
                }
            }
        }
        catch (AccessViolationException)
        {
            // A FATE can despawn while Dalamud is snapshotting it. Retry next scan.
        }

        var occultCrescent = PublicContentOccultCrescent.GetInstance();
        if (occultCrescent != null)
        {
            foreach (var encounter in occultCrescent->DynamicEventContainer.Events.ToArray())
            {
                // Match Bocchi's existing CE command: event types >= 4 are special
                // content (for example the Forked Tower), not normal CEs.
                if (encounter.State == DynamicEventState.Inactive || encounter.EventType >= 4)
                {
                    continue;
                }

                if (RecordDetectedMarker(
                        DevMarkerType.CriticalEncounter,
                        encounter.MapMarker.Position,
                        territoryId,
                        mapId,
                        encounter.DynamicEventId,
                        encounter.Name.ToString()
                    ))
                {
                    recorded.Add(GetLabel(DevMarkerType.CriticalEncounter));
                }
            }
        }

        if (recorded.Count == 0)
        {
            return;
        }

        if (!SaveMarkers())
        {
            markerFile.Markers = originalMarkers;
            return;
        }

        lastError = null;
        if (!DebugLog.Enabled)
        {
            return;
        }

        var summary = string.Join("、", recorded
            .GroupBy(label => label)
            .Select(group => group.Count() == 1 ? group.Key : $"{group.Key}×{group.Count()}"));
        Svc.Chat.Print($"[BOCCHI] dev 自动记录：{summary}");
    }

    private unsafe void AutoScanReadOnlyMapContent()
    {
        if (!WorldObjectScanGuard.IsSafe())
        {
            monsterMovementTracks.Clear();
            return;
        }

        var now = DateTime.UtcNow;
        if (now < nextReadOnlyScanAt)
        {
            return;
        }

        nextReadOnlyScanAt = now + AutoScanInterval;
        var territoryId = Svc.ClientState.TerritoryType;
        var mapId = Svc.ClientState.MapId;
        var isOutdoorOccultMap = (territoryId, mapId) is
            (ZoneData.SOUTHHORN, 967) or (ZoneData.NORTHHORN, 1135);
        if (!isOutdoorOccultMap
            || ZoneData.IsInForkedTower()
            || Svc.Objects.LocalPlayer is not { } player)
        {
            monsterMovementTracks.Clear();
            trackedMonsterTerritoryId = territoryId;
            trackedMonsterMapId = mapId;
            return;
        }

        if (trackedMonsterTerritoryId != territoryId || trackedMonsterMapId != mapId)
        {
            monsterMovementTracks.Clear();
            trackedMonsterTerritoryId = territoryId;
            trackedMonsterMapId = mapId;
        }

        var originalMarkers = markerFile.Markers.Select(CloneMarker).ToList();
        var changed = false;
        foreach (var investigation in Svc.Objects.OfType<IEventObj>().Where(obj =>
                     obj.IsValid()
                     && IsValidPosition(obj.Position)
                     && obj.Name.TextValue.Contains("调查", StringComparison.Ordinal)
                 ))
        {
            changed |= RecordInvestigationMarker(
                investigation.Position,
                territoryId,
                mapId,
                investigation.BaseId,
                investigation.Name.TextValue
            );
        }

        var seenEntityIds = new HashSet<uint>();
        foreach (var monster in Svc.Objects.OfType<IBattleNpc>())
        {
            if (monster.EntityId == 0
                || monster.SubKind != (byte)BattleNpcSubKind.Combatant
                || monster is not { IsDead: false, IsTargetable: true }
                || !monster.IsValid()
                || !monster.IsHostile()
                || monster.HasTarget()
                || Vector3.Distance(player.Position, monster.Position) > NearbyMonsterDistance
                || !IsValidPosition(monster.Position))
            {
                continue;
            }

            var battleChara = (BattleChara*)monster.Address;
            var level = (uint)battleChara->ForayInfo.Level;
            var name = monster.Name.TextValue.Trim();
            if (level is 0 or 1 || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            seenEntityIds.Add(monster.EntityId);
            if (!monsterMovementTracks.TryGetValue(monster.EntityId, out var track))
            {
                monsterMovementTracks[monster.EntityId] = new MonsterMovementTrack
                {
                    Position = monster.Position,
                    StableSince = now,
                    LastSeenAt = now,
                };
                continue;
            }

            track.LastSeenAt = now;
            if (Vector3.Distance(track.Position, monster.Position) > MonsterMovementTolerance)
            {
                track.Position = monster.Position;
                track.StableSince = now;
                continue;
            }

            if (track.RecordedAtStablePosition
                || now - track.StableSince < MonsterStationaryDuration)
            {
                continue;
            }

            changed |= RecordMonsterMarker(
                monster.Position,
                territoryId,
                mapId,
                monster.NameId,
                level,
                name
            );
            track.RecordedAtStablePosition = true;
        }

        foreach (var entityId in monsterMovementTracks
                     .Where(entry =>
                         !seenEntityIds.Contains(entry.Key)
                         && now - entry.Value.LastSeenAt >= MonsterTrackExpiry)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            monsterMovementTracks.Remove(entityId);
        }

        if (!changed)
        {
            return;
        }

        if (!SaveMarkers())
        {
            markerFile.Markers = originalMarkers;
            return;
        }

        lastError = null;
    }

    private bool RecordInvestigationMarker(
        Vector3 position,
        uint territoryId,
        uint mapId,
        uint baseId,
        string name
    )
    {
        var existing = markerFile.Markers.FirstOrDefault(marker =>
            marker.Type == DevMarkerType.InvestigationLocation
            && marker.TerritoryId == territoryId
            && marker.MapId == mapId
            && (baseId == 0 || marker.BaseId == 0 || marker.BaseId == baseId)
            && HorizontalDistance(marker.Position, position) <= EventMergeDistance
        );
        if (existing != null)
        {
            var changed = false;
            if (existing.BaseId == 0 && baseId != 0)
            {
                existing.BaseId = baseId;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(existing.Name) && !string.IsNullOrWhiteSpace(name))
            {
                existing.Name = name;
                changed = true;
            }

            return changed;
        }

        markerFile.Markers.Add(new DevMapMarker
        {
            Type = DevMarkerType.InvestigationLocation,
            BaseId = baseId,
            Name = name,
            TerritoryId = territoryId,
            MapId = mapId,
            X = position.X,
            Y = position.Y,
            Z = position.Z,
        });
        return true;
    }

    private bool RecordMonsterMarker(
        Vector3 position,
        uint territoryId,
        uint mapId,
        uint baseId,
        uint level,
        string name
    )
    {
        if (level == 1)
        {
            return false;
        }

        var existing = markerFile.Markers.FirstOrDefault(marker =>
            marker.Type == DevMarkerType.Monster
            && marker.TerritoryId == territoryId
            && marker.MapId == mapId
            && marker.BaseId == baseId
            && HorizontalDistance(marker.Position, position) <= MonsterMergeDistance
        );
        if (existing != null)
        {
            var observationCount = Math.Max(1, existing.ObservationCount);
            var combinedCount = observationCount + 1;
            existing.X = (existing.X * observationCount + position.X) / combinedCount;
            existing.Y = (existing.Y * observationCount + position.Y) / combinedCount;
            existing.Z = (existing.Z * observationCount + position.Z) / combinedCount;
            existing.ObservationCount = combinedCount;
            existing.Level = level;
            existing.Name = name;
            return true;
        }

        markerFile.Markers.Add(new DevMapMarker
        {
            Type = DevMarkerType.Monster,
            BaseId = baseId,
            Level = level,
            ObservationCount = 1,
            Name = name,
            TerritoryId = territoryId,
            MapId = mapId,
            X = position.X,
            Y = position.Y,
            Z = position.Z,
        });
        return true;
    }

    private void AutoScanForkedTowerEventObjects(uint territoryId, uint mapId)
    {
        var originalRecords = forkedTowerEventObjFile.EventObjects
            .Select(CloneForkedTowerEventObject)
            .ToList();
        var towerRunId = GetModule<ForkedTowerModule>().TowerRun.Hash;
        var recordedBaseIds = new List<uint>();
        var changed = false;

        foreach (var eventObj in Svc.Objects.OfType<IEventObj>())
        {
            if (eventObj.BaseId == 0
                || !eventObj.IsValid()
                || !IsValidPosition(eventObj.Position))
            {
                continue;
            }

            var position = eventObj.Position;
            var existing = forkedTowerEventObjFile.EventObjects.FirstOrDefault(record =>
                record.TerritoryId == territoryId
                && record.MapId == mapId
                && record.BaseId == eventObj.BaseId
                && Vector3.Distance(record.Position, position) <= 0.25f
            );
            var (type, mechanicRadius) = GetKnownForkedTowerObjectType(
                eventObj.BaseId,
                territoryId
            );
            var name = eventObj.Name.TextValue;
            var hitboxRadius = Math.Max(0f, eventObj.HitboxRadius);

            if (existing != null)
            {
                if (existing.Name.Length == 0 && name.Length > 0)
                {
                    existing.Name = name;
                    changed = true;
                }

                if (Math.Abs(existing.HitboxRadius - hitboxRadius) > 0.01f)
                {
                    existing.HitboxRadius = hitboxRadius;
                    changed = true;
                }

                if (type != ForkedTowerEventObjType.Unknown
                    && (existing.Type != type
                        || existing.MechanicRadius != mechanicRadius))
                {
                    existing.Type = type;
                    existing.MechanicRadius = mechanicRadius;
                    changed = true;
                }

                if (existing.TowerRunId.Length == 0 && towerRunId.Length > 0)
                {
                    existing.TowerRunId = towerRunId;
                    changed = true;
                }

                existing.ObservedRunIds ??= [];
                if (towerRunId.Length > 0 && !existing.ObservedRunIds.Contains(towerRunId))
                {
                    existing.ObservedRunIds.Add(towerRunId);
                    changed = true;
                }

                continue;
            }

            forkedTowerEventObjFile.EventObjects.Add(new ForkedTowerEventObjRecord
            {
                TerritoryId = territoryId,
                MapId = mapId,
                BaseId = eventObj.BaseId,
                Name = name,
                X = position.X,
                Y = position.Y,
                Z = position.Z,
                HitboxRadius = hitboxRadius,
                MechanicRadius = mechanicRadius,
                Type = type,
                WasTargetable = eventObj.IsTargetable,
                TowerRunId = towerRunId,
                ObservedRunIds = towerRunId.Length > 0 ? [towerRunId] : [],
            });
            recordedBaseIds.Add(eventObj.BaseId);
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        if (!SaveForkedTowerEventObjects())
        {
            forkedTowerEventObjFile.EventObjects = originalRecords;
            return;
        }

        lastError = null;
        if (DebugLog.Enabled && recordedBaseIds.Count > 0)
        {
            var baseIdSummary = string.Join(
                ", ",
                recordedBaseIds
                    .Distinct()
                    .Order()
                    .Select(baseId => baseId.ToString())
            );
            Svc.Chat.Print(
                $"[BOCCHI] Tower dev 自动记录 {recordedBaseIds.Count} 个 EventObj；"
                + $"BaseId: {baseIdSummary}"
            );
        }
    }

    private (
        ForkedTowerEventObjType Type,
        float? MechanicRadius
    ) GetKnownForkedTowerObjectType(uint baseId, uint territoryId)
    {
        var known = GetBuiltInForkedTowerObjectType(baseId);
        if (known.Type != ForkedTowerEventObjType.Unknown)
        {
            return known;
        }

        var learned = forkedTowerEventObjFile.EventObjects.FirstOrDefault(record =>
            record.TerritoryId == territoryId
            && record.BaseId == baseId
            && record.Type != ForkedTowerEventObjType.Unknown
        );
        return learned != null
            ? (learned.Type, learned.MechanicRadius)
            : known;
    }

    private static (
        ForkedTowerEventObjType Type,
        float? MechanicRadius
    ) GetBuiltInForkedTowerObjectType(uint baseId)
    {
        return baseId switch
        {
            (uint)OccultObjectType.Trap =>
                (ForkedTowerEventObjType.SmallTrap, 7f),
            (uint)OccultObjectType.BigTrap =>
                (ForkedTowerEventObjType.BigTrap, 30f),
            _ => (ForkedTowerEventObjType.Unknown, null),
        };
    }

    private static ForkedTowerEventObjRecord CloneForkedTowerEventObject(
        ForkedTowerEventObjRecord record
    )
    {
        return new ForkedTowerEventObjRecord
        {
            Id = record.Id,
            TerritoryId = record.TerritoryId,
            MapId = record.MapId,
            BaseId = record.BaseId,
            Name = record.Name,
            X = record.X,
            Y = record.Y,
            Z = record.Z,
            HitboxRadius = record.HitboxRadius,
            MechanicRadius = record.MechanicRadius,
            Type = record.Type,
            WasTargetable = record.WasTargetable,
            TowerRunId = record.TowerRunId,
            ObservedRunIds = [..record.ObservedRunIds],
            FirstSeenAt = record.FirstSeenAt,
        };
    }

    private bool RecordDetectedMarker(
        DevMarkerType type,
        Vector3 position,
        uint territoryId,
        uint mapId,
        uint eventId = 0,
        string? name = null,
        uint baseId = 0
    )
    {
        if (!IsValidPosition(position))
        {
            return false;
        }

        if (linkerMarkers.Count > 0
            && territoryId == NorthernLinkerMarkerCatalog.TerritoryId
            && (NorthernLinkerMarkerCatalog.IsManagedType(type)
                || (type == DevMarkerType.UnknownChest
                    && linkerMarkers.Any(marker =>
                        marker.MapId == mapId
                        && HorizontalDistance(marker.Position, position) <= ChestMergeDistance
                    ))))
        {
            return false;
        }

        var sameMap = markerFile.Markers
            .Where(marker => marker.TerritoryId == territoryId && marker.MapId == mapId)
            .ToList();

        if (type == DevMarkerType.FortuneCarrot)
        {
            var nearbyCarrot = sameMap.FirstOrDefault(marker =>
                marker.Type == DevMarkerType.FortuneCarrot
                && HorizontalDistance(marker.Position, position) <= ChestMergeDistance
            );
            var nearbyChests = sameMap.Where(marker =>
                    IsChestType(marker.Type)
                    && HorizontalDistance(marker.Position, position) <= ChestMergeDistance
                )
                .ToList();

            if (nearbyCarrot != null)
            {
                if (nearbyChests.Count == 0)
                {
                    return false;
                }

                markerFile.Markers.RemoveAll(marker => nearbyChests.Any(chest => chest.Id == marker.Id));
                return true;
            }

            if (nearbyChests.Count > 0)
            {
                var marker = nearbyChests[0];
                marker.Type = DevMarkerType.FortuneCarrot;
                marker.X = position.X;
                marker.Y = position.Y;
                marker.Z = position.Z;
                markerFile.Markers.RemoveAll(candidate =>
                    candidate.Id != marker.Id && nearbyChests.Any(chest => chest.Id == candidate.Id)
                );
                return true;
            }
        }
        else if (IsChestType(type))
        {
            if (sameMap.Any(marker =>
                    marker.Type == DevMarkerType.FortuneCarrot
                    && HorizontalDistance(marker.Position, position) <= ChestMergeDistance
                ))
            {
                return false;
            }

            var sameObjectChest = sameMap.FirstOrDefault(marker =>
                IsChestType(marker.Type)
                && HorizontalDistance(marker.Position, position) <= 0.75f
                && (baseId == 0 || marker.BaseId == 0 || marker.BaseId == baseId)
            );
            if (sameObjectChest != null)
            {
                var changed = false;
                if (sameObjectChest.Type == DevMarkerType.UnknownChest
                    && type != DevMarkerType.UnknownChest)
                {
                    sameObjectChest.Type = type;
                    changed = true;
                }

                if (baseId != 0 && sameObjectChest.BaseId != baseId)
                {
                    sameObjectChest.BaseId = baseId;
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(name) && sameObjectChest.Name != name)
                {
                    sameObjectChest.Name = name;
                    changed = true;
                }

                return changed;
            }

            var nearbyChest = sameMap.FirstOrDefault(marker =>
                AreMergeableMarkerTypes(marker.Type, type)
                && HorizontalDistance(marker.Position, position) <= ChestMergeDistance
            );
            if (nearbyChest != null)
            {
                if (nearbyChest.Type == DevMarkerType.UnknownChest
                    && type != DevMarkerType.UnknownChest)
                {
                    nearbyChest.Type = type;
                    nearbyChest.X = position.X;
                    nearbyChest.Y = position.Y;
                    nearbyChest.Z = position.Z;
                    if (baseId != 0)
                    {
                        nearbyChest.BaseId = baseId;
                    }

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        nearbyChest.Name = name;
                    }

                    return true;
                }

                var changed = false;
                if (baseId != 0 && nearbyChest.BaseId != baseId)
                {
                    nearbyChest.BaseId = baseId;
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(name) && nearbyChest.Name != name)
                {
                    nearbyChest.Name = name;
                    changed = true;
                }

                return changed;
            }
        }
        else if (type is DevMarkerType.Fate or DevMarkerType.CriticalEncounter)
        {
            var existingEvent = sameMap.FirstOrDefault(marker =>
                marker.Type == type
                && ((eventId != 0 && marker.EventId == eventId)
                    || (marker.EventId == 0
                        && HorizontalDistance(marker.Position, position) <= EventMergeDistance))
            );
            if (existingEvent != null)
            {
                var changed = false;
                if (existingEvent.EventId == 0 && eventId != 0)
                {
                    existingEvent.EventId = eventId;
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(name)
                    && existingEvent.Name != name)
                {
                    existingEvent.Name = name;
                    changed = true;
                }

                return changed;
            }
        }
        else if (sameMap.Any(marker =>
                     marker.Type == type
                     && HorizontalDistance(marker.Position, position) <= EventMergeDistance
                 ))
        {
            return false;
        }

        markerFile.Markers.Add(new DevMapMarker
        {
            Type = type,
            EventId = eventId,
            BaseId = baseId,
            Name = name ?? "",
            TerritoryId = territoryId,
            MapId = mapId,
            X = position.X,
            Y = position.Y,
            Z = position.Z,
        });
        return true;
    }

    private static bool IsChestType(DevMarkerType type)
    {
        return type is DevMarkerType.SilverChest
            or DevMarkerType.BronzeChest
            or DevMarkerType.FortuneCarrotChest
            or DevMarkerType.PotChest
            or DevMarkerType.RerollChest
            or DevMarkerType.UnknownChest;
    }

    private static bool AreMergeableMarkerTypes(DevMarkerType left, DevMarkerType right)
    {
        return left == right
               || (IsChestType(left)
                   && IsChestType(right)
                   && (left == DevMarkerType.UnknownChest
                       || right == DevMarkerType.UnknownChest));
    }

    private static float HorizontalDistance(Vector3 left, Vector3 right)
    {
        return Vector2.Distance(new Vector2(left.X, left.Z), new Vector2(right.X, right.Z));
    }

    private static bool IsValidPosition(Vector3 position)
    {
        return position != Vector3.Zero
               && float.IsFinite(position.X)
               && float.IsFinite(position.Y)
               && float.IsFinite(position.Z);
    }

    private static DevMapMarker CloneMarker(DevMapMarker marker)
    {
        return new DevMapMarker
        {
            Id = marker.Id,
            Type = marker.Type,
            EventId = marker.EventId,
            BaseId = marker.BaseId,
            Level = marker.Level,
            ObservationCount = marker.ObservationCount,
            Name = marker.Name,
            TerritoryId = marker.TerritoryId,
            MapId = marker.MapId,
            X = marker.X,
            Y = marker.Y,
            Z = marker.Z,
            CreatedAt = marker.CreatedAt,
        };
    }

    private void DrawMarkerButton(string label, DevMarkerType type)
    {
        if (ImGui.Button($"{label}##DevMap_{type}"))
        {
            if (type == DevMarkerType.InvestigationLocation)
            {
                CaptureTargetInvestigationLocation();
            }
            else
            {
                AddCurrentPosition(type);
            }
        }
    }

    private void CaptureTargetInvestigationLocation()
    {
        var player = Svc.Objects.LocalPlayer;
        var investigation = Svc.Targets.Target as IEventObj
                            ?? Svc.Objects.OfType<IEventObj>()
                                .Where(obj =>
                                    obj.IsValid()
                                    && obj.Name.TextValue.Contains(
                                        "调查",
                                        StringComparison.Ordinal
                                    ))
                                .OrderBy(obj =>
                                    player == null
                                        ? float.MaxValue
                                        : Vector3.Distance(player.Position, obj.Position))
                                .FirstOrDefault();
        if (investigation == null
            || player == null
            || Vector3.Distance(player.Position, investigation.Position) > 30f)
        {
            lastError = "请先选中 30 yalms 内的调查地点对象。";
            return;
        }

        if (RecordDetectedMarker(
                DevMarkerType.InvestigationLocation,
                investigation.Position,
                Svc.ClientState.TerritoryType,
                Svc.ClientState.MapId,
                name: investigation.Name.TextValue,
                baseId: investigation.BaseId
            )
            && SaveMarkers())
        {
            lastError = null;
            Svc.Chat.Print(
                $"[BOCCHI] 已记录调查地点：BaseId={investigation.BaseId}，"
                + $"坐标=({investigation.Position.X:F2}, {investigation.Position.Y:F2}, "
                + $"{investigation.Position.Z:F2})。"
            );
        }
    }

    private void AddCurrentPosition(DevMarkerType type)
    {
        var player = Svc.Objects.LocalPlayer;
        var territoryId = Svc.ClientState.TerritoryType;
        var mapId = Svc.ClientState.MapId;
        if (player == null || territoryId == 0 || mapId == 0)
        {
            lastError = "无法取得当前玩家坐标、Territory 或 Map。";
            return;
        }

        var position = player.Position;
        markerFile.Markers.Add(new DevMapMarker
        {
            Type = type,
            TerritoryId = territoryId,
            MapId = mapId,
            X = position.X,
            Y = position.Y,
            Z = position.Z,
        });

        if (!SaveMarkers())
        {
            markerFile.Markers.RemoveAt(markerFile.Markers.Count - 1);
            return;
        }

        lastError = null;
        Svc.Chat.Print(
            $"[BOCCHI] 已保存“{GetLabel(type)}”坐标：({position.X:F2}, {position.Y:F2}, {position.Z:F2})"
        );
    }

    private void LoadForkedTowerEventObjects()
    {
        try
        {
            if (!File.Exists(ForkedTowerEventObjPath))
            {
                forkedTowerEventObjFile = new ForkedTowerEventObjFile();
                return;
            }

            forkedTowerEventObjFile =
                JsonSerializer.Deserialize<ForkedTowerEventObjFile>(
                    File.ReadAllText(ForkedTowerEventObjPath),
                    jsonOptions
                )
                ?? new ForkedTowerEventObjFile();
            var sourceVersion = forkedTowerEventObjFile.Version;
            forkedTowerEventObjFile.EventObjects ??= [];
            forkedTowerEventObjFile.TrapGroups ??= [];

            var changed = forkedTowerEventObjFile.Version < 3;
            forkedTowerEventObjFile.Version = 3;
            foreach (var record in forkedTowerEventObjFile.EventObjects)
            {
                record.Name ??= "";
                record.TowerRunId ??= "";
                record.ObservedRunIds ??= [];
                if (record.TowerRunId.Length > 0
                    && !record.ObservedRunIds.Contains(record.TowerRunId))
                {
                    record.ObservedRunIds.Add(record.TowerRunId);
                    changed = true;
                }

                if (record.Id == Guid.Empty)
                {
                    record.Id = Guid.NewGuid();
                    changed = true;
                }

                var (knownType, knownRadius) =
                    GetBuiltInForkedTowerObjectType(record.BaseId);
                if (knownType != ForkedTowerEventObjType.Unknown
                    && (record.Type != knownType
                        || record.MechanicRadius != knownRadius))
                {
                    record.Type = knownType;
                    record.MechanicRadius = knownRadius;
                    changed = true;
                }
            }

            changed |= NormalizeTrapGroups();

            if (changed)
            {
                if (sourceVersion < 3)
                {
                    CreateMigrationBackup(ForkedTowerEventObjPath, sourceVersion);
                }

                SaveForkedTowerEventObjects();
            }
        }
        catch (Exception ex)
        {
            forkedTowerEventObjFile = new ForkedTowerEventObjFile();
            lastError = $"读取 Tower EventObj JSON 失败：{ex.Message}";
            Svc.Log.Error(ex, "Failed to load Forked Tower EventObj records");
        }
    }

    private bool SaveForkedTowerEventObjects()
    {
        try
        {
            Directory.CreateDirectory(Svc.PluginInterface.ConfigDirectory.FullName);
            var tempPath = ForkedTowerEventObjPath + ".tmp";
            File.WriteAllText(
                tempPath,
                JsonSerializer.Serialize(forkedTowerEventObjFile, jsonOptions)
            );
            File.Move(tempPath, ForkedTowerEventObjPath, true);
            return true;
        }
        catch (Exception ex)
        {
            lastError = $"保存 Tower EventObj JSON 失败：{ex.Message}";
            Svc.Log.Error(ex, "Failed to save Forked Tower EventObj records");
            return false;
        }
    }

    private bool NormalizeTrapGroups()
    {
        var changed = false;
        var validCandidateIds = forkedTowerEventObjFile.EventObjects
            .Where(record => record.Type != ForkedTowerEventObjType.Unknown)
            .Select(record => record.Id)
            .ToHashSet();
        var claimedCandidateIds = new HashSet<Guid>();

        foreach (var group in forkedTowerEventObjFile.TrapGroups)
        {
            group.Name ??= "";
            group.CandidateIds ??= [];
            if (group.Id == Guid.Empty)
            {
                group.Id = Guid.NewGuid();
                changed = true;
            }

            var normalizedIds = group.CandidateIds
                .Where(id => validCandidateIds.Contains(id) && claimedCandidateIds.Add(id))
                .ToList();
            if (!normalizedIds.SequenceEqual(group.CandidateIds))
            {
                group.CandidateIds = normalizedIds;
                changed = true;
            }

            if (group.CandidateIds.Count == 0)
            {
                continue;
            }

            var clampedMaxActive = Math.Clamp(
                group.MaxActive,
                1,
                group.CandidateIds.Count
            );
            if (clampedMaxActive != group.MaxActive)
            {
                group.MaxActive = clampedMaxActive;
                changed = true;
            }
        }

        changed |= forkedTowerEventObjFile.TrapGroups.RemoveAll(group =>
            group.CandidateIds.Count == 0
        ) > 0;
        return changed;
    }

    private void LoadMarkers()
    {
        try
        {
            if (!File.Exists(MarkerPath))
            {
                markerFile = new DevMapMarkerFile();
                return;
            }

            markerFile = JsonSerializer.Deserialize<DevMapMarkerFile>(
                File.ReadAllText(MarkerPath),
                jsonOptions
            ) ?? new DevMapMarkerFile();
            var sourceVersion = markerFile.Version;
            markerFile.Markers ??= [];

            if (MigrateMarkers())
            {
                if (sourceVersion < 9)
                {
                    CreateMigrationBackup(MarkerPath, sourceVersion);
                }

                SaveMarkers();
            }
        }
        catch (Exception ex)
        {
            markerFile = new DevMapMarkerFile();
            lastError = $"读取标注 JSON 失败：{ex.Message}";
            Svc.Log.Error(ex, "Failed to load dev map markers");
        }
    }

    private bool MigrateMarkers()
    {
        var changed = markerFile.Version < 9;
        markerFile.Version = 9;

        changed |= markerFile.Markers.RemoveAll(marker =>
            marker.Type == DevMarkerType.InvestigationLocation
            && marker.Name?.Contains("魔路", StringComparison.Ordinal) == true
        ) > 0;

        foreach (var marker in markerFile.Markers)
        {
            marker.Name ??= "";
            if (marker.ObservationCount < 1)
            {
                marker.ObservationCount = 1;
                changed = true;
            }

            if (marker.Id == Guid.Empty)
            {
                marker.Id = Guid.NewGuid();
                changed = true;
            }

            if (marker.Type == DevMarkerType.FortuneCarrotChest)
            {
                marker.Type = DevMarkerType.FortuneCarrot;
                changed = true;
            }

            if (marker.Type is DevMarkerType.Fate or DevMarkerType.CriticalEncounter)
            {
                changed |= BackfillEventMarker(marker);
            }
        }

        // Carrot coordinates are authoritative: remove any chest marker left at the
        // same spot by an older file or by observing the spawned carrot coffer.
        foreach (var carrot in markerFile.Markers
                     .Where(marker => marker.Type == DevMarkerType.FortuneCarrot)
                     .OrderBy(marker => marker.CreatedAt)
                     .ToList())
        {
            var removed = markerFile.Markers.RemoveAll(candidate =>
                !ReferenceEquals(candidate, carrot)
                && candidate.TerritoryId == carrot.TerritoryId
                && candidate.MapId == carrot.MapId
                && IsChestType(candidate.Type)
                && HorizontalDistance(candidate.Position, carrot.Position) <= ChestMergeDistance
            );
            changed |= removed > 0;
        }

        var kept = new List<DevMapMarker>();
        foreach (var marker in markerFile.Markers.OrderBy(marker => marker.CreatedAt))
        {
            var mergeDistance = marker.Type switch
            {
                DevMarkerType.Fate or DevMarkerType.CriticalEncounter => EventMergeDistance,
                DevMarkerType.Monster => MonsterMergeDistance,
                _ => ChestMergeDistance,
            };
            var duplicate = kept.FirstOrDefault(existing =>
                existing.TerritoryId == marker.TerritoryId
                && existing.MapId == marker.MapId
                && AreMergeableMarkerTypes(existing.Type, marker.Type)
                && (marker.Type != DevMarkerType.Monster
                    || marker.BaseId == existing.BaseId)
                && (marker.Type is not (DevMarkerType.Fate or DevMarkerType.CriticalEncounter)
                    || (marker.EventId != 0 && existing.EventId == marker.EventId)
                    || (marker.EventId == 0 && existing.EventId == 0))
                && HorizontalDistance(existing.Position, marker.Position) <= mergeDistance
            );

            if (duplicate != null)
            {
                if (marker.Type == DevMarkerType.Monster)
                {
                    var duplicateCount = Math.Max(1, duplicate.ObservationCount);
                    var markerCount = Math.Max(1, marker.ObservationCount);
                    var combinedCount = duplicateCount + markerCount;
                    duplicate.X =
                        (duplicate.X * duplicateCount + marker.X * markerCount) / combinedCount;
                    duplicate.Y =
                        (duplicate.Y * duplicateCount + marker.Y * markerCount) / combinedCount;
                    duplicate.Z =
                        (duplicate.Z * duplicateCount + marker.Z * markerCount) / combinedCount;
                    duplicate.ObservationCount = combinedCount;
                    duplicate.Level = marker.Level != 0 ? marker.Level : duplicate.Level;
                    if (!string.IsNullOrWhiteSpace(marker.Name))
                    {
                        duplicate.Name = marker.Name;
                    }
                }

                if (duplicate.Type == DevMarkerType.UnknownChest
                    && marker.Type != DevMarkerType.UnknownChest)
                {
                    duplicate.Type = marker.Type;
                    duplicate.X = marker.X;
                    duplicate.Y = marker.Y;
                    duplicate.Z = marker.Z;
                }

                if (duplicate.EventId == 0 && marker.EventId != 0)
                {
                    duplicate.EventId = marker.EventId;
                }

                if (string.IsNullOrWhiteSpace(duplicate.Name)
                    && !string.IsNullOrWhiteSpace(marker.Name))
                {
                    duplicate.Name = marker.Name;
                }

                if (duplicate.Level == 0 && marker.Level != 0)
                {
                    duplicate.Level = marker.Level;
                }

                changed = true;
                continue;
            }

            kept.Add(marker);
        }

        markerFile.Markers = kept;
        return changed;
    }

    private bool BackfillEventMarker(DevMapMarker marker)
    {
        if (marker.TerritoryId != ZoneData.SOUTHHORN)
        {
            return false;
        }

        var eventData = marker.Type == DevMarkerType.Fate
            ? EventData.Fates.Values
            : EventData.CriticalEncounters.Values;
        var nearest = eventData
            .Where(data => data.StartPosition.HasValue)
            .Select(data => new
            {
                Data = data,
                Distance = HorizontalDistance(data.StartPosition!.Value, marker.Position),
            })
            .OrderBy(entry => entry.Distance)
            .FirstOrDefault();
        if (nearest == null || nearest.Distance > EventMergeDistance)
        {
            return false;
        }

        var changed = false;
        if (marker.EventId == 0)
        {
            marker.EventId = nearest.Data.Id;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(marker.Name))
        {
            var key = $"{marker.TerritoryId}:{nearest.Data.Id}";
            var recordedNames = marker.Type == DevMarkerType.Fate
                ? PluginConfig.AutomatorConfig.RecordedFateNames
                : PluginConfig.AutomatorConfig.RecordedCriticalEncounterNames;
            marker.Name =
                recordedNames.GetValueOrDefault(key, nearest.Data.InternalName)
                ?? nearest.Data.InternalName
                ?? "";
            changed = marker.Name.Length > 0 || changed;
        }

        return changed;
    }

    private bool SaveMarkers()
    {
        try
        {
            Directory.CreateDirectory(Svc.PluginInterface.ConfigDirectory.FullName);
            var tempPath = MarkerPath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(markerFile, jsonOptions));
            File.Move(tempPath, MarkerPath, true);
            return true;
        }
        catch (Exception ex)
        {
            lastError = $"保存标注 JSON 失败：{ex.Message}";
            Svc.Log.Error(ex, "Failed to save dev map markers");
            return false;
        }
    }

    private static void CreateMigrationBackup(string path, int sourceVersion)
    {
        try
        {
            var backupPath = $"{path}.v{sourceVersion}.bak";
            if (!File.Exists(backupPath))
            {
                File.Copy(path, backupPath);
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(
                ex,
                "Failed to create dev map migration backup for {Path}",
                path
            );
        }
    }

    private void DrawDevMapUi()
    {
        if (!WorldObjectScanGuard.IsSafe())
        {
            // AreaMap may be finalized without another usable draw callback
            // during zone transitions. Never carry its native pointer into the
            // next territory.
            areaMapAddonAddress = nint.Zero;
            return;
        }

        GetModule<ForkedTowerModule>().EnsureRunLifecycle();
        AutoScanReadOnlyMapContent();
        // Ocelot's normal update loop is deliberately limited to South Horn.
        // Running this throttled scan from UiBuilder keeps dev collection active
        // in a force-bound North territory without enabling every South module.
        AutoScan();

        ImGui.SetNextWindowPos(new Vector2(-10000f, -10000f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(Vector2.One, ImGuiCond.Always);
        var flags = ImGuiWindowFlags.NoDecoration
                    | ImGuiWindowFlags.NoBackground
                    | ImGuiWindowFlags.NoSavedSettings
                    | ImGuiWindowFlags.NoInputs
                    | ImGuiWindowFlags.NoFocusOnAppearing
                    | ImGuiWindowFlags.NoBringToFrontOnFocus;

        ImGui.Begin("##BOCCHI_DevMapHost", flags);
        DrawAreaMapOverlay();
        ImGui.End();

        DrawMarkerFilterOverlay();
    }

    private void OnAreaMapAvailable(AddonEvent type, AddonArgs args)
    {
        areaMapAddonAddress = args.Addon.Address;
    }

    private void OnAreaMapFinalizing(AddonEvent type, AddonArgs args)
    {
        if (areaMapAddonAddress == args.Addon.Address)
        {
            areaMapAddonAddress = nint.Zero;
        }
    }

    private unsafe void DrawAreaMapOverlay()
    {
        var sharedMarkers = GetSharedMarkers();
        var hasSharedDrawableMarkers = sharedMarkers.Any(IsSharedDrawableMarker);
        if (markerFile.Markers.Count == 0
            && linkerMarkers.Count == 0
            && !hasSharedDrawableMarkers
            && (!PluginConfig.DevModeEnabled
                || !PluginConfig.ShowForkedTowerEventObjectsOnMap
                || forkedTowerEventObjFile.EventObjects.Count == 0))
        {
            return;
        }

        if (!TryGetAreaMapAddon(out var addon, out _)
            || !addon->AtkUnitBase.IsVisible)
        {
            return;
        }

        var agentMap = AgentMap.Instance();
        var componentMap = addon->AreaMap.ComponentMap;
        if (agentMap == null || componentMap == null || componentMap->OwnerNode == null)
        {
            return;
        }

        var territoryId = agentMap->SelectedTerritoryId;
        var mapId = agentMap->SelectedMapId;
        if (!ZoneData.IsPluginTerritory(territoryId)
            || !Svc.Data.GetExcelSheet<Map>().TryGetRow(mapId, out var mapRow))
        {
            return;
        }

        Bounds bounds;
        componentMap->OwnerNode->AtkResNode.GetBounds(&bounds);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var clipMin = new Vector2(bounds.Pos1.X, bounds.Pos1.Y);
        var clipMax = new Vector2(bounds.Pos2.X, bounds.Pos2.Y);
        var nodeWidth = Math.Max(1, (int)componentMap->OwnerNode->AtkResNode.Width);
        var uiScale = bounds.Width / (float)nodeWidth;
        var ownerPosition = new Vector2(
            componentMap->OwnerNode->AtkResNode.X,
            componentMap->OwnerNode->AtkResNode.Y
        );
        // Match KamiToolKit's AreaMap overlay transform. The game's 2048x2048
        // map plane is anchored at (18, 46) in the addon, which is not always
        // the same as ComponentMap.OwnerNode's current position (9, 27 on CN).
        var center = new Vector2(bounds.CenterX, bounds.CenterY)
                     + (new Vector2(18f, 46f) - ownerPosition) * uiScale;
        var markerZoom = addon->AreaMap.MapScale * uiScale;
        var panZoom = componentMap->MapScale * uiScale;
        // The map plane is 2048 pixels wide. Relative to the visible map bounds,
        // this yields 1 at the fitted zoom and grows as the user zooms in.
        // Only monster text uses this factor; icon markers remain screen-sized.
        var monsterTextZoom = Math.Clamp(2048f * markerZoom / bounds.Width, 1f, 3f);
        var sheetScale = mapRow.SizeFactor / 100f;
        var sheetOffset = new Vector2(mapRow.OffsetX, mapRow.OffsetY) * (sheetScale - 1f);
        var pan = new Vector2(
            addon->AreaMap.MapOffsetX + agentMap->SelectedOffsetX,
            addon->AreaMap.MapOffsetY + agentMap->SelectedOffsetY
        );

        var drawList = ImGui.GetForegroundDrawList();
        drawList.PushClipRect(clipMin, clipMax, true);
        var monsterMarkers = new List<MonsterMapMarker>();

        foreach (var marker in markerFile.Markers.Where(m =>
                     m.TerritoryId == territoryId
                     && m.MapId == mapId
                     && IsMarkerVisible(m.Type)
                     && !IsSupersededByLinkerCatalog(m)
                 ))
        {
            if (marker.Type == DevMarkerType.Monster)
            {
                if (marker.Level != 1)
                {
                    monsterMarkers.Add(new MonsterMapMarker(marker, false));
                }

                continue;
            }

            var mapPosition = new Vector2(marker.X, marker.Z) * sheetScale + sheetOffset;
            var screenPosition = center
                                 - (pan + new Vector2(1024f)) * panZoom
                                 + (mapPosition + new Vector2(1024f)) * markerZoom;
            if (screenPosition.X < clipMin.X || screenPosition.X > clipMax.X
                || screenPosition.Y < clipMin.Y || screenPosition.Y > clipMax.Y)
            {
                continue;
            }

            DrawMarker(
                drawList,
                marker,
                screenPosition,
                uiScale,
                false
            );
        }

        foreach (var marker in linkerMarkers.Where(m =>
                     m.TerritoryId == territoryId
                     && m.MapId == mapId
                     && IsMarkerVisible(m.Type)
                 ))
        {
            var mapPosition = new Vector2(marker.X, marker.Z) * sheetScale + sheetOffset;
            var screenPosition = center
                                 - (pan + new Vector2(1024f)) * panZoom
                                 + (mapPosition + new Vector2(1024f)) * markerZoom;
            if (screenPosition.X < clipMin.X || screenPosition.X > clipMax.X
                || screenPosition.Y < clipMin.Y || screenPosition.Y > clipMax.Y)
            {
                continue;
            }

            DrawMarker(
                drawList,
                marker,
                screenPosition,
                uiScale,
                false,
                false,
                "Eureka Linker 权威点位（只读）"
            );
        }

        foreach (var sharedMarker in sharedMarkers.Where(marker =>
                     marker.TerritoryId == territoryId
                     && marker.MapId == mapId
                     && IsSharedDrawableMarker(marker)
                     && !(string.Equals(
                              marker.Source,
                              "monster",
                              StringComparison.OrdinalIgnoreCase
                          )
                          && marker.Level == 1)
                     && !IsSupersededByLinkerCatalog(marker)
                     && !HasLocalEquivalent(marker)
                 ))
        {
            var mapPosition = new Vector2(sharedMarker.X, sharedMarker.Z) * sheetScale
                              + sheetOffset;
            var screenPosition = center
                                 - (pan + new Vector2(1024f)) * panZoom
                                 + (mapPosition + new Vector2(1024f)) * markerZoom;
            if (screenPosition.X < clipMin.X || screenPosition.X > clipMax.X
                || screenPosition.Y < clipMin.Y || screenPosition.Y > clipMax.Y)
            {
                continue;
            }

            if (IsSharedTrapMarker(sharedMarker))
            {
                DrawSharedTrapMarker(
                    drawList,
                    sharedMarker,
                    screenPosition,
                    uiScale,
                    sheetScale * markerZoom
                );
            }
            else if (TryGetSharedDevMarkerType(sharedMarker, out var markerType)
                     && IsMarkerVisible(markerType))
            {
                var devMarker = new DevMapMarker
                {
                    Type = markerType,
                    EventId = sharedMarker.EventId ?? 0,
                    BaseId = sharedMarker.BaseId ?? 0,
                    Level = sharedMarker.Level ?? 0,
                    Name = sharedMarker.Name ?? "",
                    TerritoryId = sharedMarker.TerritoryId,
                    MapId = sharedMarker.MapId,
                    X = sharedMarker.X,
                    Y = sharedMarker.Y,
                    Z = sharedMarker.Z,
                };
                if (markerType == DevMarkerType.Monster)
                {
                    monsterMarkers.Add(new MonsterMapMarker(devMarker, true));
                }
                else
                {
                    DrawMarker(
                        drawList,
                        devMarker,
                        screenPosition,
                        uiScale,
                        false,
                        true
                    );
                }
            }
        }

        string? hoveredMonsterDetails = null;
        foreach (var cluster in GetMonsterClusters(
                     monsterMarkers,
                     territoryId,
                     mapId,
                     sheetScale * markerZoom,
                     uiScale,
                     monsterTextZoom
                 ))
        {
            var mapPosition = new Vector2(cluster.Center.X, cluster.Center.Z) * sheetScale
                              + sheetOffset;
            var screenPosition = center
                                 - (pan + new Vector2(1024f)) * panZoom
                                 + (mapPosition + new Vector2(1024f)) * markerZoom;
            if (screenPosition.X < clipMin.X || screenPosition.X > clipMax.X
                || screenPosition.Y < clipMin.Y || screenPosition.Y > clipMax.Y)
            {
                continue;
            }

            hoveredMonsterDetails = DrawMonsterCluster(
                drawList,
                cluster,
                screenPosition,
                uiScale,
                monsterTextZoom
            ) ?? hoveredMonsterDetails;
        }

        if (PluginConfig.DevModeEnabled
            && PluginConfig.ShowForkedTowerEventObjectsOnMap)
        {
            if (PluginConfig.ShowUnknownForkedTowerEventObjectsOnMap)
            {
                foreach (var record in forkedTowerEventObjFile.EventObjects.Where(record =>
                             record.TerritoryId == territoryId
                             && record.MapId == mapId
                             && record.Type == ForkedTowerEventObjType.Unknown
                         ))
                {
                    var mapPosition = new Vector2(record.X, record.Z) * sheetScale + sheetOffset;
                    var screenPosition = center
                                         - (pan + new Vector2(1024f)) * panZoom
                                         + (mapPosition + new Vector2(1024f)) * markerZoom;
                    if (screenPosition.X < clipMin.X || screenPosition.X > clipMax.X
                        || screenPosition.Y < clipMin.Y || screenPosition.Y > clipMax.Y)
                    {
                        continue;
                    }

                    DrawForkedTowerEventObject(
                        drawList,
                        record,
                        screenPosition,
                        uiScale,
                        sheetScale * markerZoom
                    );
                }
            }

            if (PluginConfig.ShowForkedTowerPotentialTrapPositionsOnMap)
            {
                var candidates = GetTowerTrapCandidates(territoryId, mapId);
                var groupLabelOwners = candidates
                    .GroupBy(candidate => candidate.GroupKey)
                    .ToDictionary(
                        group => group.Key,
                        group =>
                        {
                            var centerPosition = new Vector3(
                                group.Average(candidate => candidate.Position.X),
                                group.Average(candidate => candidate.Position.Y),
                                group.Average(candidate => candidate.Position.Z)
                            );
                            return group.OrderBy(candidate =>
                                    Vector3.Distance(candidate.Position, centerPosition)
                                )
                                .First();
                        }
                    );

                foreach (var candidate in candidates.OrderBy(candidate =>
                             candidate.IsObservedInCurrentRun ? 1 : 0
                         ))
                {
                    var mapPosition =
                        new Vector2(candidate.Position.X, candidate.Position.Z) * sheetScale
                        + sheetOffset;
                    var screenPosition = center
                                         - (pan + new Vector2(1024f)) * panZoom
                                         + (mapPosition + new Vector2(1024f)) * markerZoom;
                    if (screenPosition.X < clipMin.X || screenPosition.X > clipMax.X
                        || screenPosition.Y < clipMin.Y || screenPosition.Y > clipMax.Y)
                    {
                        continue;
                    }

                    DrawForkedTowerTrapCandidate(
                        drawList,
                        candidate,
                        screenPosition,
                        uiScale,
                        sheetScale * markerZoom,
                        ReferenceEquals(
                            candidate,
                            groupLabelOwners.GetValueOrDefault(candidate.GroupKey)
                        )
                    );
                }
            }
        }

        drawList.PopClipRect();
        if (!string.IsNullOrWhiteSpace(hoveredMonsterDetails))
        {
            DrawForegroundTooltip(drawList, hoveredMonsterDetails, uiScale);
        }
    }

    internal List<TowerMapTrapCandidate> GetTowerTrapCandidates(
        uint territoryId,
        uint mapId,
        bool includeAllMaps = false
    )
    {
        var towerRun = GetModule<ForkedTowerModule>().TowerRun;
        var currentRunId = towerRun.Hash;
        var livePositionsByBaseId = new Dictionary<uint, List<Vector3>>();
        if (territoryId == Svc.ClientState.TerritoryType
            && ZoneData.IsInForkedTower())
        {
            foreach (var eventObj in Svc.Objects.OfType<IEventObj>())
            {
                if (eventObj.BaseId == 0
                    || !eventObj.IsValid()
                    || !IsValidPosition(eventObj.Position))
                {
                    continue;
                }

                if (!livePositionsByBaseId.TryGetValue(
                        eventObj.BaseId,
                        out var positions
                    ))
                {
                    positions = [];
                    livePositionsByBaseId.Add(eventObj.BaseId, positions);
                }

                positions.Add(eventObj.Position);
            }
        }

        var knownRecords = forkedTowerEventObjFile.EventObjects
            .Where(record =>
                record.TerritoryId == territoryId
                && record.Type != ForkedTowerEventObjType.Unknown
            )
            .ToList();
        var includedRecordIds = new HashSet<Guid>();
        var candidates = new List<TowerMapTrapCandidate>();

        // FTB already ships a complete set of mutually exclusive candidate groups.
        // Their AreaMap sheets are stable and explicit; captured MapId is only the
        // map selected while scanning and must not decide whether a group exists.
        if (territoryId == ZoneData.SOUTHHORN
            && TrapData.Groups.Count != ExpectedBuiltInTrapGroupCount)
        {
            if (!warnedUnexpectedBuiltInTrapGroupCount)
            {
                DebugLog.Warning(
                    "FTB trap group map metadata expects {Expected} groups, but found {Actual}; "
                    + "built-in AreaMap candidates are disabled to avoid placing groups on the wrong floor.",
                    ExpectedBuiltInTrapGroupCount,
                    TrapData.Groups.Count
                );
                warnedUnexpectedBuiltInTrapGroupCount = true;
            }
        }
        else if (territoryId == ZoneData.SOUTHHORN)
        {
            for (var groupIndex = 0; groupIndex < TrapData.Groups.Count; groupIndex++)
            {
                var builtInGroup = TrapData.Groups[groupIndex];
                var matchingRecords = knownRecords
                    .Where(record => builtInGroup.Traps.Any(trap =>
                        MatchesTrapCandidate(record, trap)
                    ))
                    .ToList();
                foreach (var matchingRecord in matchingRecords)
                {
                    includedRecordIds.Add(matchingRecord.Id);
                }

                if (!includeAllMaps && GetBuiltInTrapMapId(groupIndex) != mapId)
                {
                    continue;
                }

                var groupName = $"F{groupIndex + 1:D3}";
                foreach (var trap in builtInGroup.Traps)
                {
                    var baseId = trap.Type == OccultObjectType.BigTrap
                        ? (uint)OccultObjectType.BigTrap
                        : (uint)OccultObjectType.Trap;
                    var sourceRecord = matchingRecords
                        .Where(record => MatchesTrapCandidate(record, trap))
                        .OrderByDescending(record =>
                            WasObservedInRun(record, currentRunId)
                        )
                        .ThenByDescending(record => record.MapId == mapId)
                        .FirstOrDefault(record => MatchesTrapCandidate(record, trap));

                    candidates.Add(new TowerMapTrapCandidate
                    {
                        Position = trap.Position,
                        BaseId = baseId,
                        Type = trap.Type == OccultObjectType.BigTrap
                            ? ForkedTowerEventObjType.BigTrap
                            : ForkedTowerEventObjType.SmallTrap,
                        MechanicRadius = trap.Type == OccultObjectType.BigTrap ? 30f : 7f,
                        GroupKey = $"builtin:{groupIndex}",
                        GroupName = groupName,
                        MaxActive = Math.Max(1, (int)builtInGroup.MaxInGroup),
                        IsBuiltInGroup = true,
                        IsObservedInCurrentRun =
                            WasObservedInRun(sourceRecord, currentRunId)
                            || towerRun.HasDiscoveredTrap(trap.Position, trap.Type)
                            || ObserveLiveCandidate(
                                towerRun,
                                livePositionsByBaseId,
                                baseId,
                                trap.Position
                            ),
                        SourceRecord = sourceRecord,
                    });
                }
            }
        }

        // Captured North/future traps are candidate points too. They can be manually
        // assigned to persisted mutually exclusive groups from the map editor.
        foreach (var record in knownRecords.Where(record =>
                     (includeAllMaps || record.MapId == mapId)
                     && !includedRecordIds.Contains(record.Id)
                 ))
        {
            var customGroup = forkedTowerEventObjFile.TrapGroups.FirstOrDefault(group =>
                group.TerritoryId == territoryId
                && (includeAllMaps || group.MapId == mapId)
                && group.CandidateIds.Contains(record.Id)
            );
            var isObserved =
                WasObservedInRun(record, currentRunId)
                || ObserveLiveCandidate(
                    towerRun,
                    livePositionsByBaseId,
                    record.BaseId,
                    record.Position
                );
            var groupKey = customGroup != null
                ? $"custom:{customGroup.Id}"
                : $"single:{record.Id}";

            if (includeAllMaps)
            {
                var physicalAlias = candidates.FirstOrDefault(candidate =>
                    !candidate.IsBuiltInGroup
                    && candidate.BaseId == record.BaseId
                    && candidate.Type == record.Type
                    && Vector3.Distance(candidate.Position, record.Position) <= 0.25f
                );
                if (physicalAlias != null)
                {
                    physicalAlias.IsObservedInCurrentRun |= isObserved;
                    if (customGroup != null
                        && physicalAlias.GroupKey.StartsWith(
                            "single:",
                            StringComparison.Ordinal
                        ))
                    {
                        physicalAlias.GroupKey = groupKey;
                        physicalAlias.GroupName = customGroup.Name;
                        physicalAlias.MaxActive = Math.Max(
                            1,
                            customGroup.MaxActive
                        );
                        physicalAlias.SourceRecord = record;
                    }

                    continue;
                }
            }

            candidates.Add(new TowerMapTrapCandidate
            {
                Position = record.Position,
                BaseId = record.BaseId,
                Type = record.Type,
                MechanicRadius = record.MechanicRadius
                    ?? (record.Type == ForkedTowerEventObjType.BigTrap ? 30f : 7f),
                GroupKey = groupKey,
                GroupName = customGroup?.Name ?? "",
                MaxActive = Math.Max(1, customGroup?.MaxActive ?? 1),
                IsBuiltInGroup = false,
                IsObservedInCurrentRun = isObserved,
                SourceRecord = record,
            });
        }

        ApplyTrapCandidateStates(candidates);
        return candidates;
    }

    private static bool ObserveLiveCandidate(
        TowerRun towerRun,
        Dictionary<uint, List<Vector3>> livePositionsByBaseId,
        uint baseId,
        Vector3 candidatePosition
    )
    {
        if (livePositionsByBaseId.TryGetValue(baseId, out var livePositions)
            && livePositions.Any(position =>
                Vector3.DistanceSquared(position, candidatePosition) <= 0.75f * 0.75f
            ))
        {
            towerRun.ObserveCandidate(baseId, candidatePosition);
        }

        return towerRun.HasObservedCandidate(baseId, candidatePosition);
    }

    private static void ApplyTrapCandidateStates(List<TowerMapTrapCandidate> candidates)
    {
        foreach (var group in candidates.GroupBy(candidate => candidate.GroupKey))
        {
            var observedCount = group.Count(candidate => candidate.IsObservedInCurrentRun);
            var maxActive = Math.Clamp(group.First().MaxActive, 1, group.Count());
            foreach (var candidate in group)
            {
                var excludedByObservedVariant =
                    !candidate.IsObservedInCurrentRun
                    && group.Any(other =>
                        other.IsObservedInCurrentRun
                        && Vector3.Distance(other.Position, candidate.Position) <= 0.1f
                    );
                candidate.MaxActive = maxActive;
                candidate.ObservedInGroup = observedCount;
                candidate.IsExcludedByObservedVariant = excludedByObservedVariant;
                candidate.IsExcluded =
                    !candidate.IsObservedInCurrentRun
                    && (observedCount >= candidate.MaxActive
                        || excludedByObservedVariant);
            }
        }
    }

    private static uint GetBuiltInTrapMapId(int zeroBasedGroupIndex)
    {
        return zeroBasedGroupIndex switch
        {
            < 22 => 969,
            < 32 => 970,
            < 46 => 971,
            _ => 986,
        };
    }

    private static bool MatchesTrapCandidate(
        ForkedTowerEventObjRecord record,
        TrapDatum candidate
    )
    {
        var expectedBaseId = candidate.Type == OccultObjectType.BigTrap
            ? (uint)OccultObjectType.BigTrap
            : (uint)OccultObjectType.Trap;
        return record.BaseId == expectedBaseId
               && Vector3.Distance(record.Position, candidate.Position) <= 0.75f;
    }

    private static bool WasObservedInRun(
        ForkedTowerEventObjRecord? record,
        string currentRunId
    )
    {
        return record != null
               && currentRunId.Length > 0
               && (record.ObservedRunIds.Contains(currentRunId)
                   || record.TowerRunId == currentRunId);
    }

    private void DrawForkedTowerTrapCandidate(
        ImDrawListPtr drawList,
        TowerMapTrapCandidate candidate,
        Vector2 center,
        float uiScale,
        float pixelsPerYalm,
        bool showGroupLabel
    )
    {
        var baseColor = candidate.Type == ForkedTowerEventObjType.BigTrap
            ? new Vector4(1f, 0.55f, 0.08f, 1f)
            : new Vector4(1f, 0.18f, 0.18f, 1f);
        var isExcluded = candidate.IsExcluded;
        var hasConflict = candidate.ObservedInGroup > candidate.MaxActive;
        var markerRadius = candidate.Type == ForkedTowerEventObjType.BigTrap
            ? Math.Clamp(7f * uiScale, 6f, 11f)
            : Math.Clamp(6f * uiScale, 5f, 10f);
        var mechanicRadius = Math.Max(
            markerRadius,
            candidate.MechanicRadius * pixelsPerYalm
        );

        var fillAlpha = candidate.IsObservedInCurrentRun ? 0.16f : isExcluded ? 0.01f : 0.035f;
        var outlineAlpha = candidate.IsObservedInCurrentRun ? 0.95f : isExcluded ? 0.18f : 0.52f;
        drawList.AddCircleFilled(
            center,
            mechanicRadius,
            ImGui.ColorConvertFloat4ToU32(baseColor with { W = fillAlpha }),
            64
        );
        drawList.AddCircle(
            center,
            mechanicRadius,
            ImGui.ColorConvertFloat4ToU32(baseColor with { W = outlineAlpha }),
            64,
            candidate.IsObservedInCurrentRun ? 2f : 1.25f
        );

        if (candidate.IsObservedInCurrentRun)
        {
            drawList.AddCircleFilled(center, markerRadius + 1.5f, 0xD9000000, 24);
            drawList.AddCircleFilled(
                center,
                markerRadius,
                ImGui.ColorConvertFloat4ToU32(baseColor),
                24
            );
            drawList.AddCircle(center, markerRadius, 0xFFFFFFFF, 24, 1f);
        }
        else
        {
            drawList.AddCircle(
                center,
                markerRadius,
                ImGui.ColorConvertFloat4ToU32(baseColor with
                {
                    W = isExcluded ? 0.25f : 0.9f,
                }),
                24,
                2f
            );
        }

        if (isExcluded)
        {
            var cross = new Vector2(markerRadius * 0.72f);
            drawList.AddLine(center - cross, center + cross, 0xCCFFFFFF, 1.5f);
            drawList.AddLine(
                center + new Vector2(-cross.X, cross.Y),
                center + new Vector2(cross.X, -cross.Y),
                0xCCFFFFFF,
                1.5f
            );
        }

        if (showGroupLabel
            && PluginConfig.ShowForkedTowerTrapGroupLabelsOnMap
            && candidate.GroupName.Length > 0)
        {
            var label = candidate.GroupName;
            var labelSize = ImGui.CalcTextSize(label);
            var labelPosition = center + new Vector2(markerRadius + 3f, -labelSize.Y / 2f);
            drawList.AddRectFilled(
                labelPosition - new Vector2(2f, 1f),
                labelPosition + labelSize + new Vector2(2f, 1f),
                0xC9000000,
                2f
            );
            drawList.AddText(
                labelPosition,
                hasConflict ? 0xFF4A4AFF : 0xFFFFFFFF,
                label
            );
        }

        var hoverRadius = markerRadius + 4f;
        if (!ImGui.IsMouseHoveringRect(
                center - new Vector2(hoverRadius),
                center + new Vector2(hoverRadius),
                false
            ))
        {
            return;
        }

        drawList.AddCircle(center, markerRadius + 3f, 0xFFFFFFFF, 24, 2f);
        var state = candidate.IsObservedInCurrentRun
            ? "本次已发现"
            : isExcluded
                ? candidate.IsExcludedByObservedVariant
                    ? "同位置变体已出现，当前排除"
                    : "同组已满足，当前排除"
                : "候选点位";
        var group = candidate.GroupName.Length > 0 ? candidate.GroupName : "未编组";
        var runCount = candidate.SourceRecord?.ObservedRunIds.Count ?? 0;
        ImGui.SetTooltip(
            $"{(candidate.Type == ForkedTowerEventObjType.BigTrap ? "大雷" : "小雷")} · {state}\n"
            + $"编组: {group}（{candidate.ObservedInGroup}/{candidate.MaxActive}）\n"
            + $"坐标: ({candidate.Position.X:F2}, {candidate.Position.Y:F2}, {candidate.Position.Z:F2})\n"
            + $"机制半径: {candidate.MechanicRadius:F1}\n"
            + $"累计观察塔次: {runCount}"
            + (hasConflict ? "\n警告：本次观察数超过编组上限" : "")
        );
    }

    private void DrawForkedTowerEventObject(
        ImDrawListPtr drawList,
        ForkedTowerEventObjRecord record,
        Vector2 center,
        float uiScale,
        float pixelsPerYalm
    )
    {
        var color = GetForkedTowerEventObjColor(record);
        var colorU32 = ImGui.ColorConvertFloat4ToU32(color);
        var markerRadius = record.Type switch
        {
            ForkedTowerEventObjType.BigTrap => Math.Clamp(7f * uiScale, 6f, 11f),
            ForkedTowerEventObjType.SmallTrap => Math.Clamp(6f * uiScale, 5f, 10f),
            _ => Math.Clamp(4f * uiScale, 3.5f, 7f),
        };

        if (record.MechanicRadius is > 0f)
        {
            var mechanicRadius = Math.Max(markerRadius, record.MechanicRadius.Value * pixelsPerYalm);
            var mechanicFill = color with { W = 0.12f };
            var mechanicOutline = color with { W = 0.78f };
            drawList.AddCircleFilled(
                center,
                mechanicRadius,
                ImGui.ColorConvertFloat4ToU32(mechanicFill),
                48
            );
            drawList.AddCircle(
                center,
                mechanicRadius,
                ImGui.ColorConvertFloat4ToU32(mechanicOutline),
                48,
                1.5f
            );
        }
        else if (record.HitboxRadius > 0f)
        {
            var hitboxRadius = Math.Max(markerRadius, record.HitboxRadius * pixelsPerYalm);
            drawList.AddCircle(
                center,
                hitboxRadius,
                ImGui.ColorConvertFloat4ToU32(color with { W = 0.55f }),
                24,
                1f
            );
        }

        drawList.AddCircleFilled(center, markerRadius + 1.5f, 0xD9000000, 24);
        drawList.AddCircleFilled(center, markerRadius, colorU32, 24);
        drawList.AddCircle(center, markerRadius, 0xFFFFFFFF, 24, 1f);

        var badge = record.Type switch
        {
            ForkedTowerEventObjType.SmallTrap => "S",
            ForkedTowerEventObjType.BigTrap => "B",
            _ => $"{record.BaseId % 10000:D4}",
        };
        if (PluginConfig.ShowForkedTowerEventObjLabels
            || record.Type != ForkedTowerEventObjType.Unknown)
        {
            var textSize = ImGui.CalcTextSize(badge);
            var textPosition = record.Type == ForkedTowerEventObjType.Unknown
                ? center + new Vector2(markerRadius + 3f, -textSize.Y / 2f)
                : center - textSize / 2f;
            if (record.Type == ForkedTowerEventObjType.Unknown)
            {
                drawList.AddRectFilled(
                    textPosition - new Vector2(2f, 1f),
                    textPosition + textSize + new Vector2(2f, 1f),
                    0xC9000000,
                    2f
                );
            }

            drawList.AddText(textPosition + Vector2.One, 0xFF000000, badge);
            drawList.AddText(textPosition, 0xFFFFFFFF, badge);
        }

        var hoverRadius = markerRadius + 3f;
        if (!ImGui.IsMouseHoveringRect(
                center - new Vector2(hoverRadius),
                center + new Vector2(hoverRadius),
                false
            ))
        {
            return;
        }

        drawList.AddCircle(center, markerRadius + 3f, 0xFFFFFFFF, 24, 2f);
        var mechanicRadiusText = record.MechanicRadius is > 0f
            ? $"{record.MechanicRadius.Value:F1}"
            : "未知";
        var objectName = string.IsNullOrWhiteSpace(record.Name) ? "（无名）" : record.Name;
        ImGui.SetTooltip(
            $"{objectName}\n"
            + $"BaseId: {record.BaseId}\n"
            + $"类型: {record.Type}\n"
            + $"Territory/Map: {record.TerritoryId}/{record.MapId}\n"
            + $"坐标: ({record.X:F2}, {record.Y:F2}, {record.Z:F2})\n"
            + $"HitboxRadius: {record.HitboxRadius:F2}\n"
            + $"机制半径: {mechanicRadiusText}\n"
            + $"TowerRun: {record.TowerRunId}"
        );
    }

    private static Vector4 GetForkedTowerEventObjColor(ForkedTowerEventObjRecord record)
    {
        if (record.Type == ForkedTowerEventObjType.SmallTrap)
        {
            return new Vector4(1f, 0.18f, 0.18f, 0.95f);
        }

        if (record.Type == ForkedTowerEventObjType.BigTrap)
        {
            return new Vector4(1f, 0.55f, 0.08f, 0.95f);
        }

        var hash = unchecked(record.BaseId * 2654435761u);
        var red = 0.35f + ((hash >> 16) & 0xFF) / 255f * 0.65f;
        var green = 0.35f + ((hash >> 8) & 0xFF) / 255f * 0.65f;
        var blue = 0.35f + (hash & 0xFF) / 255f * 0.65f;
        return new Vector4(red, green, blue, 0.92f);
    }

    private unsafe void DrawMarkerFilterOverlay()
    {
        if (markerFile.Markers.Count == 0
            && linkerMarkers.Count == 0
            && !GetSharedMarkers().Any(marker => TryGetSharedDevMarkerType(marker, out _)))
        {
            return;
        }

        var agentMap = AgentMap.Instance();
        if (agentMap == null
            || !TryGetAreaMapAddon(out var addon, out var addonPosition))
        {
            return;
        }

        if (!addon->AtkUnitBase.IsVisible)
        {
            return;
        }

        var componentMap = addon->AreaMap.ComponentMap;
        if (componentMap == null
            || componentMap->OwnerNode == null
            || !ZoneData.IsPluginTerritory(agentMap->SelectedTerritoryId))
        {
            return;
        }

        Bounds bounds;
        componentMap->OwnerNode->AtkResNode.GetBounds(&bounds);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var style = ImGui.GetStyle();
        var iconSize = new Vector2(32f) * ImGuiHelpers.GlobalScale;
        var windowHeight =
            iconSize.Y + style.FramePadding.Y * 2f + style.WindowPadding.Y * 2f;
        var viewport = ImGui.GetMainViewport();
        var desiredY = addonPosition.Y - windowHeight;
        var windowY = Math.Max(viewport.WorkPos.Y, desiredY);
        ImGui.SetNextWindowPos(
            new Vector2(addonPosition.X + 5f, windowY),
            ImGuiCond.Always
        );
        ImGui.SetNextWindowBgAlpha(0.92f);
        var flags = ImGuiWindowFlags.NoTitleBar
                    | ImGuiWindowFlags.NoResize
                    | ImGuiWindowFlags.AlwaysAutoResize
                    | ImGuiWindowFlags.NoMove
                    | ImGuiWindowFlags.NoSavedSettings
                    | ImGuiWindowFlags.NoScrollbar
                    | ImGuiWindowFlags.NoFocusOnAppearing;
        if (!ImGui.Begin("Linker 地图标记###BOCCHI_DevMapFilters", flags))
        {
            ImGui.End();
            return;
        }

        for (var i = 0; i < MarkerFilterTypes.Length; i++)
        {
            if (i > 0)
            {
                ImGui.SameLine();
            }

            DrawMarkerFilterButton(MarkerFilterTypes[i]);
        }

        ImGui.End();
    }

    private unsafe bool TryGetAreaMapAddon(
        out AddonAreaMap* addon,
        out Vector2 addonPosition
    )
    {
        if (areaMapAddonAddress != nint.Zero)
        {
            addon = (AddonAreaMap*)areaMapAddonAddress;
            addonPosition = new Vector2(
                addon->AtkUnitBase.X,
                addon->AtkUnitBase.Y
            );
            return true;
        }

        var namedAddon = Svc.GameGui.GetAddonByName<AddonAreaMap>("AreaMap", 1);
        if (namedAddon != null)
        {
            addon = namedAddon;
            addonPosition = new Vector2(
                namedAddon->AtkUnitBase.X,
                namedAddon->AtkUnitBase.Y
            );
            return true;
        }

        // Some client builds no longer expose the active AreaMap through the
        // Dalamud wrapper. Resolve it through the game's own unit manager,
        // which is the same route used by KamiToolKit's map overlay.
        var agentMap = AgentMap.Instance();
        var unitManager = RaptureAtkUnitManager.Instance();
        if (agentMap == null || unitManager == null)
        {
            addon = null;
            addonPosition = Vector2.Zero;
            return false;
        }

        var unit = unitManager->GetAddonByName("AreaMap", 1);
        if (unit != null)
        {
            addon = (AddonAreaMap*)unit;
            addonPosition = new Vector2(unit->X, unit->Y);
            return true;
        }

        var addonId = agentMap->AgentInterface.AddonId;
        if (addonId == 0 || addonId > ushort.MaxValue)
        {
            addon = null;
            addonPosition = Vector2.Zero;
            return false;
        }

        unit = unitManager->GetAddonById((ushort)addonId);
        if (unit == null)
        {
            addon = null;
            addonPosition = Vector2.Zero;
            return false;
        }

        addon = (AddonAreaMap*)unit;
        addonPosition = new Vector2(unit->X, unit->Y);
        return true;
    }

    private void DrawMarkerFilterButton(DevMarkerType type)
    {
        var visibility = GetVisibilityFlag(type);
        var enabled = PluginConfig.DevMapVisibleMarkers.HasFlag(visibility);
        var activeColor = ImGuiColors.HealerGreen;
        var inactiveColor = ImGuiColors.ParsedGrey;
        var hasIcon = iconIds.TryGetValue(type, out var iconId);
        var icon = hasIcon
            ? Svc.Texture.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty()
            : null;

        ImGui.PushID($"DevMapFilter_{type}");
        ImGui.PushStyleColor(ImGuiCol.Button, enabled ? activeColor : inactiveColor);
        var clicked = hasIcon && icon != null && icon.Handle != default
            ? ImGui.ImageButton(
                icon.Handle,
                new Vector2(32f * ImGuiHelpers.GlobalScale)
            )
            : ImGui.Button(
                GetBadge(type),
                new Vector2(32f * ImGuiHelpers.GlobalScale)
            );
        ImGui.PopStyleColor();
        if (clicked)
        {
            if (enabled)
            {
                PluginConfig.DevMapVisibleMarkers &= ~visibility;
            }
            else
            {
                PluginConfig.DevMapVisibleMarkers |= visibility;
            }

            PluginConfig.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"{GetLabel(type)}：{(enabled ? "显示" : "隐藏")}");
        }

        ImGui.PopID();
    }

    private bool IsMarkerVisible(DevMarkerType type)
    {
        return PluginConfig.DevMapVisibleMarkers.HasFlag(GetVisibilityFlag(type));
    }

    private static DevMapMarkerVisibility GetVisibilityFlag(DevMarkerType type)
    {
        return type switch
        {
            DevMarkerType.SilverChest => DevMapMarkerVisibility.SilverChest,
            DevMarkerType.BronzeChest => DevMapMarkerVisibility.BronzeChest,
            DevMarkerType.FortuneCarrot or DevMarkerType.FortuneCarrotChest =>
                DevMapMarkerVisibility.FortuneCarrot,
            DevMarkerType.PotChest => DevMapMarkerVisibility.PotChest,
            DevMarkerType.RerollChest => DevMapMarkerVisibility.RerollChest,
            DevMarkerType.Fate => DevMapMarkerVisibility.Fate,
            DevMarkerType.CriticalEncounter => DevMapMarkerVisibility.CriticalEncounter,
            DevMarkerType.InvestigationLocation =>
                DevMapMarkerVisibility.InvestigationLocation,
            DevMarkerType.UnknownChest => DevMapMarkerVisibility.UnknownChest,
            DevMarkerType.Monster => DevMapMarkerVisibility.Monster,
            _ => DevMapMarkerVisibility.None,
        };
    }

    private IReadOnlyList<TelemetryMarker> GetSharedMarkers()
    {
        if (!Plugin.Modules.TryGetModule<TelemetryModule>(out var telemetry)
            || telemetry == null
            || !telemetry.Config.ShowSharedMarkers)
        {
            return Array.Empty<TelemetryMarker>();
        }

        return telemetry.GetSharedMarkersSnapshot();
    }

    private static bool TryGetSharedDevMarkerType(
        TelemetryMarker marker,
        out DevMarkerType type
    )
    {
        type = default;
        if (string.Equals(marker.Source, "monster", StringComparison.OrdinalIgnoreCase)
            && string.Equals(marker.Kind, "Monster", StringComparison.OrdinalIgnoreCase))
        {
            type = DevMarkerType.Monster;
            return true;
        }

        if (!(string.Equals(marker.Source, "dev-map", StringComparison.OrdinalIgnoreCase)
              || string.Equals(
                  marker.Source,
                  "linker-catalog",
                  StringComparison.OrdinalIgnoreCase
              ))
            || !Enum.TryParse(marker.Kind, true, out type))
        {
            return false;
        }

        if (type == DevMarkerType.FortuneCarrotChest)
        {
            type = DevMarkerType.FortuneCarrot;
        }

        return type is DevMarkerType.SilverChest
            or DevMarkerType.BronzeChest
            or DevMarkerType.FortuneCarrot
            or DevMarkerType.PotChest
            or DevMarkerType.RerollChest
            or DevMarkerType.Fate
            or DevMarkerType.CriticalEncounter
            or DevMarkerType.InvestigationLocation
            or DevMarkerType.UnknownChest
            or DevMarkerType.Monster;
    }

    private bool IsSupersededByLinkerCatalog(TelemetryMarker marker)
    {
        if (linkerMarkers.Count == 0
            || marker.TerritoryId != NorthernLinkerMarkerCatalog.TerritoryId
            || !TryGetSharedDevMarkerType(marker, out var type))
        {
            return false;
        }

        return NorthernLinkerMarkerCatalog.IsManagedType(type)
               || (type == DevMarkerType.UnknownChest
                   && linkerMarkers.Any(catalogMarker =>
                       catalogMarker.MapId == marker.MapId
                       && HorizontalDistance(
                           catalogMarker.Position,
                           new Vector3(marker.X, marker.Y, marker.Z)
                       ) <= ChestMergeDistance
                   ));
    }

    private bool IsSupersededByLinkerCatalog(DevMapMarker marker)
    {
        if (linkerMarkers.Count == 0
            || marker.TerritoryId != NorthernLinkerMarkerCatalog.TerritoryId)
        {
            return false;
        }

        return NorthernLinkerMarkerCatalog.IsManagedType(marker.Type)
               || (marker.Type == DevMarkerType.UnknownChest
                   && linkerMarkers.Any(catalogMarker =>
                       catalogMarker.MapId == marker.MapId
                       && HorizontalDistance(catalogMarker.Position, marker.Position)
                       <= ChestMergeDistance
                   ));
    }

    private static bool IsSharedTrapMarker(TelemetryMarker marker)
    {
        return string.Equals(
                   marker.Source,
                   "tower-eventobj",
                   StringComparison.OrdinalIgnoreCase
               )
               && (string.Equals(
                       marker.Kind,
                       nameof(ForkedTowerEventObjType.SmallTrap),
                       StringComparison.OrdinalIgnoreCase
                   )
                   || string.Equals(
                       marker.Kind,
                       nameof(ForkedTowerEventObjType.BigTrap),
                       StringComparison.OrdinalIgnoreCase
                   ));
    }

    private static bool IsSharedDrawableMarker(TelemetryMarker marker)
    {
        return IsSharedTrapMarker(marker)
               || TryGetSharedDevMarkerType(marker, out _);
    }

    private bool HasLocalEquivalent(TelemetryMarker sharedMarker)
    {
        var position = new Vector3(sharedMarker.X, sharedMarker.Y, sharedMarker.Z);
        if (IsSharedTrapMarker(sharedMarker))
        {
            return forkedTowerEventObjFile.EventObjects.Any(record =>
                record.TerritoryId == sharedMarker.TerritoryId
                && record.MapId == sharedMarker.MapId
                && record.Type != ForkedTowerEventObjType.Unknown
                && (sharedMarker.BaseId is not { } baseId || record.BaseId == baseId)
                && Vector3.Distance(record.Position, position) <= 0.75f
            );
        }

        if (!TryGetSharedDevMarkerType(sharedMarker, out var type))
        {
            return false;
        }

        var mergeDistance = type switch
        {
            DevMarkerType.Fate or DevMarkerType.CriticalEncounter => EventMergeDistance,
            DevMarkerType.Monster => MonsterMergeDistance,
            _ => ChestMergeDistance,
        };
        return markerFile.Markers.Any(local =>
            local.TerritoryId == sharedMarker.TerritoryId
            && local.MapId == sharedMarker.MapId
            && (local.Type == type || AreMergeableMarkerTypes(local.Type, type))
            && (type != DevMarkerType.Monster || local.Level != 1)
            && (type != DevMarkerType.Monster
                || sharedMarker.BaseId is not { } baseId
                || local.BaseId == baseId)
            && Vector3.Distance(local.Position, position) <= mergeDistance
        );
    }

    private static void DrawSharedTrapMarker(
        ImDrawListPtr drawList,
        TelemetryMarker marker,
        Vector2 center,
        float uiScale,
        float pixelsPerYalm
    )
    {
        var markerRadius = Math.Clamp(6f * uiScale, 5f, 10f);
        var mechanicRadiusYalms = marker.MechanicRadius is > 0f
            ? marker.MechanicRadius.Value
            : string.Equals(
                marker.Kind,
                nameof(ForkedTowerEventObjType.BigTrap),
                StringComparison.OrdinalIgnoreCase
            )
                ? 30f
                : 7f;
        var mechanicRadius = Math.Max(markerRadius, mechanicRadiusYalms * pixelsPerYalm);
        const uint fillColor = 0x283030FF;
        const uint outlineColor = 0xE83030FF;
        const uint pointColor = 0xFF3030FF;
        drawList.AddCircleFilled(center, mechanicRadius, fillColor, 64);
        drawList.AddCircle(center, mechanicRadius, outlineColor, 64, 2f);
        drawList.AddCircleFilled(center, markerRadius + 1.5f, 0xD9000000, 24);
        drawList.AddCircleFilled(center, markerRadius, pointColor, 24);
        drawList.AddCircle(center, markerRadius, 0xFFFFFFFF, 24, 1f);

        var hoverRadius = markerRadius + 4f;
        if (ImGui.IsMouseHoveringRect(
                center - new Vector2(hoverRadius),
                center + new Vector2(hoverRadius),
                false
            ))
        {
            var name = string.IsNullOrWhiteSpace(marker.Name) ? marker.Kind : marker.Name;
            ImGui.SetTooltip(
                $"{name}\n社区共享雷点（只读）\n"
                + $"({marker.X:F2}, {marker.Y:F2}, {marker.Z:F2})\n"
                + $"机制半径: {mechanicRadiusYalms:F1}"
            );
        }
    }

    private void DrawMarker(
        ImDrawListPtr drawList,
        DevMapMarker marker,
        Vector2 center,
        float uiScale,
        bool editable = true,
        bool shared = false,
        string? readOnlySource = null
    )
    {
        if (marker.Type == DevMarkerType.Monster)
        {
            DrawMonsterMarker(drawList, marker, center, uiScale, shared);
            return;
        }

        var size = Math.Clamp(26f * uiScale, 20f, 40f);
        if (marker.Type is DevMarkerType.FortuneCarrot or DevMarkerType.FortuneCarrotChest)
        {
            size *= 0.72f;
        }

        var half = new Vector2(size / 2f);
        var min = center - half;
        var max = center + half;

        var icon = Svc.Texture.GetFromGameIcon(new GameIconLookup(iconIds[marker.Type])).GetWrapOrEmpty();
        if (marker.Type is DevMarkerType.Fate or DevMarkerType.CriticalEncounter)
        {
            var color = GetColor(marker.Type);
            drawList.AddCircleFilled(
                center,
                size * 0.55f,
                ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.72f))
            );
            if (icon.Handle != nint.Zero)
            {
                drawList.AddImage(
                    icon.Handle,
                    min,
                    max,
                    Vector2.Zero,
                    Vector2.One,
                    ImGui.ColorConvertFloat4ToU32(color)
                );
            }

            var badge = GetBadge(marker.Type);
            var textSize = ImGui.CalcTextSize(badge);
            var textPosition = center - textSize / 2f;
            drawList.AddText(textPosition + Vector2.One, 0xFF000000, badge);
            drawList.AddText(textPosition, 0xFFFFFFFF, badge);
        }
        else if (icon.Handle != nint.Zero)
        {
            // Match Eureka Linker's Occult markers: use the un-tinted game icon
            // directly, without BOCCHI's old colored halo or text badge.
            drawList.AddImage(icon.Handle, min, max, Vector2.Zero, Vector2.One, 0xFFFFFFFF);
        }

        if (!ImGui.IsMouseHoveringRect(min, max, false))
        {
            return;
        }

        drawList.AddRect(min - Vector2.One, max + Vector2.One, 0xFFFFFFFF, 2f, ImDrawFlags.None, 2f);
        var markerName = string.IsNullOrWhiteSpace(marker.Name)
            ? GetLabel(marker.Type)
            : marker.Name;
        var eventId = marker.EventId > 0 ? $"\nEventId: {marker.EventId}" : "";
        var sourceLabel = readOnlySource
                          ?? (shared
                              ? "社区共享标记（只读）"
                              : "只读自动采集标记");
        ImGui.SetTooltip(
            $"{markerName}{eventId}\n"
            + $"({marker.X:F2}, {marker.Y:F2}, {marker.Z:F2})\n"
            + sourceLabel
        );

    }

    private static void DrawMonsterMarker(
        ImDrawListPtr drawList,
        DevMapMarker marker,
        Vector2 center,
        float uiScale,
        bool shared
    )
    {
        var label = marker.Level > 0
            ? $"{marker.Level} {marker.Name}"
            : marker.Name;
        if (string.IsNullOrWhiteSpace(label))
        {
            label = "怪物";
        }

        var font = ImGui.GetFont();
        var fontSize = Math.Clamp(ImGui.GetFontSize() * 0.72f * uiScale, 9f, 14f);
        var textSize = ImGui.CalcTextSize(label) * (fontSize / ImGui.GetFontSize());
        var textPosition = center - textSize / 2f;
        drawList.AddText(font, fontSize, textPosition + Vector2.One, 0xE0000000, label);
        drawList.AddText(font, fontSize, textPosition, 0xFFE8F2D0, label);

        var padding = new Vector2(3f * uiScale, 2f * uiScale);
        var min = textPosition - padding;
        var max = textPosition + textSize + padding;
        if (!ImGui.IsMouseHoveringRect(min, max, false))
        {
            return;
        }

        drawList.AddRect(min, max, 0xFFFFFFFF, 2f, ImDrawFlags.None, 1f);
        ImGui.SetTooltip(
            $"{label}\nBaseId: {marker.BaseId}\n"
            + $"({marker.X:F2}, {marker.Y:F2}, {marker.Z:F2})\n"
            + (shared ? "社区共享怪物点（只读）" : "本地记录的静止未交战怪物")
        );
    }

    private IReadOnlyList<MonsterMapCluster> GetMonsterClusters(
        IReadOnlyCollection<MonsterMapMarker> markers,
        uint territoryId,
        uint mapId,
        float screenScale,
        float uiScale,
        float mapZoom
    )
    {
        var hash = new HashCode();
        hash.Add(markers.Count);
        foreach (var entry in markers
                     .OrderBy(entry => entry.Shared)
                     .ThenBy(entry => entry.Marker.BaseId)
                     .ThenBy(entry => entry.Marker.X)
                     .ThenBy(entry => entry.Marker.Z)
                     .ThenBy(entry => entry.Marker.Level)
                     .ThenBy(entry => entry.Marker.Name, StringComparer.Ordinal))
        {
            hash.Add(entry.Marker.BaseId);
            hash.Add(entry.Marker.Level);
            hash.Add(entry.Marker.Name, StringComparer.Ordinal);
            hash.Add(entry.Marker.X);
            hash.Add(entry.Marker.Y);
            hash.Add(entry.Marker.Z);
            hash.Add(entry.Shared);
        }

        var collisionOnly = mapZoom >= MonsterCollisionOnlyZoom;
        hash.Add(collisionOnly);
        if (collisionOnly)
        {
            // Panning translates every rectangle equally, so only the scale and
            // font metrics affect screen-space collision clustering.
            hash.Add(MathF.Round(screenScale, 5));
            hash.Add(MathF.Round(uiScale, 3));
            hash.Add(MathF.Round(mapZoom, 3));
        }

        var fingerprint = hash.ToHashCode();
        if (cachedMonsterClusterTerritoryId == territoryId
            && cachedMonsterClusterMapId == mapId
            && cachedMonsterClusterFingerprint == fingerprint)
        {
            return cachedMonsterClusters;
        }

        var clusters = new List<MonsterMapCluster>();
        foreach (var marker in markers
                      .OrderBy(entry => entry.Marker.X)
                      .ThenBy(entry => entry.Marker.Z)
                      .ThenBy(entry => entry.Marker.BaseId))
        {
            MonsterMapCluster? bestCluster = null;
            var bestDistance = float.MaxValue;
            foreach (var cluster in clusters)
            {
                var mergedMembers = cluster.Members.Append(marker).ToList();
                if (mergedMembers
                        .Select(member => (member.Marker.Level, member.Marker.Name))
                        .Distinct()
                        .Count() > MonsterLabelsPerCluster)
                {
                    continue;
                }

                float distance;
                if (collisionOnly)
                {
                    if (!ClusterHasLabelCollision(
                            cluster,
                            marker,
                            screenScale,
                            uiScale,
                            mapZoom
                        ))
                    {
                        continue;
                    }

                    distance = Vector2.Distance(
                        new Vector2(cluster.Center.X, cluster.Center.Z) * screenScale,
                        new Vector2(marker.Marker.X, marker.Marker.Z) * screenScale
                    );
                }
                else
                {
                    var mergedCenter = MonsterMapCluster.CalculateCenter(mergedMembers);
                    if (mergedMembers.Any(member =>
                            Vector2.Distance(
                                new Vector2(member.Marker.X, member.Marker.Z),
                                new Vector2(mergedCenter.X, mergedCenter.Z)
                            ) > MonsterDisplayClusterRadius
                        ))
                    {
                        continue;
                    }

                    distance = Vector2.Distance(
                        new Vector2(cluster.Center.X, cluster.Center.Z),
                        new Vector2(marker.Marker.X, marker.Marker.Z)
                    );
                }

                if (distance < bestDistance)
                {
                    bestCluster = cluster;
                    bestDistance = distance;
                }
            }

            if (bestCluster == null)
            {
                clusters.Add(new MonsterMapCluster([marker]));
                continue;
            }

            var index = clusters.IndexOf(bestCluster);
            clusters[index] = new MonsterMapCluster(bestCluster.Members.Append(marker).ToList());
        }

        cachedMonsterClusterTerritoryId = territoryId;
        cachedMonsterClusterMapId = mapId;
        cachedMonsterClusterFingerprint = fingerprint;
        cachedMonsterClusters = clusters;
        return cachedMonsterClusters;
    }

    private static bool ClusterHasLabelCollision(
        MonsterMapCluster cluster,
        MonsterMapMarker candidate,
        float screenScale,
        float uiScale,
        float mapZoom
    )
    {
        var candidateBounds = GetMonsterLabelBounds(
            candidate,
            screenScale,
            uiScale,
            mapZoom
        );
        return cluster.Members.Any(member =>
        {
            var memberBounds = GetMonsterLabelBounds(
                member,
                screenScale,
                uiScale,
                mapZoom
            );
            return candidateBounds.Min.X <= memberBounds.Max.X
                   && candidateBounds.Max.X >= memberBounds.Min.X
                   && candidateBounds.Min.Y <= memberBounds.Max.Y
                   && candidateBounds.Max.Y >= memberBounds.Min.Y;
        });
    }

    private static MonsterLabelBounds GetMonsterLabelBounds(
        MonsterMapMarker member,
        float screenScale,
        float uiScale,
        float mapZoom
    )
    {
        var label = GetMonsterLabel(member.Marker);
        var fontSize = GetMonsterFontSize(uiScale, mapZoom);
        var textSize = ImGui.CalcTextSize(label) * (fontSize / ImGui.GetFontSize());
        var point = new Vector2(member.Marker.X, member.Marker.Z) * screenScale;
        var origin = point + new Vector2(4f * uiScale);
        var padding = new Vector2(
            MonsterLabelCollisionGap * uiScale,
            Math.Max(1.5f, 2f * uiScale)
        );
        return new MonsterLabelBounds(origin - padding, origin + textSize + padding);
    }

    private static string GetMonsterLabel(DevMapMarker marker)
    {
        var name = string.IsNullOrWhiteSpace(marker.Name) ? "怪物" : marker.Name;
        return marker.Level > 0 ? $"{marker.Level} {name}" : name;
    }

    private static float GetMonsterFontSize(float uiScale, float mapZoom)
    {
        return Math.Clamp(
            ImGui.GetFontSize() * 0.68f * uiScale * mapZoom,
            8f,
            26f
        );
    }

    private static string? DrawMonsterCluster(
        ImDrawListPtr drawList,
        MonsterMapCluster cluster,
        Vector2 center,
        float uiScale,
        float mapZoom
    )
    {
        var labels = cluster.Members
            .Select(member => GetMonsterLabel(member.Marker))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(label => label, StringComparer.Ordinal)
            .ToList();
        if (labels.Count == 0)
        {
            labels.Add("怪物");
        }

        var font = ImGui.GetFont();
        var fontSize = GetMonsterFontSize(uiScale, mapZoom);
        var lineHeight = fontSize + Math.Max(1.5f, 2f * uiScale);
        var textSizes = labels
            .Select(label => ImGui.CalcTextSize(label) * (fontSize / ImGui.GetFontSize()))
            .ToList();
        var columnCount = (labels.Count + MonsterLabelsPerColumn - 1) / MonsterLabelsPerColumn;
        var columnGap = Math.Max(8f, 10f * uiScale);
        var columnWidths = Enumerable.Range(0, columnCount)
            .Select(column => Enumerable.Range(
                    column * MonsterLabelsPerColumn,
                    Math.Min(MonsterLabelsPerColumn, labels.Count - column * MonsterLabelsPerColumn)
                )
                .Max(index => textSizes[index].X))
            .ToList();
        var totalWidth = columnWidths.Sum() + columnGap * (columnCount - 1);
        var rowCount = Math.Min(MonsterLabelsPerColumn, labels.Count);
        var totalHeight = lineHeight * rowCount;
        // Keep the label block's upper-left anchor fixed to the map point.
        // Growing the font around the block center made the text visibly slide
        // whenever the native AreaMap zoom changed.
        var blockOrigin = center + new Vector2(4f * uiScale);
        var top = blockOrigin.Y;
        var columnLeft = blockOrigin.X;
        for (var index = 0; index < labels.Count; index++)
        {
            var column = index / MonsterLabelsPerColumn;
            var row = index % MonsterLabelsPerColumn;
            var textPosition = new Vector2(
                columnLeft,
                top + row * lineHeight
            );
            drawList.AddText(font, fontSize, textPosition + Vector2.One, 0xE0000000, labels[index]);
            drawList.AddText(font, fontSize, textPosition, 0xFFE8F2D0, labels[index]);
            if (row == MonsterLabelsPerColumn - 1)
            {
                columnLeft += columnWidths[column] + columnGap;
            }
        }

        var padding = new Vector2(3f * uiScale, 2f * uiScale);
        var min = blockOrigin - padding;
        var max = blockOrigin + new Vector2(totalWidth, totalHeight) + padding;
        if (!ImGui.IsMouseHoveringRect(min, max, false))
        {
            return null;
        }

        drawList.AddRect(min, max, 0xFFFFFFFF, 2f, ImDrawFlags.None, 1f);
        var source = cluster.Members.Any(member => member.Shared)
            ? "含社区共享统计（只读）"
            : "本地记录的静止未交战怪物";
        return $"{string.Join("\n", labels)}\n"
               + $"区域中心: ({cluster.Center.X:F2}, {cluster.Center.Y:F2}, {cluster.Center.Z:F2})\n"
               + source;
    }

    private static void DrawForegroundTooltip(
        ImDrawListPtr drawList,
        string text,
        float uiScale
    )
    {
        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize();
        var textSize = ImGui.CalcTextSize(text);
        var padding = new Vector2(8f, 6f) * uiScale;
        var boxSize = textSize + padding * 2f;
        var viewport = ImGui.GetMainViewport();
        var viewportMin = viewport.Pos + new Vector2(4f);
        var viewportMax = viewport.Pos + viewport.Size - boxSize - new Vector2(4f);
        var boxMin = Vector2.Clamp(
            ImGui.GetMousePos() + new Vector2(16f, 18f) * uiScale,
            viewportMin,
            Vector2.Max(viewportMin, viewportMax)
        );
        var boxMax = boxMin + boxSize;

        // The map overlay itself uses the foreground draw list. A regular
        // ImGui tooltip is therefore rendered underneath it; append this box
        // after every map primitive so the hovered monster details stay on top.
        drawList.AddRectFilled(boxMin, boxMax, 0xF51A1A1A, 4f * uiScale);
        drawList.AddRect(boxMin, boxMax, 0xFFD8D8D8, 4f * uiScale);
        drawList.AddText(font, fontSize, boxMin + padding, 0xFFFFFFFF, text);
    }

    private sealed record MonsterMapMarker(DevMapMarker Marker, bool Shared);

    private readonly record struct MonsterLabelBounds(Vector2 Min, Vector2 Max);

    private sealed class MonsterMapCluster
    {
        public List<MonsterMapMarker> Members { get; }

        public Vector3 Center { get; }

        public MonsterMapCluster(List<MonsterMapMarker> members)
        {
            Members = members;
            Center = CalculateCenter(members);
        }

        public static Vector3 CalculateCenter(IReadOnlyCollection<MonsterMapMarker> members)
        {
            return new Vector3(
                members.Average(member => member.Marker.X),
                members.Average(member => member.Marker.Y),
                members.Average(member => member.Marker.Z)
            );
        }
    }

    private void DrawMarkerEditor()
    {
        if (!editorOpen || pendingEdit == null)
        {
            return;
        }

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(
            viewport.Pos + viewport.Size / 2f,
            ImGuiCond.Appearing,
            new Vector2(0.5f, 0.5f)
        );

        var windowOpen = editorOpen;
        if (ImGui.Begin(
                EditWindowId,
                ref windowOpen,
                ImGuiWindowFlags.AlwaysAutoResize
                | ImGuiWindowFlags.NoCollapse
                | ImGuiWindowFlags.NoSavedSettings
            ))
        {
            ImGui.TextUnformatted($"当前类型：{GetLabel(pendingEdit.Type)}");
            ImGui.TextUnformatted($"坐标：({pendingEdit.X:F2}, {pendingEdit.Y:F2}, {pendingEdit.Z:F2})");
            ImGui.Separator();
            ImGui.TextUnformatted("修改类型：");

            for (var i = 0; i < EditableTypes.Length; i++)
            {
                var type = EditableTypes[i];
                if (i > 0 && i % 4 != 0)
                {
                    ImGui.SameLine();
                }

                if (ImGui.Button($"{GetLabel(type)}##DevMap_Edit_{type}"))
                {
                    ChangeMarkerType(pendingEdit, type);
                }
            }

            ImGui.Separator();
            if (!deleteConfirmationRequested)
            {
                if (ImGui.Button("删除标注"))
                {
                    deleteConfirmationRequested = true;
                }
            }
            else
            {
                ImGui.TextColored(
                    new Vector4(1f, 0.35f, 0.35f, 1f),
                    "确定删除这个标注吗？此操作会立即写入 JSON。"
                );
                if (ImGui.Button("确认删除"))
                {
                    DeleteMarker(pendingEdit);
                }

                ImGui.SameLine();
                if (ImGui.Button("取消删除"))
                {
                    deleteConfirmationRequested = false;
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("关闭"))
            {
                CloseMarkerEditor();
            }
        }

        ImGui.End();

        if (!windowOpen)
        {
            CloseMarkerEditor();
        }
    }

    private void DrawTowerTrapGroupEditor()
    {
        if (!towerTrapEditorOpen || pendingTowerTrapEdit == null)
        {
            return;
        }

        var record = pendingTowerTrapEdit;
        var currentGroup = forkedTowerEventObjFile.TrapGroups.FirstOrDefault(group =>
            group.CandidateIds.Contains(record.Id)
        );
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(
            viewport.Pos + viewport.Size / 2f,
            ImGuiCond.Appearing,
            new Vector2(0.5f)
        );

        var windowOpen = towerTrapEditorOpen;
        if (ImGui.Begin(
                "雷候选点编组###BOCCHI_DevMap_TrapGroup",
                ref windowOpen,
                ImGuiWindowFlags.AlwaysAutoResize
                | ImGuiWindowFlags.NoCollapse
                | ImGuiWindowFlags.NoSavedSettings
            ))
        {
            var typeLabel = record.Type switch
            {
                ForkedTowerEventObjType.SmallTrap => "小雷",
                ForkedTowerEventObjType.BigTrap => "大雷",
                _ => "未知/非雷",
            };
            ImGui.TextUnformatted($"类型：{typeLabel}");
            ImGui.TextUnformatted($"BaseId：{record.BaseId}");
            ImGui.TextUnformatted(
                $"坐标：({record.X:F2}, {record.Y:F2}, {record.Z:F2})"
            );
            ImGui.TextUnformatted($"累计观察塔次：{record.ObservedRunIds.Count}");
            ImGui.Separator();
            ImGui.TextUnformatted("将此 Territory 内相同 BaseId 批量识别为：");
            if (ImGui.Button("小雷（7）##DevMap_ClassifySmallTrap"))
            {
                ChangeTowerBaseIdType(record, ForkedTowerEventObjType.SmallTrap);
            }

            ImGui.SameLine();
            if (ImGui.Button("大雷（30）##DevMap_ClassifyBigTrap"))
            {
                ChangeTowerBaseIdType(record, ForkedTowerEventObjType.BigTrap);
            }

            ImGui.SameLine();
            if (ImGui.Button("未知/非雷##DevMap_ClassifyUnknown"))
            {
                ChangeTowerBaseIdType(record, ForkedTowerEventObjType.Unknown);
            }

            ImGui.Separator();

            if (record.Type == ForkedTowerEventObjType.Unknown)
            {
                ImGui.TextDisabled("先将此 BaseId 识别为小雷或大雷，之后才能编组。");
            }
            else if (currentGroup != null)
            {
                ImGui.TextUnformatted(
                    $"当前编组：{currentGroup.Name}（{currentGroup.CandidateIds.Count} 个候选点）"
                );
                var maxActive = currentGroup.MaxActive;
                if (ImGui.InputInt("组内最多同时出现", ref maxActive))
                {
                    var previous = currentGroup.MaxActive;
                    currentGroup.MaxActive = Math.Clamp(
                        maxActive,
                        1,
                        Math.Max(1, currentGroup.CandidateIds.Count)
                    );
                    if (!SaveForkedTowerEventObjects())
                    {
                        currentGroup.MaxActive = previous;
                    }
                }

                if (ImGui.Button("移出当前编组"))
                {
                    RemoveTrapFromGroup(record);
                    currentGroup = null;
                }
            }
            else
            {
                ImGui.TextDisabled("当前未编组；未编组点不会排除其他候选点。");
            }

            var availableGroups = forkedTowerEventObjFile.TrapGroups
                .Where(group =>
                    record.Type != ForkedTowerEventObjType.Unknown
                    &&
                    group.TerritoryId == record.TerritoryId
                    && group.MapId == record.MapId
                    && group.Id != currentGroup?.Id
                )
                .OrderBy(group => group.Name)
                .ToList();
            if (availableGroups.Count > 0
                && ImGui.BeginCombo("加入已有候选组", "选择编组"))
            {
                foreach (var group in availableGroups)
                {
                    if (ImGui.Selectable(
                            $"{group.Name}（{group.CandidateIds.Count} 点，最多 {group.MaxActive}）"
                        ))
                    {
                        AssignTrapToGroup(record, group);
                    }
                }

                ImGui.EndCombo();
            }

            if (record.Type != ForkedTowerEventObjType.Unknown
                && ImGui.Button("新建候选组并加入（默认互斥）"))
            {
                CreateTrapGroup(record);
            }

            ImGui.SameLine();
            if (ImGui.Button("关闭"))
            {
                CloseTowerTrapGroupEditor();
            }
        }

        ImGui.End();
        if (!windowOpen)
        {
            CloseTowerTrapGroupEditor();
        }
    }

    private void CreateTrapGroup(ForkedTowerEventObjRecord record)
    {
        var previousGroups = CloneTrapGroups();
        foreach (var group in forkedTowerEventObjFile.TrapGroups)
        {
            group.CandidateIds.Remove(record.Id);
        }

        var groupNumber = 1;
        var existingNames = forkedTowerEventObjFile.TrapGroups
            .Where(group =>
                group.TerritoryId == record.TerritoryId && group.MapId == record.MapId
            )
            .Select(group => group.Name)
            .ToHashSet();
        while (existingNames.Contains($"G{groupNumber}"))
        {
            groupNumber++;
        }

        NormalizeTrapGroups();
        forkedTowerEventObjFile.TrapGroups.Add(new ForkedTowerTrapGroupDefinition
        {
            Name = $"G{groupNumber}",
            TerritoryId = record.TerritoryId,
            MapId = record.MapId,
            MaxActive = 1,
            CandidateIds = [record.Id],
        });
        NormalizeTrapGroups();

        if (!SaveForkedTowerEventObjects())
        {
            forkedTowerEventObjFile.TrapGroups = previousGroups;
            return;
        }

        lastError = null;
    }

    private void ChangeTowerBaseIdType(
        ForkedTowerEventObjRecord source,
        ForkedTowerEventObjType type
    )
    {
        var previousRecords = forkedTowerEventObjFile.EventObjects
            .Select(CloneForkedTowerEventObject)
            .ToList();
        var previousGroups = CloneTrapGroups();
        var matchingRecords = forkedTowerEventObjFile.EventObjects
            .Where(record =>
                record.TerritoryId == source.TerritoryId
                && record.BaseId == source.BaseId
            )
            .ToList();
        var mechanicRadius = type switch
        {
            ForkedTowerEventObjType.SmallTrap => 7f,
            ForkedTowerEventObjType.BigTrap => 30f,
            _ => (float?)null,
        };
        foreach (var record in matchingRecords)
        {
            record.Type = type;
            record.MechanicRadius = mechanicRadius;
        }

        if (type == ForkedTowerEventObjType.Unknown)
        {
            var affectedIds = matchingRecords.Select(record => record.Id).ToHashSet();
            foreach (var group in forkedTowerEventObjFile.TrapGroups)
            {
                group.CandidateIds.RemoveAll(affectedIds.Contains);
            }

            NormalizeTrapGroups();
        }

        if (!SaveForkedTowerEventObjects())
        {
            forkedTowerEventObjFile.EventObjects = previousRecords;
            forkedTowerEventObjFile.TrapGroups = previousGroups;
            pendingTowerTrapEdit = previousRecords.FirstOrDefault(record =>
                record.Id == source.Id
            );
            return;
        }

        lastError = null;
    }

    private void AssignTrapToGroup(
        ForkedTowerEventObjRecord record,
        ForkedTowerTrapGroupDefinition target
    )
    {
        var previousGroups = CloneTrapGroups();
        foreach (var group in forkedTowerEventObjFile.TrapGroups)
        {
            group.CandidateIds.Remove(record.Id);
        }

        if (!target.CandidateIds.Contains(record.Id))
        {
            target.CandidateIds.Add(record.Id);
        }

        NormalizeTrapGroups();
        if (!SaveForkedTowerEventObjects())
        {
            forkedTowerEventObjFile.TrapGroups = previousGroups;
            return;
        }

        lastError = null;
    }

    private void RemoveTrapFromGroup(ForkedTowerEventObjRecord record)
    {
        var previousGroups = CloneTrapGroups();
        foreach (var group in forkedTowerEventObjFile.TrapGroups)
        {
            group.CandidateIds.Remove(record.Id);
        }

        NormalizeTrapGroups();
        if (!SaveForkedTowerEventObjects())
        {
            forkedTowerEventObjFile.TrapGroups = previousGroups;
            return;
        }

        lastError = null;
    }

    private List<ForkedTowerTrapGroupDefinition> CloneTrapGroups()
    {
        return forkedTowerEventObjFile.TrapGroups
            .Select(group => new ForkedTowerTrapGroupDefinition
            {
                Id = group.Id,
                Name = group.Name,
                TerritoryId = group.TerritoryId,
                MapId = group.MapId,
                MaxActive = group.MaxActive,
                CandidateIds = [..group.CandidateIds],
            })
            .ToList();
    }

    private void CloseTowerTrapGroupEditor()
    {
        pendingTowerTrapEdit = null;
        towerTrapEditorOpen = false;
    }

    private void ChangeMarkerType(DevMapMarker marker, DevMarkerType type)
    {
        if (marker.Type == type)
        {
            return;
        }

        var previousType = marker.Type;
        var previousEventId = marker.EventId;
        var previousName = marker.Name;
        marker.Type = type;
        if (type is DevMarkerType.Fate or DevMarkerType.CriticalEncounter)
        {
            if (previousType != type)
            {
                marker.EventId = 0;
                marker.Name = "";
                BackfillEventMarker(marker);
            }
        }
        else
        {
            marker.EventId = 0;
            marker.BaseId = 0;
            marker.Level = 0;
            marker.Name = "";
        }

        if (!SaveMarkers())
        {
            marker.Type = previousType;
            marker.EventId = previousEventId;
            marker.Name = previousName;
            return;
        }

        lastError = null;
        deleteConfirmationRequested = false;
        Svc.Chat.Print($"[BOCCHI] 地图标注已修改为“{GetLabel(type)}”。");
    }

    private void DeleteMarker(DevMapMarker marker)
    {
        var index = markerFile.Markers.FindIndex(candidate => candidate.Id == marker.Id);
        if (index < 0)
        {
            CloseMarkerEditor();
            return;
        }

        markerFile.Markers.RemoveAt(index);
        if (!SaveMarkers())
        {
            markerFile.Markers.Insert(index, marker);
            return;
        }

        CloseMarkerEditor();
        lastError = null;
        Svc.Chat.Print("[BOCCHI] 地图标注已删除。");
    }

    private void CloseMarkerEditor()
    {
        pendingEdit = null;
        editorOpen = false;
        deleteConfirmationRequested = false;
    }

    private static string GetLabel(DevMarkerType type)
    {
        return type switch
        {
            DevMarkerType.SilverChest => "银宝箱",
            DevMarkerType.BronzeChest => "铜宝箱",
            DevMarkerType.FortuneCarrot => "好运胡萝卜",
            DevMarkerType.FortuneCarrotChest => "好运胡萝卜",
            DevMarkerType.PotChest => "罐子宝箱",
            DevMarkerType.RerollChest => "重抽宝箱",
            DevMarkerType.Fate => "FATE",
            DevMarkerType.CriticalEncounter => "CE",
            DevMarkerType.InvestigationLocation => "调查地点",
            DevMarkerType.UnknownChest => "未识别宝箱",
            DevMarkerType.Monster => "静止未交战怪物",
            _ => type.ToString(),
        };
    }

    private static string GetBadge(DevMarkerType type)
    {
        return type switch
        {
            DevMarkerType.SilverChest => "银",
            DevMarkerType.BronzeChest => "铜",
            DevMarkerType.FortuneCarrot => "胡",
            DevMarkerType.FortuneCarrotChest => "胡",
            DevMarkerType.PotChest => "罐",
            DevMarkerType.RerollChest => "重",
            DevMarkerType.Fate => "F",
            DevMarkerType.CriticalEncounter => "CE",
            DevMarkerType.InvestigationLocation => "查",
            DevMarkerType.UnknownChest => "箱",
            DevMarkerType.Monster => "怪",
            _ => "?",
        };
    }

    private static Vector4 GetColor(DevMarkerType type)
    {
        return type switch
        {
            DevMarkerType.SilverChest => new Vector4(0.9f, 0.95f, 1f, 1f),
            DevMarkerType.BronzeChest => new Vector4(0.9f, 0.55f, 0.24f, 1f),
            DevMarkerType.FortuneCarrot => new Vector4(0.62f, 1f, 0.4f, 1f),
            DevMarkerType.FortuneCarrotChest => new Vector4(0.62f, 1f, 0.4f, 1f),
            DevMarkerType.PotChest => new Vector4(1f, 0.72f, 0.35f, 1f),
            DevMarkerType.RerollChest => new Vector4(0.35f, 0.82f, 1f, 1f),
            DevMarkerType.Fate => new Vector4(1f, 0.82f, 0.2f, 1f),
            DevMarkerType.CriticalEncounter => new Vector4(0.82f, 0.4f, 1f, 1f),
            DevMarkerType.InvestigationLocation => new Vector4(0.25f, 0.9f, 1f, 1f),
            DevMarkerType.UnknownChest => new Vector4(0.72f, 0.5f, 0.9f, 1f),
            DevMarkerType.Monster => new Vector4(0.82f, 0.95f, 0.68f, 1f),
            _ => Vector4.One,
        };
    }

    private sealed class MonsterMovementTrack
    {
        public Vector3 Position { get; set; }

        public DateTime StableSince { get; set; }

        public DateTime LastSeenAt { get; set; }

        public bool RecordedAtStablePosition { get; set; }
    }

    public override void Dispose()
    {
        Svc.AddonLifecycle.UnregisterListener(
            AddonEvent.PostDraw,
            "AreaMap",
            OnAreaMapAvailable
        );
        Svc.AddonLifecycle.UnregisterListener(
            AddonEvent.PreFinalize,
            "AreaMap",
            OnAreaMapFinalizing
        );
        Svc.PluginInterface.UiBuilder.Draw -= DrawDevMapUi;
        base.Dispose();
    }
}
