using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.GameFunctions;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Dalamud.Bindings.ImGui;
using Ocelot.Ui;

namespace BOCCHI.Modules.Debug.Panels;

public class EnemyPanel : Panel
{
    public override string GetName()
    {
        return "附近敌人";
    }

    private List<IGameObject> enemies = [];

    public override unsafe void Render(DebugModule module)
    {
        OcelotUi.Indent(() =>
        {
            foreach (var enemy in enemies)
            {
                if (ImGui.CollapsingHeader($"{enemy.Name} - {enemy.BaseId}##{enemy.ObjectIndex}"))
                {
                    OcelotUi.Indent(() =>
                    {
                        ImGui.Text($"名称：{enemy.Name.TextValue}");
                        ImGui.Text($"游戏对象编号：{enemy.GameObjectId:X}");
                        ImGui.Text($"实体编号：{enemy.EntityId:X}");
                        ImGui.Text($"基础编号：{enemy.BaseId}");
                        ImGui.Text($"所有者编号：{enemy.OwnerId}");
                        ImGui.Text($"对象索引：{enemy.ObjectIndex}");
                        ImGui.Text($"对象类型：{enemy.ObjectKind}");
                        ImGui.Text($"子类型：{enemy.SubKind}");
                        ImGui.Text($"位置：{enemy.Position}");
                        ImGui.Text($"朝向：{enemy.Rotation}");
                        ImGui.Text($"碰撞半径：{enemy.HitboxRadius}");
                        ImGui.Text($"横向距离：{enemy.YalmDistanceX}");
                        ImGui.Text($"纵向距离：{enemy.YalmDistanceZ}");
                        ImGui.Text($"是否死亡：{enemy.IsDead}");
                        ImGui.Text($"是否可选中：{enemy.IsTargetable}");
                        ImGui.Text($"目标对象编号：{enemy.TargetObjectId:X}");

                        if (enemy.TargetObject is { } target)
                        {
                            ImGui.Text($"目标对象：{target.Name.TextValue}（{target.GameObjectId:X}）");
                        }
                        else
                        {
                            ImGui.Text("目标对象：无");
                        }

                        ImGui.Text($"是否有效：{enemy.IsValid()}");
                        ImGui.Text($"内存地址：0x{enemy.Address.ToInt64():X}");


                        var battleChara = (BattleChara*)enemy.Address;


                        ImGui.Text($"布局编号：{battleChara->LayoutId}");
                        ImGui.Text($"等级：{battleChara->ForayInfo.Level}");

                        var distance = Player.DistanceTo(enemy.Position);
                        if (distance <= 30f)
                        {
                            if (ImGui.Button("选中"))
                            {
                                Svc.Targets.Target = enemy;
                            }
                        }
                    });
                }
            }
        });
    }

    public override void Update(DebugModule module)
    {
        if (EzThrottler.Throttle("enemies", 2000))
        {
            // DoThing();
            enemies = Svc.Objects
                .Where(o =>
                    o != null &&
                    o.IsHostile() &&
                    o.IsTargetable &&
                    o.Name.TextValue.Length > 0
                )
                .OrderBy(o => Vector3.Distance(o.Position, Player.Position))
                .ToList();
        }
    }
}
