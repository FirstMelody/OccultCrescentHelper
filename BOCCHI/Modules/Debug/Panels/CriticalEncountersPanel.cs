using System;
using System.Linq;
using BOCCHI.Data;
using BOCCHI.Modules.CriticalEncounters;
using BOCCHI.Modules.Teleporter;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Dalamud.Bindings.ImGui;
using Ocelot.Ui;

namespace BOCCHI.Modules.Debug.Panels;

public class CriticalEncountersPanel : Panel
{
    public override string GetName()
    {
        return "紧急遭遇战";
    }

    public override unsafe void Render(DebugModule module)
    {
        OcelotUi.Title("紧急遭遇战：");
        OcelotUi.Indent(() =>
        {
            foreach (var data in EventData.CriticalEncounters.Values)
            {
                var ev = module.GetModule<CriticalEncountersModule>().CriticalEncounters[data.Id];

                ImGui.TextUnformatted(ev.Name.ToString());

                if (ev.State == DynamicEventState.Inactive)
                {
                    ImGui.SameLine();
                    ImGui.TextUnformatted("（未激活）");
                }

                if (ev.State == DynamicEventState.Register)
                {
                    var start = DateTimeOffset.FromUnixTimeSeconds(ev.StartTimestamp).DateTime;
                    var timeUntilStart = start - DateTime.UtcNow;
                    var formattedTime = $"{timeUntilStart.Minutes:D2}:{timeUntilStart.Seconds:D2}";

                    ImGui.SameLine();
                    ImGui.TextUnformatted($"（准备中：{formattedTime}）");
                }

                if (ev.State == DynamicEventState.Warmup)
                {
                    ImGui.SameLine();
                    ImGui.TextUnformatted("（开始中）");
                }

                if (ev.State == DynamicEventState.Battle)
                {
                    ImGui.SameLine();
                    ImGui.TextUnformatted($"({ev.Progress}%)");
                }

                if (module.TryGetModule<TeleporterModule>(out var teleporter) && teleporter!.IsReady())
                {
                    var start = ev.MapMarker.Position;

                    teleporter.teleporter.Button(data.Aethernet, start, ev.Name.ToString(), $"ce_{data.Id}", data);
                }

                OcelotUi.Indent(() => EventIconRenderer.Drops(data, module.PluginConfig.EventDropConfig));

                if (data.Id != EventData.CriticalEncounters.Keys.Max())
                {
                    OcelotUi.VSpace();
                }

                if (ImGui.CollapsingHeader($"事件数据##{data.Id}"))
                {
                    PrintEvent(ev);
                }

                if (ImGui.CollapsingHeader($"地图标记数据##{data.Id}"))
                {
                    PrintMapMarker(ev.MapMarker);
                }
            }
        });
    }

    private unsafe void PrintEvent(DynamicEvent ev)
    {
        OcelotUi.Title("名称偏移：");
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.NameOffset.ToString());

        OcelotUi.Title("描述偏移：");
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.DescriptionOffset.ToString());

        OcelotUi.Title("场景事件对象：");
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.LGBEventObject.ToString());

        OcelotUi.Title("场景地图范围：");
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.LGBMapRange.ToString());

        OcelotUi.Title("任务（行编号）：");
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.Quest.ToString());

        OcelotUi.Title("公告（行编号）：");
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.Announce.ToString());

        // OcelotUi.Title("Unknown0:");
        // ImGui.SameLine();
        // ImGui.TextUnformatted(ev.Unknown0.ToString());
        //
        // OcelotUi.Title("Unknown1:");
        // ImGui.SameLine();
        // ImGui.TextUnformatted(ev.Unknown1.ToString());
        //
        // OcelotUi.Title("Unknown6:");
        // ImGui.SameLine();
        // ImGui.TextUnformatted(ev.Unknown6.ToString());
        //
        // OcelotUi.Title("Unknown7:");
        // ImGui.SameLine();
        // ImGui.TextUnformatted(ev.Unknown7.ToString());
        //
        // OcelotUi.Title("Unknown2:");
        // ImGui.SameLine();
        // ImGui.TextUnformatted(ev.Unknown2.ToString());

        OcelotUi.Title("事件类型（行编号）：");
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.EventType.ToString());

        OcelotUi.Title("敌人类型（行编号）：");
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.EnemyType.ToString());

        OcelotUi.Title("最多参与人数：");
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.MaxParticipants.ToString());

        // OcelotUi.Title("Radius?:");
        // ImGui.SameLine();
        // ImGui.TextUnformatted(ev.Unknown4.ToString());

        // OcelotUi.Title("Unknown5:");
        // ImGui.SameLine();
        // ImGui.TextUnformatted(ev.Unknown5.ToString());

        OcelotUi.Title("单人战斗（行编号）：");
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.SingleBattle.ToString());

        // OcelotUi.Title("Unknown8:");
        // ImGui.SameLine();
        // ImGui.TextUnformatted(ev.Unknown8.ToString());

        OcelotUi.Title("开始时间戳：");
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.StartTimestamp.ToString());

        OcelotUi.Title("剩余秒数：");
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.SecondsLeft.ToString());

        OcelotUi.Title("持续秒数：");
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.SecondsDuration.ToString());

        OcelotUi.Title("动态事件编号：");
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.DynamicEventId.ToString());

        OcelotUi.Title("动态事件类型：");
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.DynamicEventType.ToString());

        OcelotUi.Title("状态：");
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.State switch
        {
            DynamicEventState.Inactive => "未激活",
            DynamicEventState.Register => "报名中",
            DynamicEventState.Warmup => "准备开始",
            DynamicEventState.Battle => "战斗中",
            _ => "未知",
        });

        OcelotUi.Title("参与人数：");
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.Participants.ToString());

        OcelotUi.Title("进度：");
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.Progress.ToString());

        OcelotUi.Title("名称：");
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.Name.ToString());

        OcelotUi.Title("描述：");
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.Description.ToString());

        OcelotUi.Title("目标图标 0：");
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.IconObjective0.ToString());

        OcelotUi.Title("最多参与人数 2：");
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.MaxParticipants2.ToString());

        OcelotUi.Title("地图标记：");
        ImGui.SameLine();
        ImGui.TextUnformatted(
            $"横坐标：{ev.MapMarker.Position.X}，纵坐标：{ev.MapMarker.Position.Y}，图标编号：{ev.MapMarker.IconId}");

        OcelotUi.Title("事件容器指针：");
        ImGui.SameLine();
        ImGui.TextUnformatted(((IntPtr)ev.EventContainer).ToString("X"));
    }


    private unsafe void PrintMapMarker(MapMarkerData marker)
    {
        OcelotUi.Title("层级编号：");
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.LevelId.ToString());

        OcelotUi.Title("目标编号：");
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.ObjectiveId.ToString());

        OcelotUi.Title("悬浮提示文本：");
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.TooltipString != null ? marker.TooltipString->ToString() : "空");

        OcelotUi.Title("图标编号：");
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.IconId.ToString());

        OcelotUi.Title("横坐标：");
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.Position.X.ToString("F2"));

        OcelotUi.Title("纵坐标：");
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.Position.Y.ToString("F2"));

        OcelotUi.Title("高度坐标：");
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.Position.Z.ToString("F2"));

        OcelotUi.Title("半径：");
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.Radius.ToString("F2"));

        OcelotUi.Title("地图编号：");
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.MapId.ToString());

        OcelotUi.Title("区域地名编号：");
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.PlaceNameZoneId.ToString());

        OcelotUi.Title("地名编号：");
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.PlaceNameId.ToString());

        OcelotUi.Title("结束时间戳：");
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.EndTimestamp.ToString());

        OcelotUi.Title("推荐等级：");
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.RecommendedLevel.ToString());

        OcelotUi.Title("区域类型编号：");
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.TerritoryTypeId.ToString());

        OcelotUi.Title("数据编号：");
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.DataId.ToString());

        OcelotUi.Title("标记类型：");
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.MarkerType.ToString());

        OcelotUi.Title("事件状态：");
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.EventState.ToString());

        OcelotUi.Title("标志位：");
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.Flags.ToString());
    }
}
