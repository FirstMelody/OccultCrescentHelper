using System.Linq;
using BOCCHI.Modules.MobFarmer.States;
using Dalamud.Bindings.ImGui;
using Ocelot;
using Ocelot.Ui;

namespace BOCCHI.Modules.MobFarmer;

public class Panel
{
    public void Draw(MobFarmerModule module)
    {
        OcelotUi.Title("自动刷怪：");
        OcelotUi.Indent(() =>
        {
            if (ImGui.Button(module.Farmer.Running ? I18N.T("generic.label.stop") : I18N.T("generic.label.start")))
            {
                module.Farmer.Toggle(module);
            }

            if (module.Farmer.Running)
            {
                var phase = module.Farmer.StateMachine.State switch
                {
                    FarmerPhase.Waiting => "等待",
                    FarmerPhase.Buffing => "使用增益",
                    FarmerPhase.Gathering => "聚集怪物",
                    FarmerPhase.Stacking => "集中怪物",
                    FarmerPhase.Fighting => "战斗",
                    _ => "未知",
                };
                OcelotUi.LabelledValue("阶段", phase);
            }

            OcelotUi.LabelledValue("未接战", module.Scanner.NotInCombat.Count());
            OcelotUi.LabelledValue("已接战", module.Scanner.InCombat.Count());
        });
    }
}
