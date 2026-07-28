using System;
using System.Linq;
using BOCCHI.Data;
using BOCCHI.Modules.Teleporter;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Ocelot.Ui;

namespace BOCCHI.Modules.Fates;

public class Panel
{
    public void Draw(FatesModule module)
    {
        OcelotUi.Title($"{module.T("panel.title")}:");
        OcelotUi.Indent(() =>
        {
            DrawPotTracker(module);

            if (module.tracker.Fates.Count <= 0)
            {
                ImGui.TextUnformatted(module.T("panel.none"));
                return;
            }

            foreach (var fate in module.fates.Values)
            {
                if (!ZoneData.IsInOccultCrescent())
                {
                    module.fates.Clear();
                    return;
                }

                try
                {
                    ImGui.TextUnformatted($"{fate.Name} ({fate.CurrentProgress}%)");
                }
                catch (AccessViolationException)
                {
                    continue;
                }


                var estimate = fate.Progress.EstimateTimeToCompletion();
                if (estimate != null)
                {
                    ImGui.SameLine();
                    ImGui.TextUnformatted($"({module.T("panel.estimated")} {estimate.Value:mm\\:ss})");
                }


                if (module.TryGetModule<TeleporterModule>(out var teleporter) && teleporter!.IsReady())
                {
                    teleporter.teleporter.Button(fate.Data.Aethernet, fate.StartPosition, fate.Name, $"fate_{fate.Id}", fate.Data);
                }

                OcelotUi.Indent(() => EventIconRenderer.Drops(fate.Data, module.PluginConfig.EventDropConfig));

                if (!fate.Equals(module.fates.Values.Last()))
                {
                    OcelotUi.VSpace();
                }
            }
        });
    }

    private static void DrawPotTracker(FatesModule module)
    {
        var snapshot = module.PotTracker.Snapshot;
        if (!snapshot.IsSupportedTerritory)
        {
            return;
        }

        ImGui.TextColored(
            ImGuiColors.DalamudOrange,
            module.T("panel.pot_tracker.title")
        );
        if (snapshot.IsActive)
        {
            ImGui.TextColored(
                ImGuiColors.HealerGreen,
                string.Format(module.T("panel.pot_tracker.active"), snapshot.FateName)
            );
        }
        else if (!snapshot.HasHistory)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                module.T("panel.pot_tracker.unknown")
            );
        }
        else if (snapshot.Remaining > TimeSpan.Zero)
        {
            ImGui.TextUnformatted(
                string.Format(
                    module.T("panel.pot_tracker.next"),
                    snapshot.FateName,
                    snapshot.Remaining.ToString(@"mm\:ss")
                )
            );
        }
        else
        {
            ImGui.TextColored(
                ImGuiColors.ParsedGold,
                string.Format(module.T("panel.pot_tracker.ready"), snapshot.FateName)
            );
        }

        OcelotUi.VSpace();
    }
}
