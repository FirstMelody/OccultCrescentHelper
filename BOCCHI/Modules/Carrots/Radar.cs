using BOCCHI.Data;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using Ocelot.Windows;

namespace BOCCHI.Modules.Carrots;

public class Radar
{
    public void Draw(RenderContext context)
    {
        var isSupportedOutdoorTerritory = ZoneData.IsInSouthHorn()
                                          || ZoneData.IsInNorthernExpedition();
        if (!isSupportedOutdoorTerritory
            || ZoneData.IsInForkedTower()
            || Svc.Condition[ConditionFlag.InCombat])
        {
            return;
        }

        if (!context.IsForModule<CarrotsModule>(out var module))
        {
            return;
        }

        if (!module.Config.ShouldDrawLineToCarrots)
        {
            return;
        }

        if (Svc.Objects.LocalPlayer == null || Svc.Condition[ConditionFlag.InCombat])
        {
            return;
        }

        foreach (var carrot in module.carrots)
        {
            if (!carrot.IsValid())
            {
                continue;
            }

            context.DrawLine(carrot.GetPosition(), Carrot.Color);
        }
    }
}
