using System;
using BOCCHI.Modules.Automator;
using BOCCHI.Modules.Buff;
using BOCCHI.Modules.Carrots;
using BOCCHI.Modules.CriticalEncounters;
using BOCCHI.Modules.Currency;
using BOCCHI.Modules.Data;
using BOCCHI.Modules.DevMap;
using BOCCHI.Modules.EventDrop;
using BOCCHI.Modules.Exp;
using BOCCHI.Modules.Fates;
using BOCCHI.Modules.ForkedTower;
using BOCCHI.Modules.MobFarmer;
using BOCCHI.Modules.Mount;
using BOCCHI.Modules.Pathfinder;
using BOCCHI.Modules.StateManager;
using BOCCHI.Modules.Teleporter;
using BOCCHI.Modules.Telemetry;
using BOCCHI.Modules.Treasure;
using BOCCHI.Modules.WindowManager;
using ECommons.DalamudServices;
using Ocelot;

namespace BOCCHI;

[Serializable]
public class Config : IOcelotConfig
{
    public int Version { get; set; } = 8;

    // Core
    public MountConfig MountConfig { get; set; } = new();

    public TeleporterConfig TeleporterConfig { get; set; } = new();

    public PathfinderConfig PathfinderConfig { get; set; } = new();

    public EventDropConfig EventDropConfig { get; set; } = new();

    public WindowManagerConfig WindowManagerConfig { get; set; } = new();

    public StateManagerConfig StateManagerConfig { get; set; } = new();

    // Functional

    public FatesConfig FatesConfig { get; set; } = new();

    public CriticalEncountersConfig CriticalEncountersConfig { get; set; } = new();

    public ForkedTowerConfig ForkedTowerConfig { get; set; } = new();

    public TreasureConfig TreasureConfig { get; set; } = new();

    public CarrotsConfig CarrotsConfig { get; set; } = new();

    public BuffConfig BuffConfig { get; set; } = new();

    // Trackers
    public CurrencyConfig CurrencyConfig { get; set; } = new();

    public ExpConfig ExpConfig { get; set; } = new();

    // Other
    public MobFarmerConfig MobFarmerConfig { get; set; } = new();

    public AutomatorConfig AutomatorConfig { get; set; } = new();

    public DataConfig DataConfig { get; set; } = new();

    public TelemetryConfig TelemetryConfig { get; set; } = new();

    // Dev map authoring
    public bool DevModeEnabled { get; set; } = true;

    public bool DebugLoggingEnabled { get; set; }

    public uint NorthernExpeditionTerritoryId { get; set; }

    public uint NorthernExpeditionMapId { get; set; }

    public uint ForkedTowerBloodTerritoryId { get; set; }

    public uint ForkedTowerBloodMapId { get; set; }

    public bool ForceForkedTowerBloodTerritory { get; set; }

    public bool ShowForkedTowerEventObjectsOnMap { get; set; } = true;

    public bool ShowUnknownForkedTowerEventObjectsOnMap { get; set; } = true;

    public bool ShowForkedTowerEventObjLabels { get; set; } = true;

    public bool ShowForkedTowerPotentialTrapPositionsOnMap { get; set; } = true;

    public bool ShowForkedTowerTrapGroupLabelsOnMap { get; set; } = true;

    public DevMapMarkerVisibility DevMapVisibleMarkers { get; set; } =
        DevMapMarkerVisibility.All;

    public void Save()
    {
        Svc.PluginInterface.SavePluginConfig(this);
    }
}
