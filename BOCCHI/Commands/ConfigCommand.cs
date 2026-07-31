using System.Collections.Generic;
using Ocelot.Commands;
using Ocelot.Modules;

namespace BOCCHI.Commands;

[OcelotCommand]
public class ConfigCommand(Plugin plugin) : OcelotCommand
{
    protected override string Command
    {
        get => "/bocchicfg";
    }

    protected override string Description
    {
        get => @"
打开 BOCCHI 新月岛辅助设置界面
 - /bocchicfg：打开设置界面
--------------------------------
".Trim();
    }

    protected override IReadOnlyList<string> Aliases
    {
        get => ["/bocchic", "/ochcfg", "/ochc", "/occultcrescenthelperconfig"];
    }


    public override void Execute(string command, string arguments)
    {
        plugin.Windows.ToggleConfigUI();
    }
}
