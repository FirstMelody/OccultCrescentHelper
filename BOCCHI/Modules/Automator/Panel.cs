using System;
using System.Linq;
using BOCCHI.Modules.NorthernRoutes;
using BOCCHI.Data;
using Dalamud.Bindings.ImGui;
using ECommons.DalamudServices;
using Ocelot.Ui;

namespace BOCCHI.Modules.Automator;

public class Panel
{
    private string northernRouteName = "";
    private int northernRouteDestinationId;
    private string northernStandbyName = "默认蹲守点";
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
            || !ImGui.TreeNode("北岛魔路与事件后蹲守##IllegalNorthernRoutes"))
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
        if (ImGui.Checkbox("CE/FATE 结束后返回蹲守点", ref returnToStandby))
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
            "先与魔路共鸣并站在魔路旁（最好选中它），填写游戏传送面板"
            + "中的准确名称后记录。随后从别处传送到该魔路，"
            + "选中记录并保存落地坐标。"
        );
        ImGui.SetNextItemWidth(240f);
        ImGui.InputText(
            "魔路名称##IllegalNorthernRouteName",
            ref northernRouteName,
            128
        );
        ImGui.SetNextItemWidth(160f);
        ImGui.InputInt(
            "旧版 Lifestream 目的地 ID（保留，不再使用）##IllegalNorthernRouteId",
            ref northernRouteDestinationId
        );
        northernRouteDestinationId = Math.Max(0, northernRouteDestinationId);
        if (ImGui.Button("记录当前已共鸣魔路##IllegalNorthernRecordRoute")
            && module.RecordCurrentNorthernRoute(
                northernRouteName,
                (uint)northernRouteDestinationId
            ))
        {
            northernRouteName = "";
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
                $"Base={route.BaseId}, Custom={route.ActiveCustomAetheryteId}, "
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

        ImGui.Separator();
        ImGui.SetNextItemWidth(220f);
        ImGui.InputText(
            "蹲守点名称##IllegalNorthernStandbyName",
            ref northernStandbyName,
            128
        );
        if (ImGui.Button("将当前位置设置为事件后蹲守点"))
        {
            module.SetCurrentNorthernStandbyPoint(northernStandbyName);
        }

        var standby = module.GetNorthernStandbyPoint();
        if (standby != null)
        {
            var position = NorthernRouteStore.GetPosition(standby);
            ImGui.TextDisabled(
                $"当前蹲守点：{standby.Name} "
                + $"({position.X:F1}, {position.Y:F1}, {position.Z:F1})"
            );
        }
        else
        {
            ImGui.TextDisabled("尚未设置蹲守点；首个魔路记录后会自动用其位置。");
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
        if (recorded.Count == 0
            || !ImGui.TreeNode($"已实时记录的 CE ({recorded.Count})##IllegalRecordedCE"))
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
        if (recorded.Count == 0
            || !ImGui.TreeNode($"已实时记录的 FATE ({recorded.Count})##IllegalRecordedFATE"))
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
