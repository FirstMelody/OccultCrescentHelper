using BOCCHI.Data;
using Dalamud.Bindings.ImGui;
using Ocelot.Ui;

namespace BOCCHI.Modules.ForkedTower;

public class Panel
{
    public void Draw(ForkedTowerModule module)
    {
        if (!ZoneData.IsInForkedTower())
        {
            return;
        }

        OcelotUi.Title("两歧塔：");
        OcelotUi.Indent(() =>
        {
            var state = OcelotUi.LabelledValue("本次塔次编号", module.TowerRun.Hash);
            if (state == UiState.Hovered)
            {
                ImGui.SetTooltip("此编号仅用于区分你本地记录的不同塔次。");
            }
        });
    }
}
