using System;
using System.Linq;
using BOCCHI.Data;
using Dalamud.Bindings.ImGui;
using ECommons.DalamudServices;
using Ocelot.Ui;

namespace BOCCHI.Modules.Automator;

public class Panel
{
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
