using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Dalamud.Bindings.ImGui;
using Ocelot.Ui;

namespace BOCCHI.Modules.Debug.Panels;

public class TargetPanel : Panel
{
    public override string GetName()
    {
        return "当前目标";
    }

    public override unsafe void Render(DebugModule module)
    {
        OcelotUi.Indent(() =>
        {
            var target = Svc.Targets.Target;
            if (target == null)
            {
                ImGui.TextUnformatted("当前未选中目标。");
                return;
            }

            // Try to cast to internal GameObject
            var obj = (GameObject*)target.Address;

            if (obj == null)
            {
                ImGui.TextUnformatted("当前目标不是原生游戏对象。");
                return;
            }

            void Draw<T>(string label, T value)
            {
                OcelotUi.Title($"{label}:");
                ImGui.SameLine();
                ImGui.TextUnformatted(value?.ToString() ?? "空");
            }

            Draw("名称", obj->NameString);
            Draw("事件状态", obj->EventState);
            Draw("实体编号", obj->EntityId);
            Draw("布局编号", obj->LayoutId);
            Draw("基础编号", obj->BaseId);
            Draw("所有者编号", obj->OwnerId);
            Draw("对象索引", obj->ObjectIndex);
            Draw("对象类型", obj->ObjectKind);
            Draw("子类型", obj->SubKind);
            Draw("性别", obj->Sex);
            Draw("横向距离", obj->YalmDistanceFromPlayerX);
            Draw("目标状态", obj->TargetStatus);
            Draw("纵向距离", obj->YalmDistanceFromPlayerZ);
            Draw("可选中状态", obj->TargetableStatus);
            Draw("位置", obj->Position);
            Draw("朝向", obj->Rotation);
            Draw("缩放", obj->Scale);
            Draw("高度", obj->Height);
            Draw("特效缩放", obj->VfxScale);
            Draw("碰撞半径", obj->HitboxRadius);
            Draw("绘制偏移", obj->DrawOffset);
            Draw("事件编号", obj->EventId);
            Draw("临危受命编号", obj->FateId);
            Draw("姓名板图标编号", obj->NamePlateIconId);
            Draw("渲染标志", obj->RenderFlags);

            // Pointers and advanced types
            Draw("绘制对象", (ulong)obj->DrawObject);
            Draw("共享组布局实例", (ulong)obj->SharedGroupLayoutInstance);
            Draw("脚本角色", (ulong)obj->LuaActor);
            Draw("事件处理器", (ulong)obj->EventHandler);

            // Virtual methods (callable via vtable)
            Draw("是否可选中", obj->GetIsTargetable());
            Draw("半径", obj->GetRadius());
            Draw("高度（虚函数）", obj->GetHeight());
            Draw("性别（虚函数）", obj->GetSex());
            Draw("是否死亡", obj->IsDead());
            Draw("是否未骑乘", obj->IsNotMounted());
            Draw("是否为角色", obj->IsCharacter());
        });
    }


    // public override void Update(DebugModule module)
    // {
    //     if (EzThrottler.Throttle("enemies", 2000))
    //     {
    //         // DoThing();
    //         enemies = Svc.Objects
    //             .Where(o =>
    //                 o != null &&
    //                 o.IsHostile() &&
    //                 o.IsTargetable &&
    //                 o.Name.TextValue.Length > 0
    //             )
    //             .OrderBy(o => Vector3.Distance(o.Position, Player.Position))
    //             .ToList();
    //     }
    // }
}
