using System.Collections.Generic;
using System.Linq;
using BOCCHI.Modules.Debug;
using BOCCHI.Data;
using BOCCHI.Modules.ForkedTower;
using BOCCHI.Modules.Telemetry;
using ECommons;
using ECommons.DalamudServices;
using Ocelot;
using Ocelot.Commands;
using Ocelot.Modules;

namespace BOCCHI.Commands;

[OcelotCommand]
public class MainCommand(Plugin plugin) : OcelotCommand
{
    protected override string Command
    {
        get => "/bocchi";
    }

    protected override string Description
    {
        get => @"
Opens Occult Crescent Helper main ui
 - /bocchi : Opens the main ui
 - /bocchi config : opens the config ui
 - /bocchi cfg : opens the config ui
 - /bocchi dev : toggles dev map authoring mode
 - /bocchi dev bind : binds the current territory as Northern Expedition
 - /bocchi dev tower : binds and forces the current territory as Forked Tower: Blood
 - /bocchi dev tower-auto : stops forcing Tower detection and uses status detection
 - /bocchi telemetry [on|off|status] : anonymous map telemetry
--------------------------------
".Trim();
    }

    protected override IReadOnlyList<string> Aliases
    {
        get => ["/och", "/occultcrescenthelper"];
    }

    private readonly IReadOnlyList<string> languageCodes =
    [
        "en", "de", "fr", "jp", "uwu",
    ];

    public override void Execute(string command, string arguments)
    {
        arguments = arguments.Trim();

        if (arguments is "config" or "cfg")
        {
            plugin.Windows.ToggleConfigUI();
            return;
        }

#if DEBUG_BUILD
        if (arguments == "debug")
        {
            plugin.Windows.GetWindow<DebugWindow>().Toggle();
            return;
        }
#endif

        if (arguments == "buff")
        {
            new BuffCommand(plugin).Execute("/bocchibuff", "");
            return;
        }

        if (arguments.StartsWith("tp"))
        {
            new TeleportCommand(plugin).Execute("/bocchitp", arguments.ReplaceFirst("tp", "").Trim());
            return;
        }

        if (arguments.StartsWith("language"))
        {
            var parts = arguments.Split(' ', 2);
            if (parts.Length == 2)
            {
                var code = parts[1].Trim().ToLowerInvariant();
                if (languageCodes.Contains(code))
                {
                    I18N.SetLanguage(code);
                    Svc.Chat.Print($"Language set to: {code}");
                    return;
                }

                Svc.Log.Error($"Unknown language code: {code}");
                return;
            }

            Svc.Chat.Print("Usage: /bocchi language <code>");
            return;
        }

        if (arguments == "dev" || arguments.StartsWith("dev "))
        {
            ExecuteDevCommand(arguments);
            return;
        }

        if (arguments == "telemetry" || arguments.StartsWith("telemetry "))
        {
            ExecuteTelemetryCommand(arguments);
            return;
        }

        plugin.Windows.ToggleMainUI();
    }

    private void ExecuteTelemetryCommand(string arguments)
    {
        var subcommand = arguments.Length > 9 ? arguments[9..].Trim().ToLowerInvariant() : "status";
        var module = plugin.Modules.GetModule<TelemetryModule>();
        switch (subcommand)
        {
            case "on":
                module.SetEnabled(true);
                break;
            case "off":
                module.SetEnabled(false);
                break;
            case "":
            case "status":
                Svc.Chat.Print($"[BOCCHI] 匿名地图遥测：{module.GetStatus()}");
                break;
            default:
                Svc.Chat.Print("[BOCCHI] 用法：/bocchi telemetry [on|off|status]");
                break;
        }
    }

    private void ExecuteDevCommand(string arguments)
    {
        var subcommand = arguments.Length > 3 ? arguments[3..].Trim().ToLowerInvariant() : "";

        if (subcommand is "bind" or "force" or "north" or "北征")
        {
            var territoryId = Svc.ClientState.TerritoryType;
            var mapId = Svc.ClientState.MapId;
            if (territoryId == 0 || mapId == 0)
            {
                Svc.Chat.Print("[BOCCHI] 当前 Territory/Map 尚不可用，请进入目标区域后重试。");
                return;
            }

            plugin.Config.NorthernExpeditionTerritoryId = territoryId;
            plugin.Config.NorthernExpeditionMapId = mapId;
            plugin.Config.DevModeEnabled = true;
            plugin.Config.Save();
            plugin.Windows.OpenMainUI();

            Svc.Chat.Print(
                $"[BOCCHI] 已将当前区域记录为“北征之章”：Territory={territoryId}, Map={mapId}。dev 模式已启用。"
            );
            return;
        }

        if (subcommand is "tower" or "tower-blood" or "forked-tower")
        {
            var territoryId = Svc.ClientState.TerritoryType;
            var mapId = Svc.ClientState.MapId;
            if (territoryId == 0 || mapId == 0)
            {
                Svc.Chat.Print("[BOCCHI] 当前 Territory/Map 尚不可用，请进入目标区域后重试。");
                return;
            }

            plugin.Config.ForkedTowerBloodTerritoryId = territoryId;
            plugin.Config.ForkedTowerBloodMapId = mapId;
            plugin.Config.ForceForkedTowerBloodTerritory = true;
            plugin.Config.DevModeEnabled = true;
            plugin.Config.Save();
            plugin.Modules.GetModule<ForkedTowerModule>().StartNewRun();
            plugin.Windows.OpenMainUI();

            Svc.Chat.Print(
                $"[BOCCHI] 已将当前区域强制记录为 Forked Tower: Blood：Territory={territoryId}, Map={mapId}。"
            );
            return;
        }

        if (subcommand is "tower-auto" or "tower-off")
        {
            plugin.Config.ForceForkedTowerBloodTerritory = false;
            plugin.Config.Save();
            Svc.Chat.Print("[BOCCHI] 已关闭强制 Tower 判定，保留绑定并恢复状态检测。");
            return;
        }

        if (subcommand == "on")
        {
            plugin.Config.DevModeEnabled = true;
        }
        else if (subcommand == "off")
        {
            plugin.Config.DevModeEnabled = false;
        }
        else if (subcommand.Length == 0)
        {
            plugin.Config.DevModeEnabled = !plugin.Config.DevModeEnabled;
        }
        else
        {
            Svc.Chat.Print("[BOCCHI] 用法：/bocchi dev [on|off|bind|tower|tower-auto]");
            return;
        }

        plugin.Config.Save();
        Svc.Chat.Print($"[BOCCHI] dev 模式已{(plugin.Config.DevModeEnabled ? "启用" : "关闭")}。");
    }
}
