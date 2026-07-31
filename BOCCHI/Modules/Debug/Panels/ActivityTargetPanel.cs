using System;
using System.Collections.Generic;
using System.Linq;
using BOCCHI.Enums;
using BOCCHI.Modules.Automator;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using Ocelot.Ui;

namespace BOCCHI.Modules.Debug.Panels;

public class ActivityTargetPanel : Panel
{
    private List<IGameObject> enemies = [];

    public override string GetName()
    {
        return "活动目标";
    }

    public override unsafe void Render(DebugModule module)
    {
        OcelotUi.Indent(() =>
        {
            if (EzThrottler.Throttle("ActivityTargetPanel", 1000))
            {
                enemies = GetEnemies();
            }

            foreach (var enemy in enemies)
            {
                ImGui.TextUnformatted(enemy.Name.ToString());
                OcelotUi.Indent(() =>
                {
                    OcelotUi.LabelledValue("对象类型", enemy.ObjectKind);
                    OcelotUi.LabelledValue("可选中", enemy.IsTargetable ? "是" : "否");
                    OcelotUi.LabelledValue("存活", enemy.IsDead ? "否" : "是");
                    OcelotUi.LabelledValue("属于当前活动目标", IsActivityTarget(enemy, module) ? "是" : "否");
                });
            }
        });
    }

    private List<IGameObject> GetEnemies()
    {
        return Svc.Objects
            .Where(o => o != null && Player.DistanceTo(o) <= 50f && o.ObjectKind == ObjectKind.BattleNpc)
            .OrderBy(o => o.IsTargetable ? 0 : 1)
            .ToList();
    }

    private unsafe bool IsActivityTarget(IGameObject? obj, DebugModule module)
    {
        if (obj == null)
        {
            return false;
        }

        try
        {
            var battleChara = (BattleChara*)obj.Address;

            var id = battleChara->EventId.EntryId;
            var count = Svc.Data.GetExcelSheet<DynamicEvent>().Count();

            var activity = module.GetModule<AutomatorModule>().automator.Activity;
            if (activity != null)
            {
                var data = activity.data;
                if (data.Type == EventType.Fate)
                {
                    return battleChara->FateId == data!.Id;
                }
            }


            var isRelatedToCurrentEvent = battleChara->EventId.EntryId == Player.BattleChara->EventId.EntryId;

            return obj.SubKind == (byte)BattleNpcSubKind.Combatant && isRelatedToCurrentEvent;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex.Message);
            return false;
        }
    }
}
