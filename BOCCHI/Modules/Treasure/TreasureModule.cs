using System;
using System.Collections.Generic;
using System.Numerics;
using BOCCHI.Data;
using Ocelot.Modules;
using Ocelot.Windows;

namespace BOCCHI.Modules.Treasure;

[OcelotModule(1003, 1)]
public class TreasureModule(Plugin _plugin, Config config) : Module(_plugin, config)
{
    public override TreasureConfig Config
    {
        get => PluginConfig.TreasureConfig;
    }

    public override bool ShouldInitialize
    {
        get => true;
    }

    public override bool IsEnabled
    {
        get => Config.IsPropertyEnabled(nameof(Config.Enabled));
    }

    public readonly static Vector4 Bronze = new(0.804f, 0.498f, 0.196f, 1f);

    public readonly static Vector4 Silver = new(0.753f, 0.753f, 0.753f, 1f);

    public readonly static Vector4 Unknown = new(0.6f, 0.2f, 0.8f, 1f);

    public readonly TreasureTracker Tracker = new();

    private TreasureHunt hunter = null!;

    public List<Treasure> Treasures
    {
        get => Tracker.Treasures;
    }

    private readonly Panel panel = new();

    private readonly Radar radar = new();

    private DateTime nextNorthernTrackerScanAt = DateTime.MinValue;

    public override void PostInitialize()
    {
        hunter = new TreasureHunt(this);
    }

    public override void Update(UpdateContext context)
    {
        Tracker.Tick(Plugin);
        hunter.Update();
    }

    public override void Render(RenderContext context)
    {
        // Plugin.ShouldUpdate() deliberately remains South-only so South automation
        // cannot run in North. Keep this small, read-only tracker alive independently
        // so the North chest radar still receives current game objects.
        if (ZoneData.IsInNorthernExpedition()
            && !ZoneData.IsInForkedTower()
            && DateTime.UtcNow >= nextNorthernTrackerScanAt)
        {
            nextNorthernTrackerScanAt = DateTime.UtcNow.AddMilliseconds(250);
            Tracker.Tick(Plugin);
        }

        radar.Draw(context.ForModule(this));
    }

    public override bool RenderMainUi(RenderContext context)
    {
        panel.Draw(this);

        if (Config.ShouldEnableTreasureHunt)
        {
            hunter.Draw(this);
        }

        return true;
    }
}
