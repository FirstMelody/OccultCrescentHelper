using Dalamud.Bindings.ImGui;
using Ocelot.Ui;
using Ocelot.Chain;

namespace BOCCHI.Modules.Debug.Panels;

public class ChainManagerPanel : Panel
{
    public override string GetName()
    {
        return "任务链管理器";
    }

    public override void Render(DebugModule module)
    {
        OcelotUi.Title("任务链管理器：");
        OcelotUi.Indent(() =>
        {
            var instances = ChainManager.Queues;
            OcelotUi.Title("实例数量：");
            ImGui.SameLine();
            ImGui.TextUnformatted(instances.Count.ToString());

            foreach (var pair in instances)
            {
                if (pair.Value.CurrentChain == null)
                {
                    continue;
                }

                OcelotUi.Title($"{pair.Key}:");
                OcelotUi.Indent(() =>
                {
                    var current = pair.Value.CurrentChain!;
                    OcelotUi.Title("当前任务链：");
                    ImGui.SameLine();
                    ImGui.TextUnformatted(current.Name);

                    OcelotUi.Title("进度：");
                    ImGui.SameLine();
                    ImGui.TextUnformatted($"{current.Progress * 100}%");

                    OcelotUi.Title("排队中的任务链：");
                    ImGui.SameLine();
                    ImGui.TextUnformatted(pair.Value.QueueCount.ToString());
                });
            }
        });
    }
}
