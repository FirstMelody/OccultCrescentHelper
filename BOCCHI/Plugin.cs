using System;
using BOCCHI.Chains;
using BOCCHI.Data;
using BOCCHI.Modules.NorthernRoutes;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using ECommons;
using ECommons.DalamudServices;
using Ocelot;
using Ocelot.Chain;

namespace BOCCHI;

public sealed class Plugin : OcelotPlugin
{
    public override string Name
    {
        get => "Occult Crescent Helper";
    }

    public Config Config { get; }

    public NorthernRouteStore NorthernRoutes { get; }

    public override IOcelotConfig OcelotConfig
    {
        get => Config;
    }

    public static ChainQueue Chain
    {
        get => ChainManager.Get("OCH##main");
    }

    public Plugin(IDalamudPluginInterface plugin)
        : base(plugin, Module.DalamudReflector)
    {
        Config = plugin.GetPluginConfig() as Config ?? new Config();
        NorthernRoutes = new NorthernRouteStore(plugin.ConfigDirectory.FullName);
        var configChanged = false;
        if (Config.Version < 2)
        {
            Config.DevModeEnabled = true;
            Config.Version = 2;
            configChanged = true;
        }

        if (Config.Version < 3)
        {
            Config.ForkedTowerConfig ??= new();
            Config.ForkedTowerConfig.DrawPotentialTrapPositions = true;
            Config.Version = 3;
            configChanged = true;
        }

        if (Config.Version < 4)
        {
            Config.TelemetryConfig ??= new();
            Config.Version = 4;
            configChanged = true;
        }

        if (Config.Version < 5)
        {
            Config.DebugLoggingEnabled = false;
            Config.Version = 5;
            configChanged = true;
        }

        if (Config.Version < 6)
        {
            // Older builds enabled BossMod control by default. Keep automation
            // independent from the user's combat-AI mode unless they opt in.
            Config.AutomatorConfig ??= new();
            Config.AutomatorConfig.ToggleAiProvider = false;
            Config.Version = 6;
            configChanged = true;
        }

        if (Config.Version < 7)
        {
            Config.DevMapVisibleMarkers |=
                global::BOCCHI.Modules.DevMap.DevMapMarkerVisibility.InvestigationLocation
                | global::BOCCHI.Modules.DevMap.DevMapMarkerVisibility.Monster;
            Config.Version = 7;
            configChanged = true;
        }

        if (configChanged)
        {
            plugin.SavePluginConfig(Config);
        }

        ZoneData.Initialize(Config);
        DebugLog.Initialize(Config);

        SetupLanguage(plugin);

        OcelotInitialize(OcelotFeature.All);

        ChainHelper.Initialize(this);
    }

    private void SetupLanguage(IDalamudPluginInterface plugin)
    {
        I18N.SetDirectory(plugin.AssemblyLocation.Directory?.FullName!);
        I18N.LoadAllFromDirectory("en", "Translations/en");
        I18N.LoadAllFromDirectory("jp", "Translations/jp");
        I18N.LoadAllFromDirectory("fr", "Translations/fr");
#if DALAMUD_CN
        I18N.LoadAllFromDirectory("zh", "Translations/zh");
#endif

        // @todo: Breakup German and uwu translation
        I18N.LoadFromFile("de", "Translations/de.json");
        I18N.LoadFromFile("uwu", "Translations/uwu.json");

        var lang = Svc.ClientState.ClientLanguage switch
        {
            ClientLanguage.French => "fr",
            ClientLanguage.German => "de",
            ClientLanguage.Japanese => "jp",
#if DALAMUD_CN
            ClientLanguage.ChineseSimplified => "zh",
#endif
            _ => "en",
        };

        I18N.SetLanguage(lang);

        var today = DateTime.Today;
        if (today is { Month: 4, Day: 1 } && Random.Shared.NextDouble() < 0.05)
        {
            I18N.SetLanguage("uwu");
        }
    }

    protected override bool ShouldUpdate()
    {
        return ZoneData.IsInOccultCrescent()
               && !(
                   Svc.Condition[ConditionFlag.BetweenAreas] ||
                   Svc.Condition[ConditionFlag.BetweenAreas51] ||
                   Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
                   Svc.Condition[ConditionFlag.OccupiedInEvent] ||
                   Svc.Condition[ConditionFlag.WatchingCutscene] ||
                   Svc.Condition[ConditionFlag.WatchingCutscene78] ||
                   Svc.Objects.LocalPlayer?.IsTargetable != true
               );
    }
}
