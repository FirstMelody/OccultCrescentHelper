using System.Numerics;
using Dalamud.Bindings.ImGui;
using Ocelot.Ui;
using Ocelot.IPC;

namespace BOCCHI.Modules.Debug.Panels;

public class VnavmeshPanel : Panel
{
    public override string GetName()
    {
        return "导航网格";
    }

    public override void Render(DebugModule module)
    {
        if (module.TryGetIPCSubscriber<VNavmesh>(out var vnav) && vnav!.IsReady())
        {
            OcelotUi.Title("导航状态：");
            ImGui.SameLine();
            ImGui.TextUnformatted(vnav.IsRunning() ? "运行中" : "待机");


            if (ImGui.Button("测试导航"))
            {
                vnav.FollowPath([new Vector3(815.2f, 72.5f, -705.15f)], false);
            }
        }
    }
}
