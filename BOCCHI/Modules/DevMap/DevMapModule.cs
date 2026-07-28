using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using BOCCHI.Data;
using BOCCHI.Data.Traps;
using BOCCHI.Enums;
using BOCCHI.Modules.ForkedTower;
using BOCCHI.Modules.Telemetry;
using BOCCHI.Modules.Treasure;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Textures;
using Dalamud.Bindings.ImGui;
using ECommons.DalamudServices;
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
    private const int VkRightMouseButton = 0x02;
    private const string MarkerFileName = "northern_expedition_markers.json";
    private const string ForkedTowerEventObjFileName = "forked_tower_eventobjs.json";
    private const string EditWindowId = "编辑地图标注###BOCCHI_DevMap_Edit";
    private const float ChestMergeDistance = 4f;
    private const float EventMergeDistance = 8f;
    private const int ExpectedBuiltInTrapGroupCount = 47;
    private static readonly TimeSpan AutoScanInterval = TimeSpan.FromMilliseconds(500);
    private static readonly DevMarkerType[] EditableTypes =
    [
        DevMarkerType.SilverChest,
        DevMarkerType.BronzeChest,
        DevMarkerType.FortuneCarrot,
        DevMarkerType.PotChest,
        DevMarkerType.Fate,
        DevMarkerType.CriticalEncounter,
        DevMarkerType.InvestigationLocation,
    ];
    private static readonly DevMarkerType[] MarkerFilterTypes =
    [
        DevMarkerType.BronzeChest,
        DevMarkerType.SilverChest,
        DevMarkerType.PotChest,
        DevMarkerType.FortuneCarrot,
        DevMarkerType.InvestigationLocation,
        DevMarkerType.Fate,
        DevMarkerType.CriticalEncounter,
        DevMarkerType.UnknownChest,
    ];

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

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
        [DevMarkerType.Fate] = 60502,
        [DevMarkerType.CriticalEncounter] = 63909,
        [DevMarkerType.InvestigationLocation] = 60474,
        [DevMarkerType.UnknownChest] = 60354,
    };

    private DevMapMarkerFile markerFile = new();
    private ForkedTowerEventObjFile forkedTowerEventObjFile = new();
    private DevMapMarker? pendingEdit;
    private ForkedTowerEventObjRecord? pendingTowerTrapEdit;
    private bool editorOpen;
    private bool towerTrapEditorOpen;
    private bool deleteConfirmationRequested;
    private bool rightMouseWasDown;
    private bool rightMousePressed;
    private bool warnedUnexpectedBuiltInTrapGroupCount;
    private DateTime nextAutoScanAt = DateTime.MinValue;
    private string? lastError;

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
        LoadMarkers();
        LoadForkedTowerEventObjects();
        Svc.PluginInterface.UiBuilder.Draw += DrawDevMapUi;
    }

    public IReadOnlyList<DevMapMarker> GetTelemetryMarkersSnapshot()
    {
        return markerFile.Markers.Select(CloneMarker).ToList();
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
            + "右键已采集的雷候选点可新建或加入互斥组。"
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

        ImGui.TextWrapped("附近出现的银/铜宝箱、好运胡萝卜、FATE 和 CE 会自动记录。胡萝卜生成的宝箱会与胡萝卜坐标合并，不会重复标注；未识别的箱子可在地图上右键改为罐子宝箱。");
        DrawMarkerButton("设置当前位置为调查地点", DevMarkerType.InvestigationLocation);

        var count = markerFile.Markers.Count(m => m.TerritoryId == territoryId);
        ImGui.TextDisabled($"本区域已保存 {count} 个标注。右键大地图上的标注可修改类型或删除；删除需二次确认。");
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
        if (!PluginConfig.DevModeEnabled
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
                _ => DevMarkerType.UnknownChest,
            };

            if (RecordDetectedMarker(
                    markerType,
                    treasure.GetPosition(),
                    territoryId,
                    mapId
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
        var summary = string.Join("、", recorded
            .GroupBy(label => label)
            .Select(group => group.Count() == 1 ? group.Key : $"{group.Key}×{group.Count()}"));
        Svc.Chat.Print($"[BOCCHI] dev 自动记录：{summary}");
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

                if (existing.Type == ForkedTowerEventObjType.Unknown
                    && type != ForkedTowerEventObjType.Unknown)
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
        if (recordedBaseIds.Count > 0)
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
        (ForkedTowerEventObjType Type, float? MechanicRadius) known =
            territoryId == ZoneData.SOUTHHORN
                ? baseId switch
                {
                    (uint)OccultObjectType.Trap =>
                        (ForkedTowerEventObjType.SmallTrap, 7f),
                    (uint)OccultObjectType.BigTrap =>
                        (ForkedTowerEventObjType.BigTrap, 30f),
                    _ => (ForkedTowerEventObjType.Unknown, null),
                }
                : (ForkedTowerEventObjType.Unknown, null);
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
        string? name = null
    )
    {
        if (!IsValidPosition(position))
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
                    return true;
                }

                return false;
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
            AddCurrentPosition(type);
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

            var changed = forkedTowerEventObjFile.Version < 2;
            forkedTowerEventObjFile.Version = 2;
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
            }

            changed |= NormalizeTrapGroups();

            if (changed)
            {
                if (sourceVersion < 2)
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
                if (sourceVersion < 3)
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
        var changed = markerFile.Version < 3;
        markerFile.Version = 3;

        foreach (var marker in markerFile.Markers)
        {
            marker.Name ??= "";

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
            var mergeDistance = marker.Type is DevMarkerType.Fate
                or DevMarkerType.CriticalEncounter
                or DevMarkerType.InvestigationLocation
                ? EventMergeDistance
                : ChestMergeDistance;
            var duplicate = kept.FirstOrDefault(existing =>
                existing.TerritoryId == marker.TerritoryId
                && existing.MapId == marker.MapId
                && AreMergeableMarkerTypes(existing.Type, marker.Type)
                && (marker.Type is not (DevMarkerType.Fate or DevMarkerType.CriticalEncounter)
                    || (marker.EventId != 0 && existing.EventId == marker.EventId)
                    || (marker.EventId == 0 && existing.EventId == 0))
                && HorizontalDistance(existing.Position, marker.Position) <= mergeDistance
            );

            if (duplicate != null)
            {
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
        GetModule<ForkedTowerModule>().EnsureRunLifecycle();

        // Ocelot's normal update loop is deliberately limited to South Horn.
        // Running this throttled scan from UiBuilder keeps dev collection active
        // in a force-bound North territory without enabling every South module.
        AutoScan();

        var rightMouseDown = (GetAsyncKeyState(VkRightMouseButton) & 0x8000) != 0;
        rightMousePressed = rightMouseDown && !rightMouseWasDown;
        rightMouseWasDown = rightMouseDown;

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
        DrawMarkerEditor();
        DrawTowerTrapGroupEditor();
        ImGui.End();

        DrawMarkerFilterOverlay();
    }

    private unsafe void DrawAreaMapOverlay()
    {
        var sharedMarkers = GetSharedMarkers();
        var hasSharedDrawableMarkers = sharedMarkers.Any(IsSharedDrawableMarker);
        if (markerFile.Markers.Count == 0
            && !hasSharedDrawableMarkers
            && (!PluginConfig.DevModeEnabled
                || !PluginConfig.ShowForkedTowerEventObjectsOnMap
                || forkedTowerEventObjFile.EventObjects.Count == 0))
        {
            return;
        }

        var addonAddress = Svc.GameGui.GetAddonByName("AreaMap");
        if (addonAddress == nint.Zero)
        {
            return;
        }

        var addon = (AddonAreaMap*)addonAddress.Address;
        if (addon == null || !addon->AtkUnitBase.IsVisible)
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
        var sheetScale = mapRow.SizeFactor / 100f;
        var sheetOffset = new Vector2(mapRow.OffsetX, mapRow.OffsetY) * (sheetScale - 1f);
        var pan = new Vector2(
            addon->AreaMap.MapOffsetX + agentMap->SelectedOffsetX,
            addon->AreaMap.MapOffsetY + agentMap->SelectedOffsetY
        );

        var drawList = ImGui.GetForegroundDrawList();
        drawList.PushClipRect(clipMin, clipMax, true);

        foreach (var marker in markerFile.Markers.Where(m =>
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

            DrawMarker(drawList, marker, screenPosition, uiScale);
        }

        foreach (var sharedMarker in sharedMarkers.Where(marker =>
                     marker.TerritoryId == territoryId
                     && marker.MapId == mapId
                     && IsSharedDrawableMarker(marker)
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
                DrawMarker(
                    drawList,
                    new DevMapMarker
                    {
                        Type = markerType,
                        EventId = sharedMarker.EventId ?? 0,
                        Name = sharedMarker.Name ?? "",
                        TerritoryId = sharedMarker.TerritoryId,
                        MapId = sharedMarker.MapId,
                        X = sharedMarker.X,
                        Y = sharedMarker.Y,
                        Z = sharedMarker.Z,
                    },
                    screenPosition,
                    uiScale,
                    false,
                    true
                );
            }
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
                Svc.Log.Warning(
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
        var editHint = candidate.IsBuiltInGroup || candidate.SourceRecord == null
            ? ""
            : "\n右键设置互斥编组";
        ImGui.SetTooltip(
            $"{(candidate.Type == ForkedTowerEventObjType.BigTrap ? "大雷" : "小雷")} · {state}\n"
            + $"编组: {group}（{candidate.ObservedInGroup}/{candidate.MaxActive}）\n"
            + $"坐标: ({candidate.Position.X:F2}, {candidate.Position.Y:F2}, {candidate.Position.Z:F2})\n"
            + $"机制半径: {candidate.MechanicRadius:F1}\n"
            + $"累计观察塔次: {runCount}"
            + (hasConflict ? "\n警告：本次观察数超过编组上限" : "")
            + editHint
        );

        if (rightMousePressed
            && !candidate.IsBuiltInGroup
            && candidate.SourceRecord != null)
        {
            pendingTowerTrapEdit = candidate.SourceRecord;
            towerTrapEditorOpen = true;
        }
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
            + $"TowerRun: {record.TowerRunId}\n"
            + "右键识别为雷或设置编组"
        );
        if (rightMousePressed)
        {
            pendingTowerTrapEdit = record;
            towerTrapEditorOpen = true;
        }
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
            && !GetSharedMarkers().Any(marker => TryGetSharedDevMarkerType(marker, out _)))
        {
            return;
        }

        var addonAddress = Svc.GameGui.GetAddonByName("AreaMap");
        var agentMap = AgentMap.Instance();
        if (addonAddress == nint.Zero || agentMap == null)
        {
            return;
        }

        var addon = (AddonAreaMap*)addonAddress.Address;
        if (addon == null || !addon->AtkUnitBase.IsVisible)
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
        ImGui.SetNextWindowPos(
            new Vector2(addonAddress.X + 5f, addonAddress.Y - windowHeight),
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

    private void DrawMarkerFilterButton(DevMarkerType type)
    {
        var visibility = GetVisibilityFlag(type);
        var enabled = PluginConfig.DevMapVisibleMarkers.HasFlag(visibility);
        var activeColor = ImGuiColors.HealerGreen;
        var inactiveColor = ImGuiColors.ParsedGrey;
        var icon = Svc.Texture.GetFromGameIcon(new GameIconLookup(iconIds[type])).GetWrapOrEmpty();

        ImGui.PushID($"DevMapFilter_{type}");
        ImGui.PushStyleColor(ImGuiCol.Button, enabled ? activeColor : inactiveColor);
        var clicked = icon.Handle != nint.Zero
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
            DevMarkerType.Fate => DevMapMarkerVisibility.Fate,
            DevMarkerType.CriticalEncounter => DevMapMarkerVisibility.CriticalEncounter,
            DevMarkerType.InvestigationLocation =>
                DevMapMarkerVisibility.InvestigationLocation,
            DevMarkerType.UnknownChest => DevMapMarkerVisibility.UnknownChest,
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
        if (!string.Equals(marker.Source, "dev-map", StringComparison.OrdinalIgnoreCase)
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
            or DevMarkerType.Fate
            or DevMarkerType.CriticalEncounter
            or DevMarkerType.InvestigationLocation
            or DevMarkerType.UnknownChest;
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

        var mergeDistance = type is DevMarkerType.Fate
            or DevMarkerType.CriticalEncounter
            or DevMarkerType.InvestigationLocation
                ? EventMergeDistance
                : ChestMergeDistance;
        return markerFile.Markers.Any(local =>
            local.TerritoryId == sharedMarker.TerritoryId
            && local.MapId == sharedMarker.MapId
            && (local.Type == type || AreMergeableMarkerTypes(local.Type, type))
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
        bool shared = false
    )
    {
        var size = Math.Clamp(26f * uiScale, 20f, 40f);
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
        ImGui.SetTooltip(
            $"{markerName}{eventId}\n"
            + $"({marker.X:F2}, {marker.Y:F2}, {marker.Z:F2})\n右键编辑"
        );
        if (!editable && shared)
        {
            ImGui.SetTooltip(
                $"{markerName}{eventId}\n"
                + $"({marker.X:F2}, {marker.Y:F2}, {marker.Z:F2})\n"
                + "社区共享标记（只读）"
            );
        }

        if (editable && rightMousePressed)
        {
            pendingEdit = marker;
            editorOpen = true;
            deleteConfirmationRequested = false;
            Svc.Log.Information(
                "Dev map marker right-clicked: {Type} ({X}, {Y}, {Z})",
                marker.Type,
                marker.X,
                marker.Y,
                marker.Z
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
            DevMarkerType.Fate => "FATE",
            DevMarkerType.CriticalEncounter => "CE",
            DevMarkerType.InvestigationLocation => "调查地点",
            DevMarkerType.UnknownChest => "未识别宝箱",
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
            DevMarkerType.Fate => "F",
            DevMarkerType.CriticalEncounter => "CE",
            DevMarkerType.InvestigationLocation => "查",
            DevMarkerType.UnknownChest => "箱",
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
            DevMarkerType.Fate => new Vector4(1f, 0.82f, 0.2f, 1f),
            DevMarkerType.CriticalEncounter => new Vector4(0.82f, 0.4f, 1f, 1f),
            DevMarkerType.InvestigationLocation => new Vector4(0.25f, 0.9f, 1f, 1f),
            DevMarkerType.UnknownChest => new Vector4(0.72f, 0.5f, 0.9f, 1f),
            _ => Vector4.One,
        };
    }

    public override void Dispose()
    {
        Svc.PluginInterface.UiBuilder.Draw -= DrawDevMapUi;
        base.Dispose();
    }
}
