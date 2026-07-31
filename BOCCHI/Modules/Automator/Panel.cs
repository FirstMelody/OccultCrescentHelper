using System;
using System.Linq;
using BOCCHI.Data;
using Dalamud.Bindings.ImGui;
using ECommons.DalamudServices;
using Ocelot.Ui;

namespace BOCCHI.Modules.Automator;

public class Panel
{
    private string northernRouteName = "";
    private int northernRouteMenuOrder = 1;
    private int northernRouteDestinationId;
    private Guid? selectedNorthernRouteId;
    private Guid? pendingNorthernRouteDeleteId;

    public void Draw(AutomatorModule module)
    {
        OcelotUi.Title($"{module.T("panel.title")}:");
        OcelotUi.Indent(() =>
        {
            DrawEventControls(module);

            if (!module.Config.Enabled)
            {
                return;
            }

            ImGui.Separator();
            OcelotUi.Title($"{module.T("panel.activity.label")}:");
            try
            {
                var name = module.automator.Activity?.GetName() ?? module.T("panel.activity.none");
                ImGui.SameLine();
                ImGui.TextUnformatted(name);
            }
            catch (AccessViolationException)
            {
                return;
            }

            OcelotUi.Title($"{module.T("panel.activity_state.label")}:");
            ImGui.SameLine();
            ImGui.TextUnformatted(module.automator.Activity?.state.ToLabel() ?? module.T("panel.activity_state.none"));
        });
    }

    public void DrawEventControls(AutomatorModule module)
    {
        var illegalEnabled = module.Config.Enabled;
        if (ImGui.Checkbox("直接启用 Illegal Mode##IllegalModeDirect", ref illegalEnabled))
        {
            if (illegalEnabled)
            {
                module.EnableIllegalMode();
            }
            else
            {
                module.DisableIllegalMode();
            }
        }

        var toggleAiProvider = module.Config.ToggleAiProvider;
        if (ImGui.Checkbox(
                "自动切换 BossMod AI（默认关闭）##IllegalModeToggleAI",
                ref toggleAiProvider
            ))
        {
            module.Config.ToggleAiProvider = toggleAiProvider;
            module.PluginConfig.Save();
        }

        var doCriticalEncounters = module.Config.DoCriticalEncounters;
        if (ImGui.Checkbox("参加 CE##IllegalModeCE", ref doCriticalEncounters))
        {
            module.Config.DoCriticalEncounters = doCriticalEncounters;
            module.PluginConfig.Save();
        }

        ImGui.SameLine();
        DrawActiveNames(module.ActiveCriticalEncounterNames.Values.ToArray());
        DrawRecordedCriticalEncounters(module);

        var doFates = module.Config.DoFates;
        if (ImGui.Checkbox("参加 FATE##IllegalModeFATE", ref doFates))
        {
            module.Config.DoFates = doFates;
            module.PluginConfig.Save();
        }

        ImGui.SameLine();
        DrawActiveNames(module.ActiveFateNames.Values.ToArray());
        DrawRecordedFates(module);

        DrawNorthernRouteControls(module);
    }

    public void DrawNorthernRouteControls(AutomatorModule module)
    {
        if (!ZoneData.IsInNorthernExpedition()
            || !ImGui.TreeNode("北岛魔路##IllegalNorthernRoutes"))
        {
            return;
        }

        var useRoutes = module.Config.UseNorthernAethernetRoutes;
        if (ImGui.Checkbox("按 vnav 实际路程选择直走或魔路传送", ref useRoutes))
        {
            module.Config.UseNorthernAethernetRoutes = useRoutes;
            module.PluginConfig.Save();
        }

        var returnToStandby = module.Config.ReturnToNorthernStandby;
        if (ImGui.Checkbox("CE/FATE 结束后立即返回出生大水晶", ref returnToStandby))
        {
            module.Config.ReturnToNorthernStandby = returnToStandby;
            module.PluginConfig.Save();
        }

        var teleportPenalty = module.Config.NorthernTeleportPenalty;
        if (ImGui.SliderFloat(
                "传送等效距离惩罚",
                ref teleportPenalty,
                0f,
                300f,
                "%.0f"
            ))
        {
            module.Config.NorthernTeleportPenalty = teleportPenalty;
            module.PluginConfig.Save();
        }

        ImGui.Separator();
        ImGui.TextWrapped(
            "内置北岛魔路按游戏传送面板顺序选择，不依赖客户端语言。"
            + "新增记录时，请填写面板顺序；名称仅用于显示和旧记录回退。"
            + "随后从别处传送到该魔路，选中记录并保存落地坐标。"
        );
        ImGui.SetNextItemWidth(240f);
        ImGui.InputText(
            "魔路名称##IllegalNorthernRouteName",
            ref northernRouteName,
            128
        );
        ImGui.SetNextItemWidth(160f);
        ImGui.InputInt(
            "传送面板顺序（从 1 开始）##IllegalNorthernRouteMenuOrder",
            ref northernRouteMenuOrder
        );
        northernRouteMenuOrder = Math.Max(1, northernRouteMenuOrder);
        ImGui.SetNextItemWidth(160f);
        ImGui.InputInt(
            "旧版 Lifestream 目的地 ID（保留，不再使用）##IllegalNorthernRouteId",
            ref northernRouteDestinationId
        );
        northernRouteDestinationId = Math.Max(0, northernRouteDestinationId);
        if (ImGui.Button("记录当前已共鸣魔路##IllegalNorthernRecordRoute")
            && module.RecordCurrentNorthernRoute(
                northernRouteName,
                northernRouteMenuOrder,
                (uint)northernRouteDestinationId
            ))
        {
            northernRouteName = "";
            northernRouteMenuOrder++;
            northernRouteDestinationId = 0;
        }

        var routes = module.GetNorthernRoutes();
        foreach (var route in routes)
        {
            var selected = selectedNorthernRouteId == route.Id;
            var arrival = route.HasArrival ? "已记录落地" : "缺少落地坐标";
            if (ImGui.Selectable(
                    $"{route.Name} · {arrival}##NorthernRoute_{route.Id}",
                    selected
                ))
            {
                selectedNorthernRouteId = route.Id;
                pendingNorthernRouteDeleteId = null;
            }

            var enabled = route.Enabled;
            if (ImGui.Checkbox(
                    $"用于自动选路##NorthernRouteEnabled_{route.Id}",
                    ref enabled
                ))
            {
                module.SetNorthernRouteEnabled(route.Id, enabled);
            }

            ImGui.SameLine();
            ImGui.TextDisabled(
                $"顺序={route.TeleportMenuOrder}, Base={route.BaseId}, "
                + $"Custom={route.ActiveCustomAetheryteId}, "
                + $"ID={route.LifestreamDestinationId}"
            );
        }

        if (selectedNorthernRouteId is { } selectedId)
        {
            if (ImGui.Button("将当前位置记录为所选魔路落地点")
                && module.RecordNorthernRouteArrival(selectedId))
            {
                pendingNorthernRouteDeleteId = null;
            }

            ImGui.SameLine();
            if (pendingNorthernRouteDeleteId == selectedId)
            {
                if (ImGui.Button("确认删除所选魔路"))
                {
                    module.DeleteNorthernRoute(selectedId);
                    selectedNorthernRouteId = null;
                    pendingNorthernRouteDeleteId = null;
                }
            }
            else if (ImGui.Button("删除所选魔路"))
            {
                pendingNorthernRouteDeleteId = selectedId;
            }
        }

        ImGui.TextDisabled($"JSON: {module.Plugin.NorthernRoutes.Path}");
        ImGui.TreePop();
    }

    private static void DrawActiveNames(string[] names)
    {
        var label = names.Length == 0
            ? "当前：无"
            : $"当前：{string.Join("、", names)}";
        ImGui.TextDisabled(label);
    }

    private static void DrawRecordedCriticalEncounters(AutomatorModule module)
    {
        var recorded = module.GetRecordedCriticalEncounters()
            .Where(entry =>
                Svc.ClientState.TerritoryType != ZoneData.SOUTHHORN
                || !EventData.CriticalEncounters.ContainsKey(entry.Id)
            )
            .ToList();
        var label = ZoneData.IsInNorthernExpedition()
            ? $"北岛 CE ({recorded.Count})##IllegalRecordedCE"
            : $"已实时记录的 CE ({recorded.Count})##IllegalRecordedCE";
        if (recorded.Count == 0
            || !ImGui.TreeNode(label))
        {
            return;
        }

        foreach (var (id, name, configured) in recorded)
        {
            var enabled = configured;
            if (ImGui.Checkbox($"{name}##IllegalRecordedCE_{id}", ref enabled))
            {
                module.SetRecordedCriticalEncounterEnabled(id, enabled);
            }
        }

        ImGui.TreePop();
    }

    private static void DrawRecordedFates(AutomatorModule module)
    {
        var recorded = module.GetRecordedFates()
            .Where(entry =>
                Svc.ClientState.TerritoryType != ZoneData.SOUTHHORN
                || !EventData.Fates.ContainsKey(entry.Id)
            )
            .ToList();
        var label = ZoneData.IsInNorthernExpedition()
            ? $"北岛 FATE ({recorded.Count})##IllegalRecordedFATE"
            : $"已实时记录的 FATE ({recorded.Count})##IllegalRecordedFATE";
        if (recorded.Count == 0
            || !ImGui.TreeNode(label))
        {
            return;
        }

        foreach (var (id, name, configured) in recorded)
        {
            var enabled = configured;
            if (ImGui.Checkbox($"{name}##IllegalRecordedFATE_{id}", ref enabled))
            {
                module.SetRecordedFateEnabled(id, enabled);
            }
        }

        ImGui.TreePop();
    }
}
