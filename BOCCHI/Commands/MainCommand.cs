using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BOCCHI.Modules.Debug;
using BOCCHI.Data;
using BOCCHI.Modules.ForkedTower;
using BOCCHI.Modules.Telemetry;
using Dalamud.Game.ClientState.Objects.Enums;
using ECommons;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
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
打开 BOCCHI 新月岛辅助主界面
 - /bocchi：打开主界面
 - /bocchi config：打开设置界面
 - /bocchi cfg：打开设置界面
 - /bocchi dev：切换开发者地图采集模式
 - /bocchi dev bind：将当前区域绑定为北征之章
 - /bocchi dev tower：将当前区域绑定并强制识别为两歧塔 血之塔
 - /bocchi dev tower-auto：停止强制识别，改用塔内状态自动判断
 - /bocchi dev route [名称]：记录当前选中的北岛魔路及到达位置
 - /bocchi debug-log [on|off|status]：管理调试日志（默认关闭）
 - /bocchi telemetry [on|off|status]：管理匿名地图资料共享
--------------------------------
".Trim();
    }

    protected override IReadOnlyList<string> Aliases
    {
        get => ["/och", "/occultcrescenthelper"];
    }

    private readonly IReadOnlyList<string> languageCodes =
    [
        "en", "de", "fr", "jp", "zh", "uwu",
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
                    Svc.Chat.Print($"[BOCCHI] 语言已切换为：{code}");
                    return;
                }

                Svc.Log.Error($"未知语言代码：{code}");
                return;
            }

            Svc.Chat.Print("[BOCCHI] 用法：/bocchi language <语言代码>");
            return;
        }

        if (arguments == "dev" || arguments.StartsWith("dev "))
        {
            ExecuteDevCommand(arguments);
            return;
        }

        if (arguments == "debug-log" || arguments.StartsWith("debug-log "))
        {
            ExecuteDebugLogCommand(arguments);
            return;
        }

        if (arguments == "telemetry" || arguments.StartsWith("telemetry "))
        {
            ExecuteTelemetryCommand(arguments);
            return;
        }

        plugin.Windows.ToggleMainUI();
    }

    private void ExecuteDebugLogCommand(string arguments)
    {
        var subcommand = arguments.Length > 9
            ? arguments[9..].Trim().ToLowerInvariant()
            : "status";
        switch (subcommand)
        {
            case "on":
                plugin.Config.DebugLoggingEnabled = true;
                plugin.Config.Save();
                Svc.Chat.Print("[BOCCHI] 调试日志已开启。");
                break;
            case "off":
                plugin.Config.DebugLoggingEnabled = false;
                plugin.Config.Save();
                Svc.Chat.Print("[BOCCHI] 调试日志已关闭。");
                break;
            case "":
            case "status":
                Svc.Chat.Print(
                    $"[BOCCHI] 调试日志：{(plugin.Config.DebugLoggingEnabled ? "开启" : "关闭")}。"
                );
                break;
            default:
                Svc.Chat.Print("[BOCCHI] 用法：/bocchi debug-log [on|off|status]");
                break;
        }
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
        var rawSubcommand = arguments.Length > 3 ? arguments[3..].Trim() : "";
        var subcommand = rawSubcommand.ToLowerInvariant();

        if (subcommand == "route"
            || subcommand.StartsWith("route ")
            || subcommand == "route-sample"
            || subcommand.StartsWith("route-sample "))
        {
            var prefixLength = subcommand.StartsWith("route-sample")
                ? "route-sample".Length
                : "route".Length;
            var requestedName = rawSubcommand.Length > prefixLength
                ? rawSubcommand[prefixLength..].Trim()
                : "";
            var target = Svc.Targets.Target;
            if (!ZoneData.IsInNorthernExpedition()
                || Svc.Objects.LocalPlayer == null)
            {
                Svc.Chat.PrintError("[BOCCHI] 请在北征之章内采样魔路。");
                return;
            }

            if (target == null
                || target.ObjectKind is not (ObjectKind.EventObj or ObjectKind.Aetheryte)
                || Vector3.Distance(target.Position, Player.Position) > 15f)
            {
                Svc.Chat.PrintError("[BOCCHI] 请站在魔路旁并先选中魔路对象。");
                return;
            }

            var name = string.IsNullOrWhiteSpace(requestedName)
                ? target.Name.ToString()
                : requestedName;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"魔路-{target.BaseId}";
            }

            var route = plugin.NorthernRoutes.RecordRoute(
                Svc.ClientState.TerritoryType,
                Svc.ClientState.MapId,
                name,
                0,
                0,
                0,
                target.BaseId,
                target.Position
            );
            plugin.NorthernRoutes.RecordArrival(route.Id, Player.Position);
            Svc.Chat.Print(
                $"[BOCCHI] 已记录魔路“{route.Name}”："
                + $"对象=({target.Position.X:F3}, {target.Position.Y:F3}, {target.Position.Z:F3})，"
                + $"落点=({Player.Position.X:F3}, {Player.Position.Y:F3}, {Player.Position.Z:F3})，"
                + $"基础编号={target.BaseId}。"
            );
            Svc.Log.Information(
                $"North route sampled by command: name={route.Name}, "
                + $"base={target.BaseId}, "
                + $"interaction=({target.Position.X:F3}, {target.Position.Y:F3}, {target.Position.Z:F3}), "
                + $"arrival=({Player.Position.X:F3}, {Player.Position.Y:F3}, {Player.Position.Z:F3})"
            );
            return;
        }

        if (subcommand is "bind" or "force" or "north" or "北征")
        {
            var territoryId = Svc.ClientState.TerritoryType;
            var mapId = Svc.ClientState.MapId;
            if (territoryId == 0 || mapId == 0)
            {
                Svc.Chat.Print("[BOCCHI] 当前区域编号或地图编号尚不可用，请进入目标区域后重试。");
                return;
            }

            plugin.Config.NorthernExpeditionTerritoryId = territoryId;
            plugin.Config.NorthernExpeditionMapId = mapId;
            plugin.Config.DevModeEnabled = true;
            plugin.Config.Save();
            plugin.Windows.OpenMainUI();

            Svc.Chat.Print(
                $"[BOCCHI] 已将当前区域记录为“北征之章”：区域编号={territoryId}，地图编号={mapId}。开发者模式已启用。"
            );
            return;
        }

        if (subcommand is "tower" or "tower-blood" or "forked-tower")
        {
            var territoryId = Svc.ClientState.TerritoryType;
            var mapId = Svc.ClientState.MapId;
            if (territoryId == 0 || mapId == 0)
            {
                Svc.Chat.Print("[BOCCHI] 当前区域编号或地图编号尚不可用，请进入目标区域后重试。");
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
                $"[BOCCHI] 已将当前区域强制记录为“两歧塔 血之塔”：区域编号={territoryId}，地图编号={mapId}。"
            );
            return;
        }

        if (subcommand is "tower-auto" or "tower-off")
        {
            plugin.Config.ForceForkedTowerBloodTerritory = false;
            plugin.Config.Save();
            Svc.Chat.Print("[BOCCHI] 已关闭强制塔区判定，保留绑定并恢复状态检测。");
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
            Svc.Chat.Print("[BOCCHI] 用法：/bocchi dev [on|off|bind|tower|tower-auto|route 名称]");
            return;
        }

        plugin.Config.Save();
        Svc.Chat.Print($"[BOCCHI] 开发者模式已{(plugin.Config.DevModeEnabled ? "启用" : "关闭")}。");
    }
}
