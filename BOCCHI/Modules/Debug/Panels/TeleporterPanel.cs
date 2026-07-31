using System.Linq;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Modules.Teleporter;
using Dalamud.Bindings.ImGui;
using Ocelot.Ui;

namespace BOCCHI.Modules.Debug.Panels;

public class TeleporterPanel : Panel
{
    public override string GetName()
    {
        return "传送";
    }

    public override void Render(DebugModule module)
    {
        if (module.TryGetModule<TeleporterModule>(out var teleporter) && teleporter!.IsReady())
        {
            OcelotUi.Title("传送：");
            OcelotUi.Indent(() =>
            {
                var shards = ZoneData.GetNearbyAethernetShards();
                if (shards.Count > 0)
                {
                    OcelotUi.Title("附近魔路节点：");
                    OcelotUi.Indent(() =>
                    {
                        foreach (var shard in ZoneData.GetNearbyAethernetShards())
                        {
                            var data = AethernetData.All().First(o => o.BaseId == shard.BaseId);
                            ImGui.TextUnformatted(data.Aethernet.ToFriendlyString());
                        }
                    });
                }

                if (ImGui.Button("测试返回"))
                {
                    teleporter.teleporter.Return();
                }
            });
        }
    }
}
