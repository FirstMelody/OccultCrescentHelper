using System;
using System.Collections.Generic;
using BOCCHI.Data;
using Dalamud.Bindings.ImGui;
using ECommons.DalamudServices;
using Ocelot.Modules;
using Ocelot.Windows;
using Pictomancy;

namespace BOCCHI.Modules.Carrots;

[OcelotModule(1004, 2)]
public class CarrotsModule(Plugin plugin, Config config) : Module(plugin, config)
{
    public override CarrotsConfig Config
    {
        get => PluginConfig.CarrotsConfig;
    }

    public override bool ShouldUpdate
    {
        get => true;
    }

    public override bool ShouldInitialize
    {
        get => true;
    }

    public override bool IsEnabled
    {
        get => Config.IsPropertyEnabled(nameof(Config.Enabled));
    }

    private readonly CarrotsTracker tracker = new();

    private CarrotHunt hunter = null!;

    public List<Carrot> carrots
    {
        get => tracker.carrots;
    }

    private readonly Panel panel = new();

    private readonly Radar radar = new();

    private DateTime nextNorthernTrackerScanAt = DateTime.MinValue;

    public override void PostInitialize()
    {
        hunter = new CarrotHunt(this);
        Svc.PluginInterface.UiBuilder.Draw += DrawNorthernRadar;
    }

    public override void Update(UpdateContext context)
    {
        tracker.Tick(context.Framework);
        hunter.Update();
    }

    public override void Render(RenderContext context)
    {
        // North uses an independent late UiBuilder callback below. This keeps
        // its read-only radar outside South's module update/render conditions.
        if (!ZoneData.IsInNorthernExpedition())
        {
            radar.Draw(context.ForModule(this));
        }
    }

    private void DrawNorthernRadar()
    {
        if (!WorldObjectScanGuard.IsSafe()
            || !ZoneData.IsInNorthernExpedition()
            || ZoneData.IsInForkedTower()
            || !Config.ShouldDrawLineToCarrots
            || Svc.Objects.LocalPlayer is not { } player)
        {
            return;
        }

        if (DateTime.UtcNow >= nextNorthernTrackerScanAt)
        {
            nextNorthernTrackerScanAt = DateTime.UtcNow.AddMilliseconds(250);
            tracker.Tick(Svc.Framework);
        }

        try
        {
            var drawList = PctService.GetDrawList();
            var color = ImGui.GetColorU32(Carrot.Color);
            foreach (var carrot in tracker.carrots)
            {
                if (carrot.IsValid())
                {
                    drawList.AddLine(
                        player.Position,
                        carrot.GetPosition(),
                        1f / 1000f,
                        color,
                        3f
                    );
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Pictomancy can skip a frame while its swap chain is rebuilding.
        }
    }

    public override bool RenderMainUi(RenderContext context)
    {
        panel.Draw(this);

        if (Config.ShouldEnableCarrotHunt)
        {
            hunter.Draw(this);
        }

        return true;
    }

    public override void Dispose()
    {
        Svc.PluginInterface.UiBuilder.Draw -= DrawNorthernRadar;
        base.Dispose();
    }
}
