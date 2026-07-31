using System.Numerics;
using BOCCHI.Data;
using BOCCHI.Modules.Automator;
using BOCCHI.Modules.DevMap;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using Ocelot;
using Ocelot.Windows;

namespace BOCCHI.Windows;

[OcelotMainWindow]
public class MainWindow(Plugin primaryPlugin, Config config) : OcelotMainWindow(primaryPlugin, config)
{
    public override void PostInitialize()
    {
        base.PostInitialize();

        TitleBarButtons.Add(new TitleBarButton
        {
            Click = (m) =>
            {
                if (m != ImGuiMouseButton.Left)
                {
                    return;
                }

                primaryPlugin.Config.DevModeEnabled = !primaryPlugin.Config.DevModeEnabled;
                primaryPlugin.Config.Save();
            },
            Icon = FontAwesomeIcon.Code,
            IconOffset = new Vector2(2, 2),
            ShowTooltip = () => ImGui.SetTooltip(
                primaryPlugin.Config.DevModeEnabled ? "关闭开发者地图标注模式" : "开启开发者地图标注模式"
            ),
        });

        TitleBarButtons.Add(new TitleBarButton
        {
            Click = (m) =>
            {
                if (m != ImGuiMouseButton.Left)
                {
                    return;
                }

                Plugin.Modules.GetModule<AutomatorModule>().DisableIllegalMode();
            },
            Icon = FontAwesomeIcon.Stop,
            IconOffset = new Vector2(2, 2),
            ShowTooltip = () => ImGui.SetTooltip(I18N.T("windows.main.buttons.emergency_stop")),
        });

        TitleBarButtons.Add(new TitleBarButton
        {
            Click = (m) =>
            {
                if (m != ImGuiMouseButton.Left)
                {
                    return;
                }

                AutomatorModule.ToggleIllegalMode(Plugin);
            },
            Icon = FontAwesomeIcon.Skull,
            IconOffset = new Vector2(2, 2),
            ShowTooltip = () => ImGui.SetTooltip(I18N.T("windows.main.buttons.toggle_illegal_mode")),
        });
    }

    protected override void Render(RenderContext context)
    {
        if (!ZoneData.IsInPluginTerritory())
        {
            ImGui.TextUnformatted(I18N.T("generic.label.not_in_zone"));
            ImGui.TextWrapped("如当前区域是“蜃景幻界：新月岛 北征之章”，请使用 /bocchi dev bind 强制绑定当前区域。");
            if (ImGui.Button("将当前区域设为两歧塔 血之塔##BindUnknownTower"))
            {
                Plugin.Modules.GetModule<DevMapModule>()
                    .BindCurrentTerritoryAsForkedTowerBlood();
            }

            return;
        }

        if (ZoneData.IsInNorthernExpedition())
        {
            if (Plugin.Modules.TryGetModule<DevMapModule>(out var devMap) && devMap != null)
            {
                devMap.RenderDevUi();
            }

            if (Plugin.Modules.TryGetModule<AutomatorModule>(out var automator)
                && automator != null)
            {
                ImGui.Separator();
                automator.panel.Draw(automator);
            }

            return;
        }

        Plugin.Modules.RenderMainUi(context);
    }
}
